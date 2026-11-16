using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DshRemote.Core;

namespace DshRemote.Core.Tests;

/// <summary>In-memory <see cref="IRemoteSocket"/>: outbound frames are recorded, inbound frames are pushed.</summary>
internal sealed class FakeRemoteSocket : IRemoteSocket
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly List<string> _sent = new();

    public IReadOnlyList<string> SentFrames
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public bool Disposed { get; private set; }

    public ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(text);
        }

        return ValueTask.CompletedTask;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Push(string text) => _inbound.Writer.TryWrite(text);

    /// <summary>Simulates a clean server/network close of the physical socket.</summary>
    public void Close() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Launcher that hands out pre-queued sockets (and can fail the next launch on demand).</summary>
internal sealed class FakeLauncher : IConnectionLauncher
{
    private readonly Channel<FakeRemoteSocket> _sockets = Channel.CreateUnbounded<FakeRemoteSocket>();
    private int _launchCount;

    public int LaunchCount => Volatile.Read(ref _launchCount);

    public int LaunchWaitCount => Volatile.Read(ref _launchWaitCount);

    private int _launchWaitCount;

    public bool FailNextWithAuth { get; set; }

    public bool FailNextWithNetwork { get; set; }

    public void Enqueue(FakeRemoteSocket socket) => _sockets.Writer.TryWrite(socket);

    public async Task<LaunchedConnection> LaunchAsync(
        HttpClient http,
        Uri baseUri,
        DshPairingInfo pairing,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _launchCount);
        if (FailNextWithAuth)
        {
            FailNextWithAuth = false;
            throw new DshRemoteException(DshRemoteException.Codes.Unauthorized, "fake 401");
        }

        if (FailNextWithNetwork)
        {
            FailNextWithNetwork = false;
            throw new HttpRequestException("fake network failure");
        }

        Interlocked.Increment(ref _launchWaitCount);
        var socket = await _sockets.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new LaunchedConnection(baseUri, $"dsh-auth-test=cookie-{LaunchCount}", socket);
    }
}

/// <summary>Captures unary RPC requests and answers them from a scripted responder.</summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    public Func<RecordedRequest, HttpResponseMessage> Responder { get; set; } =
        _ => JsonResponse("""{"type":"server-response","rpcId":"missing","result":{"ok":true,"value":{}}}""");

    public void EnqueueResponse(HttpResponseMessage response) => Responder = _ => response;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : null,
            body,
            request.Headers.TryGetValues("Authorization", out var authorization) ? string.Join(",", authorization) : null,
            request.Headers.TryGetValues("Accept", out var accept) ? string.Join(",", accept) : null);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        return Responder(recorded);
    }

    public static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Cookie,
    string Body,
    string? Authorization = null,
    string? Accept = null);

internal static class TestJson
{
    public static string RpcIdOf(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.GetProperty("rpcId").GetString()!;
    }

    public static string Success(string rpcId, string valueJson) =>
        "{\"type\":\"server-response\",\"rpcId\":\"" + rpcId + "\",\"result\":{\"ok\":true,\"value\":" + valueJson + "}}";

    public static string SuccessFromRequest(string requestBody, string valueJson) =>
        Success(RpcIdOf(requestBody), valueJson);

    public static string Failure(string rpcId, string code, string message) =>
        "{\"type\":\"server-response\",\"rpcId\":\"" + rpcId +
        "\",\"result\":{\"ok\":false,\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\",\"details\":{}}}}";
}
