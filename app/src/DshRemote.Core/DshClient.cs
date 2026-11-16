using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DshRemote.Core;

/// <summary>Tunables for <see cref="DshClient"/>. Production callers only need the defaults.</summary>
public sealed class DshClientOptions
{
    /// <summary>Exponential backoff used after the mux socket is lost.</summary>
    public ReconnectPolicy Reconnect { get; init; } = new();

    /// <summary>Optional diagnostic sink (pairing warnings, TLS rejections, reconnect attempts).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Time zone name sent with <c>session/prompt</c>; defaults to the local zone id. Set to
    /// <see cref="string.Empty"/> to omit the field.
    /// </summary>
    public string? ClientTimeZone { get; init; } = TryGetLocalTimeZoneId();

    /// <summary>Time allowed for one HTTP request (the WebSocket is not covered by this timeout).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Snapshot history budget used by the live follow pump. The pump already starts from the caller's
    /// baseline (the history the UI loaded, or the local-cache baseline seeded through
    /// <see cref="ISessionBaselineClient"/>), so a one-message snapshot is enough: it is only the anchor for
    /// the log cut that <c>session/events</c> gap repair uses. Keeping this at the old 80 re-downloaded a
    /// baseline the caller already had. <see cref="ISessionFollowClient.OpenSessionAsync"/> keeps its own
    /// 80-message default for the first-screen open.
    /// </summary>
    public int FollowMaxMessages { get; init; } = 1;

    /// <summary>
    /// How long a new follow consumer waits for a previous subscription slot to drain before reporting
    /// <c>session/follow-busy</c>. The default covers the logical-stream cancel frame.
    /// </summary>
    internal TimeSpan FollowAcquireTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Poll interval used while waiting for a previous follow subscription slot to drain.</summary>
    internal TimeSpan FollowAcquirePollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    // Test seams (InternalsVisibleTo DshRemote.Core.Tests).
    internal IConnectionLauncher? Launcher { get; init; }

    internal Func<HttpMessageHandler>? HttpHandlerFactory { get; init; }

    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    private static string? TryGetLocalTimeZoneId()
    {
        try
        {
            var id = TimeZoneInfo.Local.Id;
            // The official prompt validator only accepts UTC or IANA Area/Location names. Windows reports
            // ids like "China Standard Time", so convert before sending.
            if (id.Contains('/', StringComparison.Ordinal))
            {
                return id;
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && !string.IsNullOrWhiteSpace(iana))
            {
                return iana;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Phone-side client for the official DSH Remote wire: one <c>GET /connect</c> owner credential, unary
/// <c>POST /api/&lt;ns&gt;/&lt;method&gt;</c> calls, and the <c>/api/remote.mux</c> logical-stream WebSocket.
/// The mux socket owns the credential lifetime: when it drops the API Peer is dead, so the client discards
/// the cookie, re-runs <c>/connect</c>, and reopens its streams (exponential backoff with jitter).
/// </summary>
public sealed partial class DshClient
    : IDshClient, ISessionFollowClient, IInteractionClient, IWorkspaceClient, IImageClient, IHistoryPagingClient,
      ISessionSummaryClient, ISessionBaselineClient, IModelClient
{
    private static readonly TimeSpan MinReconnectPause = TimeSpan.FromMilliseconds(1);

    private readonly DshClientOptions _options;
    private readonly IConnectionLauncher _launcher;
    private readonly object _gate = new();

    private DshConnectionState _state = DshConnectionState.Disconnected;
    private DshPairingInfo? _pairing;
    private Uri? _baseUri;
    private HttpClient? _http;
    private LaunchedConnection? _current;
    private MuxConnection? _mux;
    private TaskCompletionSource _connectedSignal = NewSignal();
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisor;
    private Exception? _failure;
    private long _generation;
    private bool _disposed;

    public DshClient()
        : this(new DshClientOptions())
    {
    }

    public DshClient(DshClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _launcher = options.Launcher ?? new HttpConnectionLauncher(options.Log);
    }

    public DshConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public DshPairingInfo? Pairing
    {
        get
        {
            lock (_gate)
            {
                return _pairing;
            }
        }
    }

    public event EventHandler<DshConnectionState>? StateChanged;

    /// <summary>Connects (and waits for the first successful generation). Throws when the very first attempt fails.</summary>
    public async Task ConnectAsync(DshPairingInfo pairing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
        }

        await DisconnectAsync().ConfigureAwait(false);

        HttpClient http;
        Uri baseUri;
        CancellationTokenSource supervisorCts;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            _pairing = pairing;
            baseUri = DshEndpoint.ResolveBaseUri(pairing);
            _baseUri = baseUri;
            _failure = null;
            _generation = 0;
            http = CreateHttpClient(pairing, baseUri);
            _http = http;
            supervisorCts = new CancellationTokenSource();
            _supervisorCts = supervisorCts;
        }

        Transition(DshConnectionState.Connecting, null);
        _supervisor = Task.Run(() => SuperviseAsync(pairing, baseUri, http, supervisorCts.Token), CancellationToken.None);

        var connectStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await WaitForConnectionAsync(-1, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }

        connectStopwatch.Stop();
        DshDiagnostics.Instance.Connect(pairing.Host, pairing.Port, connectStopwatch.ElapsedMilliseconds, 1);
        _options.Log?.Invoke($"connect: {connectStopwatch.ElapsedMilliseconds}ms {pairing.Host}:{pairing.Port}");

        // A UI that already subscribed before this connect keeps receiving waterfall requests.
        RestartInteractionPumpIfNeeded();
    }

    /// <summary>Tears the connection down; safe to call when already disconnected.</summary>
    public async Task DisconnectAsync()
    {
        Task? supervisor;
        lock (_gate)
        {
            supervisor = _supervisor;
            try
            {
                _supervisorCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        if (supervisor is not null)
        {
            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch
            {
                // The supervisor records its own failure.
            }
        }

        MuxConnection? mux;
        HttpClient? http;
        lock (_gate)
        {
            mux = _mux;
            _mux = null;
            _current = null;
            http = _http;
            _http = null;
            _supervisor = null;
            try
            {
                _supervisorCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }

            _supervisorCts = null;
        }

        if (mux is not null)
        {
            await mux.DisposeAsync().ConfigureAwait(false);
        }

        http?.Dispose();
        CancelFollowSubscriptions();
        CancelInteractions();
        Transition(DshConnectionState.Disconnected, null);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var value = await CallAsync("session/list", DshArgs.List(), cancellationToken).ConfigureAwait(false);
        return DshParsing.ParseSessionList(value);
    }

    /// <summary>
    /// Reads one history page. Kept for compatibility: a <c>null</c> <paramref name="beforeSeq"/> delegates to
    /// <see cref="GetLatestHistoryAsync"/>, otherwise to <see cref="GetOlderHistoryAsync"/>.
    /// </summary>
    public Task<SessionHistoryPage> GetHistoryAsync(
        string sessionId,
        long? beforeSeq = null,
        int maxMessages = 80,
        CancellationToken cancellationToken = default) =>
        beforeSeq is null
            ? GetLatestHistoryAsync(sessionId, maxMessages, cancellationToken)
            : GetOlderHistoryAsync(sessionId, beforeSeq.Value, maxMessages, cancellationToken);

    public async Task<SessionRuntimeState> GetStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var value = await CallAsync("session/state", DshArgs.State(sessionId), cancellationToken).ConfigureAwait(false);
        return DshParsing.ParseState(value);
    }

    public Task<string> SendPromptAsync(
        string sessionId,
        string text,
        bool steer = false,
        CancellationToken cancellationToken = default) =>
        SendPromptAsync(sessionId, text, images: null, steer, cancellationToken);

    public async Task<string> SendPromptAsync(
        string sessionId,
        string text,
        IReadOnlyList<DshImageAttachment>? images,
        bool steer = false,
        CancellationToken cancellationToken = default)
    {
        var hasImages = images is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(text) && !hasImages)
        {
            throw new ArgumentException("prompt text or at least one image is required", nameof(text));
        }

        var requestId = Guid.NewGuid().ToString("D");
        var args = DshArgs.PromptWithImages(
            requestId,
            sessionId,
            text,
            images,
            steer ? "steer" : "queue",
            _options.ClientTimeZone);
        var value = await CallAsync("session/prompt", args, cancellationToken).ConfigureAwait(false);
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("accepted", out var accepted) &&
            accepted.ValueKind == JsonValueKind.False)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/prompt was not accepted");
        }

        return requestId;
    }

    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await CallAsync("session/cancel", DshArgs.Cancel(sessionId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens one raw logical stream; each yielded element is the inner <c>item.value</c> frame.
    /// Completes when the stream ends or the connection is lost (no automatic reopen — callers that need
    /// resubscription should use <see cref="ObserveEventsAsync"/>).
    /// </summary>
    public async IAsyncEnumerable<JsonElement> OpenStreamAsync(
        string endpoint,
        JsonElement args,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (mux, _) = await WaitForMuxAsync(-1, cancellationToken).ConfigureAwait(false);
        await foreach (var item in mux.OpenAsync(endpoint, args, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Live <c>session/observeEvent</c> stream. Yields durable events (skipping the opening <c>ready</c>
    /// frame) and transparently reopens the stream after a reconnect, de-duplicating by <c>seq</c>.
    /// </summary>
    public async IAsyncEnumerable<SessionEventRecord> ObserveEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        }

        var args = DshArgs.ObserveEvent(sessionId);
        var lastSeq = -1L;
        var generation = -1L;
        while (!cancellationToken.IsCancellationRequested)
        {
            MuxConnection mux;
            try
            {
                (mux, generation) = await WaitForMuxAsync(generation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var enumerator = mux.OpenAsync(MuxWire.ObserveEventEndpoint, args, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }
                    catch (Exception)
                    {
                        // The connection generation died; the outer loop waits for the next one.
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    if (!DshParsing.TryParseObserveValue(enumerator.Current, out var record))
                    {
                        continue;
                    }

                    if (record.Seq <= lastSeq)
                    {
                        continue;
                    }

                    lastSeq = record.Seq;
                    yield return record;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            // A stream that ended cleanly (server end frame) may still be reopened after a short pause.
            try
            {
                await DelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await DisconnectAsync().ConfigureAwait(false);
        CompleteInteractionStreams();
    }

    // ---- connection supervisor ----------------------------------------------------------------------

    private async Task SuperviseAsync(DshPairingInfo pairing, Uri baseUri, HttpClient http, CancellationToken cancellationToken)
    {
        var everConnected = false;
        var failedAttempts = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                LaunchedConnection launched;
                try
                {
                    launched = await _launcher.LaunchAsync(http, baseUri, pairing, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (DshRemoteException ex) when (ex.Code is DshRemoteException.Codes.Unauthorized
                                                       or DshRemoteException.Codes.Forbidden
                                                       or DshRemoteException.Codes.ConnectFailed)
                {
                    FailConnection(ex);
                    break;
                }
                catch (Exception ex)
                {
                    failedAttempts++;
                    if (!everConnected || !_options.Reconnect.CanRetry(failedAttempts))
                    {
                        FailConnection(ex);
                        break;
                    }

                    _options.Log?.Invoke($"reconnect attempt {failedAttempts} failed: {ex.Message}");
                    Transition(DshConnectionState.Reconnecting, ex);
                    try
                    {
                        await DelayAsync(_options.Reconnect.DelayFor(failedAttempts), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                var mux = new MuxConnection(launched.Socket, _options.Log);
                long generation;
                lock (_gate)
                {
                    _current = launched;
                    _mux = mux;
                    generation = ++_generation;
                    everConnected = true;
                    failedAttempts = 0;
                }

                Transition(DshConnectionState.Connected, null);
                _options.Log?.Invoke($"connected generation {generation}");

                try
                {
                    await mux.Closed.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _current = null;
                        _mux = null;
                    }

                    await mux.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                lock (_gate)
                {
                    _current = null;
                    _mux = null;
                }

                await mux.DisposeAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _options.Log?.Invoke($"connection lost: {mux.Failure?.Message ?? "socket closed"}");
                Transition(DshConnectionState.Reconnecting, mux.Failure);
            }
        }
        catch (Exception ex)
        {
            FailConnection(ex);
        }
    }

    private void FailConnection(Exception cause)
    {
        _failure = cause;
        Transition(DshConnectionState.Failed, cause);
    }

    // ---- RPC ----------------------------------------------------------------------------------------

    private async Task<JsonElement> CallAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        var generation = -1L;
        for (var attempt = 0; ; attempt++)
        {
            LaunchedConnection connection;
            (connection, generation) = await WaitForConnectionAsync(generation, cancellationToken).ConfigureAwait(false);
            var http = GetHttpClient();
            var body = RpcWire.BuildClientRequest(method, args, out var rpcId);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(connection.BaseUri, RpcWire.ApiPath(method)));
            request.Headers.TryAddWithoutValidation("Cookie", connection.CookieHeader);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
            {
                SignalTransportFailure(ex.Message);
                if (attempt >= 1)
                {
                    throw new DshRemoteException(
                        DshRemoteException.Codes.Transport,
                        $"RPC '{method}' failed on the transport: {ex.Message}",
                        null,
                        ex);
                }

                continue;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    SignalTransportFailure($"RPC '{method}' answered HTTP {(int)response.StatusCode}");
                    if (attempt >= 1)
                    {
                        var code = response.StatusCode == HttpStatusCode.Unauthorized
                            ? DshRemoteException.Codes.Unauthorized
                            : DshRemoteException.Codes.Forbidden;
                        throw new DshRemoteException(code, $"RPC '{method}' answered HTTP {(int)response.StatusCode} after reconnecting");
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // An unknown RPC path means this host build predates the endpoint; that is a capability
                        // signal, not a lost connection (the mux is still alive). Keep it out of Transport so
                        // the UI can hide the feature instead of claiming the relay is unreachable.
                        throw new DshRemoteException(
                            DshRemoteException.Codes.EndpointUnavailable,
                            $"RPC '{method}' endpoint is not available on this host (HTTP 404)");
                    }

                    throw new DshRemoteException(
                        DshRemoteException.Codes.Transport,
                        $"RPC '{method}' answered HTTP {(int)response.StatusCode}");
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return RpcWire.ParseServerResponse(text, rpcId);
            }
        }
    }

    private void SignalTransportFailure(string reason)
    {
        MuxConnection? mux;
        lock (_gate)
        {
            mux = _mux;
        }

        mux?.Abort(new DshRemoteException(DshRemoteException.Codes.Transport, reason));
    }

    // ---- waiting for a connection generation --------------------------------------------------------

    private async Task<(LaunchedConnection Connection, long Generation)> WaitForConnectionAsync(
        long minGeneration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_state == DshConnectionState.Failed)
                {
                    throw BuildFailureException();
                }

                if (_state == DshConnectionState.Disconnected)
                {
                    throw new DshRemoteException(DshRemoteException.Codes.Transport, "client is not connected");
                }

                if (_current is not null && _generation > minGeneration)
                {
                    return (_current, _generation);
                }

                signal = _connectedSignal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(MuxConnection Mux, long Generation)> WaitForMuxAsync(long minGeneration, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_state == DshConnectionState.Failed)
                {
                    throw BuildFailureException();
                }

                if (_state == DshConnectionState.Disconnected)
                {
                    throw new DshRemoteException(DshRemoteException.Codes.Transport, "client is not connected");
                }

                if (_mux is not null && _generation > minGeneration)
                {
                    return (_mux, _generation);
                }

                signal = _connectedSignal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private DshRemoteException BuildFailureException()
    {
        if (_failure is DshRemoteException remote)
        {
            return remote;
        }

        return new DshRemoteException(
            DshRemoteException.Codes.Transport,
            $"connection failed: {_failure?.Message ?? "unknown error"}",
            null,
            _failure);
    }

    // ---- state --------------------------------------------------------------------------------------

    private void Transition(DshConnectionState next, Exception? cause)
    {
        EventHandler<DshConnectionState>? handlers;
        lock (_gate)
        {
            var previous = _state;
            if (previous == next)
            {
                return;
            }

            _state = next;
            if (next == DshConnectionState.Failed && cause is not null)
            {
                _failure = cause;
            }

            if (next == DshConnectionState.Connected)
            {
                _connectedSignal.TrySetResult();
            }
            else if (next is DshConnectionState.Failed or DshConnectionState.Disconnected)
            {
                var signal = _connectedSignal;
                _connectedSignal = NewSignal();
                signal.TrySetException(next == DshConnectionState.Failed
                    ? (Exception)BuildFailureException()
                    : new DshRemoteException(DshRemoteException.Codes.Transport, "connection was disconnected"));
            }
            else if (previous == DshConnectionState.Connected)
            {
                _connectedSignal = NewSignal();
            }

            handlers = StateChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<DshConnectionState>>())
        {
            try
            {
                handler(this, next);
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"StateChanged handler threw: {ex.Message}");
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private HttpClient CreateHttpClient(DshPairingInfo pairing, Uri baseUri)
    {
        HttpMessageHandler handler;
        if (_options.HttpHandlerFactory is { } factory)
        {
            handler = factory();
        }
        else
        {
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // The gateway compresses JSON/text responses (history pages, the session list) when the phone
                // advertises gzip. Without this the 1.4 MB session/follow and 1 MB list payloads are shipped
                // uncompressed; older relays simply ignore the header and answer plain.
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = TlsPinning.CreateValidationCallback(pairing, _options.Log)
                }
            };
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseUri,
            Timeout = _options.RequestTimeout
        };
    }

    private HttpClient GetHttpClient()
    {
        lock (_gate)
        {
            return _http ?? throw new DshRemoteException(DshRemoteException.Codes.Transport, "HTTP client is not connected");
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _options.Delay is { } custom ? custom(delay, cancellationToken) : Task.Delay(delay, cancellationToken);

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DshClient));
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
