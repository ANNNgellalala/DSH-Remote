using System.Text.Json;

namespace DshRemote.Core;

/// <summary>High-level state of the DSH remote connection.</summary>
public enum DshConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>How the phone reaches the desktop runtime.</summary>
public enum DshPairingMode
{
    /// <summary>LAN/local relay direct connection (M0–M5, pairing URI v1).</summary>
    Local,

    /// <summary>Public gateway (VPS) connection; the gateway reverses the token (pairing URI v2).</summary>
    Gateway
}

/// <summary>
/// Everything a phone needs to reach one desktop DSH instance:
/// host + relay/gateway port + access token, plus the pinned TLS SPKI fingerprint.
/// </summary>
/// <param name="Mode">
/// <see cref="DshPairingMode.Gateway"/> for the v2 <c>dshremote://pair?v=2&amp;mode=gateway…</c>
/// (or a schemaVersion=2 pairing document with a <c>gateway</c> block); <see cref="DshPairingMode.Local"/>
/// for v1 LAN pairings. The protocol layer is identical: connect to <c>Host:Port</c> and pin
/// <paramref name="SpkiSha256"/>; in gateway mode the token is the gateway's remote token.
/// </param>
/// <param name="GatewayOnline">Gateway reachability reported by a v2 pairing document, when known.</param>
/// <param name="LocalFallback">
/// The v1 LAN pairing a v2 document also carries (<c>localHost/localPort/localPairingUri</c>), so the App can
/// offer "use LAN directly" without re-pairing.
/// </param>
public sealed record DshPairingInfo(
    string Host,
    int Port,
    string Token,
    string? SpkiSha256 = null,
    string? Name = null,
    DshPairingMode Mode = DshPairingMode.Local,
    bool? GatewayOnline = null,
    DshPairingInfo? LocalFallback = null)
{
    public bool IsGateway => Mode == DshPairingMode.Gateway;

    public Uri BaseUri => new($"https://{Host}:{Port}/");

    /// <summary>Serializes back into the <c>dshremote://pair?...</c> pairing URI (v1 local / v2 gateway).</summary>
    public string ToPairingUri()
    {
        var query = new List<string>();
        if (IsGateway)
        {
            query.Add("v=2");
            query.Add("mode=gateway");
        }
        else
        {
            query.Add("v=1");
        }

        query.Add("host=" + Uri.EscapeDataString(Host));
        query.Add("port=" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        query.Add("token=" + Uri.EscapeDataString(Token));
        if (!string.IsNullOrWhiteSpace(SpkiSha256))
        {
            query.Add("fp=" + Uri.EscapeDataString(SpkiSha256));
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            query.Add("name=" + Uri.EscapeDataString(Name));
        }

        return "dshremote://pair?" + string.Join("&", query);
    }

    /// <summary>Named convenience alias for <see cref="ToPairingUri"/> (pairing-URI builder).</summary>
    public static string BuildPairingUri(DshPairingInfo pairing) =>
        (pairing ?? throw new ArgumentNullException(nameof(pairing))).ToPairingUri();

    /// <summary>
    /// Accepts the canonical pairing URI (v1 local and v2 gateway) and two convenience forms:
    /// <c>https://host:port/?token=...</c> and a bare <c>host:port</c> plus a separate token line.
    /// </summary>
    public static bool TryParse(string? text, out DshPairingInfo? pairing, string? separateToken = null)
    {
        pairing = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim();
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == "dshremote")
        {
            var q = ParseQuery(uri.Query);
            if (!q.TryGetValue("host", out var host) || !q.TryGetValue("token", out var token))
            {
                return false;
            }

            if (!int.TryParse(q.GetValueOrDefault("port", "443"), out var port))
            {
                return false;
            }

            var version = int.TryParse(q.GetValueOrDefault("v", "1"), out var parsedVersion) ? parsedVersion : 1;
            var mode = ParseMode(q.GetValueOrDefault("mode"), version);
            pairing = new DshPairingInfo(host, port, token, q.GetValueOrDefault("fp"), q.GetValueOrDefault("name"), mode);
            return true;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var httpUri) && (httpUri.Scheme is "https" or "http"))
        {
            var q = ParseQuery(httpUri.Query);
            if (!q.TryGetValue("token", out var token))
            {
                return false;
            }

            var port = httpUri.IsDefaultPort ? (httpUri.Scheme == "https" ? 443 : 80) : httpUri.Port;
            pairing = new DshPairingInfo(httpUri.Host, port, token, q.GetValueOrDefault("fp"), null);
            return true;
        }

        // "host:port" pasted together with a token on the next line.
        if (!string.IsNullOrWhiteSpace(separateToken))
        {
            var colon = raw.LastIndexOf(':');
            if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var port))
            {
                pairing = new DshPairingInfo(raw[..colon].Trim(), port, separateToken.Trim(), null, null);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps a pairing URI's <c>mode</c> to <see cref="DshPairingMode"/>. A v2 URI defaults to gateway when
    /// <c>mode</c> is absent; unknown/local values fall back to local.
    /// </summary>
    public static DshPairingMode ParseMode(string? mode, int version)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode.Trim().Equals("gateway", StringComparison.OrdinalIgnoreCase)
                ? DshPairingMode.Gateway
                : DshPairingMode.Local;
        }

        return version >= 2 ? DshPairingMode.Gateway : DshPairingMode.Local;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            var value = idx < 0 ? string.Empty : pair[(idx + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return result;
    }
}

/// <summary>One row from <c>session/list</c>.</summary>
/// <param name="ParentSessionId">
/// Durable parent address of a child (subagent) session; <c>null</c> for top-level sessions.
/// Child sessions reject direct <c>session/events</c>/<c>session/state</c> reads with
/// <c>session/agent-busy</c> — the phone must open them through the parent.
/// </param>
/// <param name="Origin">Session origin, e.g. <c>subagent</c>; <c>null</c> for user-created sessions.</param>
public sealed record SessionSummary(
    string SessionId,
    string? Title,
    string? Cwd,
    string? Model,
    bool Running,
    bool AgentAvailable,
    bool Blank,
    long UpdatedAt,
    long LastPromptAt,
    string? ParentSessionId = null,
    string? Origin = null,
    DshModelSelection? ModelSelection = null);

/// <summary>
/// How a surface event entered the ordered session surface (DSH session-log v4, docs: session.md §Surface 类型).
/// <para>
/// <see cref="None"/> means the envelope carried no <c>surfaceOp</c> (older host format); surface events in
/// that case are treated as <see cref="Append"/> for backward compatibility.
/// </para>
/// </summary>
public enum SurfaceOpKind
{
    None = 0,
    Append = 1,
    Replace = 2
}

/// <summary>
/// The shadowed range of a <c>replace</c> surface operation. Endpoints are <em>surface positions</em>
/// (seqs of the current surface nodes), not a numeric interval: after a prior replacement, <see cref="StartSeq"/>
/// can be greater than <see cref="EndSeq"/>. A point replacement has <c>StartSeq == EndSeq</c>.
/// </summary>
public readonly record struct SurfaceReplaceSpan(long StartSeq, long EndSeq)
{
    public bool IsSpan => StartSeq != EndSeq;
}

/// <summary>One persisted <c>SessionEvent</c>. <see cref="Data"/> is the raw event data payload.</summary>
public sealed record SessionEventRecord(long Seq, long Time, string Type, JsonElement Data, string RawJson)
{
    /// <summary>Surface placement declared by the envelope; <see cref="SurfaceOpKind.None"/> for legacy events.</summary>
    public SurfaceOpKind SurfaceOp { get; init; }

    /// <summary>Shadowed surface range for <see cref="SurfaceOpKind.Replace"/>; point replacements carry both endpoints.</summary>
    public SurfaceReplaceSpan? ReplaceSpan { get; init; }

    /// <summary>Earlier event seqs cited by the envelope (<c>sourceEventSeqs</c>); <c>null</c> when absent.</summary>
    public IReadOnlyList<long>? SourceEventSeqs { get; init; }

    /// <summary>Envelope <c>ignorable: true</c>: readers may skip the event when its type is unknown.</summary>
    public bool Ignorable { get; init; }

    /// <summary>True for the five message-producing event types that carry surface metadata.</summary>
    public bool IsSurfaceEvent { get; init; }

    /// <summary>True when this surface event <em>replaces</em> existing surface nodes instead of appending.</summary>
    public bool IsReplacementSurfaceEvent => IsSurfaceEvent && SurfaceOp == SurfaceOpKind.Replace;

    /// <summary>
    /// True when the event belongs to a human transcript row. Provided by the DSH contract: replacement
    /// copies stay model-only — rendering them would duplicate or erase conversation the user already saw.
    /// Legacy surface events without <c>surfaceOp</c> stay visible (compatibility with older hosts).
    /// </summary>
    public bool IsTranscriptEvent => !IsSurfaceEvent || SurfaceOp != SurfaceOpKind.Replace;

    /// <summary>
    /// First cited source seq, if any. For append <c>tool/result</c> this is the matching <c>tool/call</c>
    /// seq; for replacements it is the shadowed node seq. Used as a cross-page matching key.
    /// </summary>
    public long? PrimarySourceSeq => SourceEventSeqs is { Count: > 0 } seqs && seqs[0] > 0 ? seqs[0] : null;

    /// <summary>DSH <c>SurfaceEventType</c> set (the only event types that may carry surface metadata).</summary>
    public static bool IsSurfaceEventType(string type) => type is
        "system/message" or "developer/message" or "user/message" or "assistant/message" or "tool/result";

    /// <summary>
    /// Parses one raw envelope line (cache round-trip path). Returns <c>null</c> when the payload is not a
    /// JSON object with a usable <c>type</c>.
    /// </summary>
    public static SessionEventRecord? FromRawJson(string rawJson) => DshParsing.ParseRecord(rawJson);

    public string TextOf()
    {
        // Message events carry data.message.content[]; mirrored here as a convenience for the UI layer.
        if (Data.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }


        if (Data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            return ContentText(message);
        }

        return Data.TryGetProperty("content", out var content) ? ContentText(Data) : string.Empty;
    }

    private static string ContentText(JsonElement message)
    {
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    part.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }

            return string.Join("\n", parts);
        }

        return string.Empty;
    }
}

/// <summary>Result of a history page read (<c>session/events</c>).</summary>
/// <param name="Records">Events in ascending <c>seq</c> order.</param>
/// <param name="Cursor">Log cut reported by the server at read time (log end; may exceed this page max seq).</param>
/// <param name="HasMore">Server flag: more (older) history exists past this page.</param>
/// <param name="HasMoreOlder">Same meaning as <see cref="HasMore"/>, named for the backwards paging API.</param>
/// <param name="BeforeSeq">
/// Pass to <see cref="IHistoryPagingClient.GetOlderHistoryAsync"/> to read the next older page; <c>null</c>
/// when this page is empty (nothing older to ask for). Pages never repeat a seq.
/// </param>
public sealed record SessionHistoryPage(
    IReadOnlyList<SessionEventRecord> Records,
    long Cursor,
    bool HasMore,
    bool HasMoreOlder = false,
    long? BeforeSeq = null);

/// <summary>
/// M6: incremental history paging tuned for "show the newest page first, page upwards on scroll".
/// Implemented by <c>DshClient</c> next to <see cref="IDshClient"/>; UI may cast to it.
/// </summary>
public interface IHistoryPagingClient
{
    /// <summary>
    /// Reads only the newest page (<c>session/events</c> with no <c>beforeSeq</c>) so the first screen can
    /// render as soon as one request returns; call <see cref="GetOlderHistoryAsync"/> for the page above it.
    /// </summary>
    Task<SessionHistoryPage> GetLatestHistoryAsync(string sessionId, int maxMessages = 30, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the page strictly older than <paramref name="beforeSeq"/> (pass a previous page's
    /// <see cref="SessionHistoryPage.BeforeSeq"/>). Records are ascending, never duplicate a seq and are
    /// contiguous with the page that produced <paramref name="beforeSeq"/>.
    /// </summary>
    Task<SessionHistoryPage> GetOlderHistoryAsync(string sessionId, long beforeSeq, int maxMessages = 30, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fast first-screen open: latest history page + passive runtime state, without waiting for a
    /// <c>session/follow</c> snapshot. Subscribe to <see cref="ISessionFollowClient.FollowEventsAsync"/>
    /// in the background afterwards; it skips the events this baseline already covered.
    /// </summary>
    Task<SessionLiteSnapshot> OpenSessionLiteAsync(string sessionId, int maxMessages = 30, CancellationToken cancellationToken = default);
}

/// <summary>First-screen snapshot returned by <see cref="IHistoryPagingClient.OpenSessionLiteAsync"/>.</summary>
public sealed record SessionLiteSnapshot(SessionHistoryPage Page, SessionRuntimeState? State);

/// <summary>One queued prompt reported by <c>session/state</c>.</summary>
public sealed record QueuedPrompt(string Id, string Placement, string? Text);

/// <summary>Result of <c>session/state</c>.</summary>
public sealed record SessionRuntimeState(
    long Cursor,
    bool Active,
    bool Running,
    IReadOnlyList<QueuedPrompt> Queue,
    IReadOnlyList<string> PendingInput);

/// <summary>
/// The one interface the UI layer talks to. The implementation owns the official DSH wire:
/// GET /connect (owner API cookie) + POST /api/&lt;ns&gt;/&lt;method&gt; + WS /api/remote.mux streams.
/// </summary>
public interface IDshClient : IAsyncDisposable
{
    DshConnectionState State { get; }

    DshPairingInfo? Pairing { get; }

    event EventHandler<DshConnectionState>? StateChanged;

    Task ConnectAsync(DshPairingInfo pairing, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<SessionHistoryPage> GetHistoryAsync(
        string sessionId,
        long? beforeSeq = null,
        int maxMessages = 80,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeState> GetStateAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Queues (or steers) a text prompt; returns the client-minted request id.</summary>
    Task<string> SendPromptAsync(string sessionId, string text, bool steer = false, CancellationToken cancellationToken = default);

    Task CancelAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Live <c>session/observeEvent</c> stream; completes when the connection is disposed.</summary>
    IAsyncEnumerable<SessionEventRecord> ObserveEventsAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues/steers a prompt that may carry images (base64 content blocks, matching the official
    /// <c>session/prompt</c> strict schema). Returns the client-minted request id.
    /// </summary>
    Task<string> SendPromptAsync(
        string sessionId,
        string text,
        IReadOnlyList<DshImageAttachment>? images,
        bool steer = false,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------------------------------
// M5: images (display + send) and workspace grouping / archive / create.
// ---------------------------------------------------------------------------------------------------

/// <summary>Reference to one persisted image attachment (as it appears in session content blocks).</summary>
public sealed record DshImageRef(
    string AttachmentId,
    string MediaType,
    long Bytes,
    int Width,
    int Height,
    string? Name = null);

/// <summary>An image selected by the user, ready to be sent (raw encoded bytes).</summary>
public sealed record DshImageAttachment(byte[] Data, string MediaType, string? Name = null);

/// <summary>Fetched image bytes plus their durable reference.</summary>
public sealed record DshImageData(DshImageRef Image, byte[] Bytes);

/// <summary>One ordered part of a message/tool-result content array.</summary>
public abstract record DshContentPart;

/// <summary>Text part of a content array.</summary>
public sealed record DshTextPart(string Text) : DshContentPart;

/// <summary>Image part of a content array (fetch bytes with <see cref="IImageClient"/>).</summary>
public sealed record DshImagePart(DshImageRef Image) : DshContentPart;

/// <summary>Reads persisted image attachments over <c>session/attachment</c>.</summary>
public interface IImageClient
{
    Task<DshImageData> GetImageAsync(string sessionId, DshImageRef image, CancellationToken cancellationToken = default);
}

/// <summary>One Workspace row from <c>workspace/follow</c>.</summary>
public sealed record DshWorkspace(
    string WorkspaceId,
    string? Path,
    string Title,
    IReadOnlyList<string> SessionIds,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Full workspace state: rows, archived and pinned session ids.</summary>
public sealed record DshWorkspaceState(
    IReadOnlyList<DshWorkspace> Workspaces,
    IReadOnlyList<string> ArchivedSessionIds,
    IReadOnlyList<string> PinnedSessionIds);

/// <summary>Workspace listing and mutations used by the grouped session list.</summary>
public interface IWorkspaceClient
{
    /// <summary>Reads the current state through a short-lived <c>workspace/follow</c> baseline.</summary>
    Task<DshWorkspaceState> GetWorkspaceStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Archives a session; when the session is running pass <paramref name="stopActivity"/>.</summary>
    Task<DshWorkspaceState> ArchiveSessionAsync(string sessionId, bool stopActivity = false, CancellationToken cancellationToken = default);

    /// <summary>Restores an archived session.</summary>
    Task<DshWorkspaceState> UnarchiveSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Creates a session inside a workspace (or under a bare cwd), returning its session id.</summary>
    Task<string> CreateSessionAsync(string? workspaceId = null, string? cwd = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tolerant content-array reader shared by Core and the UI. Understands the official
/// <c>user/message</c>, <c>assistant/message</c> and <c>tool/result</c> shapes: text and image parts,
/// with images carrying a durable <c>attachment</c> reference (never inline bytes).
/// </summary>
public static class DshContent
{
    /// <summary>Ordered text/image parts of a message/tool-result event (other block kinds are skipped).</summary>
    public static IReadOnlyList<DshContentPart> PartsOf(SessionEventRecord record)
    {
        var parts = new List<DshContentPart>();
        if (record.Data.ValueKind != JsonValueKind.Object)
        {
            return parts;
        }

        if (!TryGetContentArray(record.Data, out var array))
        {
            return parts;
        }

        foreach (var block in array.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = block.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "text")
            {
                if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(new DshTextPart(text.GetString() ?? string.Empty));
                }
            }
            else if (type == "image")
            {
                if (block.TryGetProperty("attachment", out var attachment) && attachment.ValueKind == JsonValueKind.Object)
                {
                    var image = ParseImageRef(attachment);
                    if (image is not null)
                    {
                        parts.Add(new DshImagePart(image));
                    }
                }
            }
        }

        return parts;
    }

    /// <summary>Concatenated text parts of an event (empty string when none).</summary>
    public static string TextOf(SessionEventRecord record)
    {
        var texts = PartsOf(record).OfType<DshTextPart>().Select((part) => part.Text).ToList();
        return texts.Count == 0 ? string.Empty : string.Join("\n", texts);
    }

    /// <summary>All image references in an event, in content order.</summary>
    public static IReadOnlyList<DshImageRef> ImagesOf(SessionEventRecord record)
        => PartsOf(record).OfType<DshImagePart>().Select((part) => part.Image).ToList();

    /// <summary>Parses one <c>attachment</c> JSON object into a reference, or null when malformed.</summary>
    public static DshImageRef? ParseImageRef(JsonElement attachment)
    {
        // Tolerate a non-object here: callers also feed untrusted content blocks through this reader.
        if (attachment.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(attachment, "attachmentId");
        var mediaType = GetString(attachment, "mediaType");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        return new DshImageRef(
            id!,
            mediaType!,
            GetLong(attachment, "bytes"),
            (int)GetLong(attachment, "width"),
            (int)GetLong(attachment, "height"),
            GetString(attachment, "name"));
    }

    private static bool TryGetContentArray(JsonElement data, out JsonElement array)
    {
        if (data.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (data.TryGetProperty("content", out array) && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var dbl) => (long)dbl,
            _ => 0
        };
    }
}

// ---------------------------------------------------------------------------------------------------
// M1: history+live follow with reconnect / seq-gap repair.
// Implemented by DshClient in addition to IDshClient; the UI may cast to this interface.
// ---------------------------------------------------------------------------------------------------

/// <summary>Baseline returned when a session is opened: history page plus current runtime state.</summary>
public sealed record SessionSnapshot(
    IReadOnlyList<SessionEventRecord> Events,
    long Cursor,
    bool HasMore,
    SessionRuntimeState? State,
    DshModelSelection? ModelSelection = null);

/// <summary>One queue edit operation.</summary>
public enum QueueEditAction
{
    Edit,
    Remove,
    Steer
}

/// <summary>
/// Server-side <c>session/follow</c> wrapper: one baseline snapshot, then live events that survive
/// websocket loss (the client reconnects, re-reads <c>session/events</c> after the last seen seq and
/// replays missed events before resuming the live stream).
/// </summary>
public interface ISessionFollowClient
{
    /// <summary>Opens a follow generation and returns the baseline (records + cursor + runtime state).</summary>
    Task<SessionSnapshot> OpenSessionAsync(string sessionId, int maxMessages = 80, CancellationToken cancellationToken = default);

    /// <summary>Live events after <see cref="OpenSessionAsync"/>, gap-repaired across reconnects.</summary>
    IAsyncEnumerable<SessionEventRecord> FollowEventsAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Steers the running turn (equivalent to <c>session/prompt</c> with mode=steer).</summary>
    Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>Edits/removes a queued prompt (<c>session/updateQueue</c>).</summary>
    Task UpdateQueueAsync(string sessionId, string itemId, QueueEditAction action, string? text = null, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------------------------------
// M2: approvals + questions over the forwarded `$events` waterfall.
// Implemented by DshClient in addition to IDshClient.
// ---------------------------------------------------------------------------------------------------

/// <summary>One approval request forwarded by the host (tool call awaiting a human decision).</summary>
public sealed record DshApprovalRequest(
    string RequestId,
    string SessionId,
    string ToolName,
    string? CallId = null,
    string? Reason = null,
    string? DisplayReason = null,
    string? ArgumentsJson = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Approval outcome vocabulary (mirrors the official approval seam).</summary>
public enum DshApprovalOutcome
{
    AllowedOnce,
    Rejected,
    Cancelled
}

/// <summary>One question inside a structured user-question request.</summary>
public sealed record DshQuestionItem(
    string Id,
    string Text,
    IReadOnlyList<string> Options,
    bool AllowMultiple = false,
    bool AllowFreeText = false,
    string? Detail = null);

/// <summary>Structured question batch forwarded by the agent (<c>user-questions/request</c>).</summary>
public sealed record DshQuestionRequest(
    string RequestId,
    string SessionId,
    IReadOnlyList<DshQuestionItem> Questions,
    DateTimeOffset? ExpiresAt = null);

/// <summary>One answer inside a question batch.</summary>
public sealed record DshQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom = null);

/// <summary>
/// Owner-only interaction client: opens the <c>$events</c> stream with <c>interactions=answer</c>,
/// surfaces waterfall requests and answers them through <c>$events/result</c>.
/// </summary>
public interface IInteractionClient
{
    /// <summary>Approval requests as they arrive (bounded queue; also answerable when reconnecting).</summary>
    IAsyncEnumerable<DshApprovalRequest> ApprovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>Question batches as they arrive.</summary>
    IAsyncEnumerable<DshQuestionRequest> QuestionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Answers one approval request; safe to call twice (second call is a no-op).</summary>
    Task RespondApprovalAsync(string requestId, DshApprovalOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Answers one question batch; set <paramref name="cancelled"/> to close the prompt.</summary>
    Task RespondQuestionAsync(
        string requestId,
        IReadOnlyList<DshQuestionAnswerItem> answers,
        bool cancelled = false,
        CancellationToken cancellationToken = default);
}

// ---- Windows ACL guard (relay-owned API) ---------------------------------------------------------

/// <summary>One workspace root known to the desktop relay's admission guard.</summary>
/// <param name="Root">Canonical workspace path (the session header cwd, realpath-resolved).</param>
/// <param name="State"><c>ready</c> / <c>stale</c> / <c>failed</c> / <c>unknown</c>.</param>
/// <param name="PreparedAt">When the root was last admitted (cache timestamp).</param>
/// <param name="LastSeenAt">When a session last asked about this root in the current host run.</param>
public sealed record DshAclRootStatus(
    string Root,
    string State,
    DateTimeOffset? PreparedAt = null,
    DateTimeOffset? LastSeenAt = null);

/// <summary>Desktop relay ACL guard status, as shown by the phone settings card.</summary>
/// <param name="Enabled">False when the guard is off (non-Windows, disabled by config, or old relay).</param>
/// <param name="Version">Guard schema version; a bump invalidates persisted admissions.</param>
/// <param name="Roots">Known workspace roots with their last admission state.</param>
/// <param name="CandidateCount">How many roots a health check would cover right now.</param>
/// <param name="CheckedAt">When the relay generated this snapshot.</param>
public sealed record DshAclGuardStatus(
    bool Enabled,
    int Version,
    IReadOnlyList<DshAclRootStatus> Roots,
    int CandidateCount,
    DateTimeOffset? CheckedAt = null);

/// <summary>One root's outcome inside a phone-triggered health check.</summary>
/// <param name="Root">Canonical workspace path.</param>
/// <param name="Ready">True when DSH can provision the sandbox for this root.</param>
/// <param name="Repaired">True when this run actually wrote the missing ACE.</param>
/// <param name="DurationMs">How long the check/repair of this root took.</param>
/// <param name="Reason">Why it is not ready (skip reason, probe/repair failure).</param>
public sealed record DshAclRootCheck(
    string Root,
    bool Ready,
    bool Repaired = false,
    long DurationMs = 0,
    string? Reason = null);

/// <summary>Result of a phone-triggered "一键体检/修复".</summary>
/// <param name="Repair">Whether the request allowed writing ACLs.</param>
/// <param name="Roots">Per-root outcomes.</param>
/// <param name="CheckedAt">When the relay generated the result.</param>
public sealed record DshAclCheckResult(
    bool Repair,
    IReadOnlyList<DshAclRootCheck> Roots,
    DateTimeOffset? CheckedAt = null);

/// <summary>Reads the relay-owned Windows ACL guard state and can trigger a health check/repair.</summary>
public interface IAclGuardClient
{
    /// <summary>Current guard status plus the known workspace roots (no probing).</summary>
    Task<DshAclGuardStatus> GetAclStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the guard against the known workspace roots (or one of them). With <paramref name="repair"/> true
    /// the desktop additionally writes the missing inheritable ACE; with false it only reports.
    /// </summary>
    Task<DshAclCheckResult> CheckAclAsync(
        bool repair = true,
        string? root = null,
        CancellationToken cancellationToken = default);
}

// ---- Desktop-side auto-approval (relay-owned API, relay 0.7.0+) ------------------------------------

/// <summary>One workspace selected for desktop-side auto-approval.</summary>
/// <param name="WorkspaceId">Durable workspace id; the primary match key.</param>
/// <param name="Path">Workspace path, used as a fallback when the registry cannot resolve it.</param>
/// <param name="Title">Display title captured when the range was chosen.</param>
public sealed record DshAutoApproveWorkspace(
    string? WorkspaceId,
    string? Path,
    string? Title = null);

/// <summary>One session selected for desktop-side auto-approval (its subagents inherit it).</summary>
/// <param name="SessionId">Durable session id.</param>
/// <param name="Title">Display title captured when the range was chosen.</param>
/// <param name="Cwd">Session working directory at selection time (display/diagnostics only).</param>
/// <param name="ParentSessionId">Parent session id for a subagent session, when known.</param>
public sealed record DshAutoApproveSession(
    string SessionId,
    string? Title = null,
    string? Cwd = null,
    string? ParentSessionId = null);

/// <summary>How many approvals the desktop relay has answered automatically in this host run.</summary>
public sealed record DshAutoApproveStats(
    int Allowed = 0,
    int Errors = 0,
    DateTimeOffset? LastAllowedAt = null);

/// <summary>One automatic approval decision recorded by the relay (most recent first).</summary>
public sealed record DshAutoApproveAudit(
    DateTimeOffset At,
    string? SessionId = null,
    string? ToolName = null,
    string? CallId = null,
    string? Rule = null);

/// <summary>
/// Complete desktop auto-approval document: the source of truth lives on the desktop
/// (<c>~/.dsh/remote/auto-approve.json</c>); the phone edits it through the relay API.
/// </summary>
/// <param name="Enabled">Master switch.</param>
/// <param name="All">True when every session (current and future) is covered.</param>
/// <param name="Workspaces">Fully selected workspaces: every session in them, including future ones.</param>
/// <param name="Sessions">Individually selected sessions in partially selected workspaces (subagents inherit).</param>
/// <param name="Revision">Monotonic relay revision of the saved document.</param>
/// <param name="UpdatedAt">When the document was last saved.</param>
/// <param name="UpdatedBy">Who saved it (currently <c>phone</c>).</param>
/// <param name="Stats">Counters for this host run.</param>
/// <param name="Recent">Most recent automatic decisions (bounded by the relay).</param>
/// <param name="Error">Rules-file error reported by the relay, if any.</param>
/// <param name="RelayVersion">Relay version that served the document.</param>
/// <param name="File">Rules file path on the desktop (diagnostics).</param>
public sealed record DshAutoApproveConfig(
    bool Enabled,
    bool All,
    IReadOnlyList<DshAutoApproveWorkspace> Workspaces,
    IReadOnlyList<DshAutoApproveSession> Sessions,
    long Revision = 0,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null,
    DshAutoApproveStats? Stats = null,
    IReadOnlyList<DshAutoApproveAudit>? Recent = null,
    string? Error = null,
    string? RelayVersion = null,
    string? File = null);

/// <summary>Reads and replaces the desktop relay's auto-approval rules.</summary>
public interface IAutoApproveClient
{
    /// <summary>Current rules, revision, counters and recent automatic decisions.</summary>
    Task<DshAutoApproveConfig> GetAutoApproveAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the rules on the desktop; returns the normalized saved document.</summary>
    Task<DshAutoApproveConfig> SaveAutoApproveAsync(
        DshAutoApproveConfig config,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------------------------------
// M7: compact relay-owned session list (relay 0.6.0+) + follow-baseline seed for the App's local cache.
// Implemented by DshClient in addition to IDshClient; the UI may cast to these interfaces. A relay without
// the compact route is handled inside the implementation: it silently falls back to
// IDshClient.ListSessionsAsync and reports UsedCompactEndpoint=false.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// One session-list read from the compact relay endpoint (<c>GET /__relay/sessions/summary</c>) — or from the
/// official <c>session/list</c> RPC when the relay answered with something the compact path cannot use
/// (old relay, unknown schema, malformed payload).
/// </summary>
/// <param name="Items">Session rows, trimmed to the fields the list UI renders.</param>
/// <param name="Cursor">Opaque snapshot cursor from the relay, when it provided one.</param>
/// <param name="UsedCompactEndpoint">
/// True when the rows came from the compact relay route; false when the client silently fell back to
/// <see cref="IDshClient.ListSessionsAsync"/> (a caller that wants the "old relay" hint can surface this).
/// </param>
/// <param name="SchemaVersion">Compact payload schema version (<c>1</c>); <c>0</c> on the fallback path.</param>
/// <param name="TotalCount">
/// Rows the relay counted before hiding child (subagent) sessions; on the fallback path, the number of rows
/// returned (the caller can count hidden subagents itself when it opts into them).
/// </param>
/// <param name="HiddenSubagentCount">Rows hidden by the relay's default subagent filter (0 on the fallback path).</param>
public sealed record SessionListSnapshot(
    IReadOnlyList<SessionSummary> Items,
    string? Cursor,
    bool UsedCompactEndpoint,
    int SchemaVersion,
    int TotalCount = 0,
    int HiddenSubagentCount = 0);

/// <summary>
/// Read-only compact session list served by the relay (one small JSON row per session instead of the full
/// <c>session/list</c> projection payload). Implemented by <c>DshClient</c>; callers must tolerate
/// <see cref="SessionListSnapshot.UsedCompactEndpoint"/> being false.
/// </summary>
public interface ISessionSummaryClient
{
    /// <summary>
    /// Reads the session list through the compact relay route, falling back to
    /// <see cref="IDshClient.ListSessionsAsync"/> when the relay does not support it. Only transport
    /// failures throw; every "this relay cannot serve the compact schema" outcome is a silent fallback.
    /// </summary>
    Task<SessionListSnapshot> ListSessionSummariesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Seeds and inspects the in-memory <c>session/follow</c> baseline. The App persists the baseline of its local
/// session cache and seeds it before opening a session, so the follow pump starts after the cached events
/// instead of replaying them from a fresh snapshot.
/// Implemented by <c>DshClient</c> in addition to <see cref="ISessionFollowClient"/>.
/// </summary>
public interface ISessionBaselineClient
{
    /// <summary>
    /// Seeds the baseline for <paramref name="sessionId"/>. Seeding never moves an existing baseline
    /// backwards: the stored seq/cursor keep the larger of the seed and the current value.
    /// </summary>
    void SeedFollowBaseline(string sessionId, long lastSeq, long cursor);

    /// <summary>
    /// Reads the current baseline. Returns false (and leaves both outs at <c>-1</c>) when this client has not
    /// seen the session yet.
    /// </summary>
    bool TryGetFollowBaseline(string sessionId, out long lastSeq, out long cursor);
}

// ---------------------------------------------------------------------------------------------------
// M8: session model + reasoning-effort selection (`session/modelCatalog` + `session/selectModel`).
// Implemented by DshClient in addition to IDshClient; the UI may cast to IModelClient.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// One model choice: a provider route id, a model id inside that route and, when the model supports
/// reasoning, the effort id. A <c>null</c> <paramref name="ReasoningEffort"/> means "adapter default"
/// (the wire field is omitted, so the provider's <c>defaultEffort</c> applies).
/// </summary>
/// <param name="Provider">Provider route id — exactly <c>groups[].id</c> in the catalog, never the display name.</param>
/// <param name="Model">Model id inside <see cref="Provider"/>.</param>
/// <param name="ReasoningEffort">Effort id from the model's <c>reasoning.efforts[].id</c>; null = unspecified.</param>
public sealed record DshModelSelection(string Provider, string Model, string? ReasoningEffort = null);

/// <summary>One reasoning-effort option of a catalog model (e.g. <c>low</c>/<c>high</c>/<c>max</c>).</summary>
public sealed record DshModelEffort(string Id, string Name, string? Description = null);

/// <summary>
/// Reasoning metadata of a catalog model. Present only when the adapter reports reasoning support; a model
/// without it cannot take a <c>reasoningEffort</c>.
/// </summary>
public sealed record DshModelReasoning(IReadOnlyList<DshModelEffort> Efforts, string? DefaultEffort = null);

/// <summary>
/// Purely presentational catalog badge (e.g. "New"). <see cref="Text"/> is already localised by the parser
/// (the wire allows a plain string or an i18n object; zh is preferred, then en, then any language).
/// </summary>
public sealed record DshModelBadge(string Text, string Tone);

/// <summary>One model row of a catalog group.</summary>
public sealed record DshModelDescriptor(
    string Id,
    string Name,
    string? Description = null,
    DshModelReasoning? Reasoning = null,
    DshModelBadge? Badge = null);

/// <summary>One provider group of the catalog; <see cref="Id"/> is the value to send as <c>provider</c>.</summary>
public sealed record DshModelGroup(string Id, string Name, IReadOnlyList<DshModelDescriptor> Models);

/// <summary>One provider whose catalog load failed (shown collapsed in the picker; other groups still work).</summary>
public sealed record DshModelFailure(string Id, string Name, string Message);

/// <summary>
/// Host-level model directory from <c>session/modelCatalog</c>.
/// <see cref="Default"/> is the deployment-wide default (desktop setting), NOT the current session's choice —
/// the session's value comes from <c>projections.values.modelSelection.next ?? lastUsed</c>.
/// </summary>
public sealed record DshModelCatalog(
    DshModelSelection? Default,
    IReadOnlyList<string> RoutableProviders,
    IReadOnlyList<DshModelGroup> Groups,
    IReadOnlyList<DshModelFailure> Failures);

/// <summary>How far the connected desktop supports the model endpoints.</summary>
public enum DshModelCatalogSupport
{
    /// <summary>The catalog loaded (or was served from the connection-generation cache).</summary>
    Supported,

    /// <summary>The desktop DSH is too old / has no such endpoint; hide the picker silently.</summary>
    Unsupported,

    /// <summary>The endpoints exist but this attempt failed (network, timeout, server error); offer retry.</summary>
    Unavailable
}

/// <summary>
/// Result of <see cref="IModelClient.GetModelCatalogAsync"/>: never throws for ordinary failures, so the UI can
/// distinguish "old DSH" (<see cref="DshModelCatalogSupport.Unsupported"/>) from "try again"
/// (<see cref="DshModelCatalogSupport.Unavailable"/>).
/// </summary>
public sealed record DshModelCatalogResult(
    DshModelCatalog? Catalog,
    DshModelCatalogSupport Support,
    string? Message = null);

/// <summary>
/// Session-local model + reasoning-effort selection. Implemented by <c>DshClient</c> in addition to
/// <see cref="IDshClient"/>; the UI casts to it, so <see cref="IDshClient"/> stays minimal.
/// </summary>
/// <remarks>
/// The selection applies to the <b>next</b> step that has not started preparing yet; a running turn keeps its
/// current route. Selecting also persists the desktop-wide default model (official endpoint behaviour).
/// </remarks>
public interface IModelClient
{
    /// <summary>
    /// Loads the host model directory. Cached per connection generation; pass <paramref name="forceRefresh"/>
    /// to bypass the cache (retry button / reconnect). Endpoint-missing hosts return
    /// <see cref="DshModelCatalogSupport.Unsupported"/>; other failures return
    /// <see cref="DshModelCatalogSupport.Unavailable"/> with a displayable <see cref="DshModelCatalogResult.Message"/>.
    /// </summary>
    Task<DshModelCatalogResult> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the session's model selection (<c>session/selectModel</c>) and returns the server-normalised
    /// <c>selected</c> value. Pass <paramref name="reasoningEffort"/> as <c>null</c> to leave the effort to the
    /// provider default (the wire field is omitted). Server failures (e.g. <c>session/model-unavailable</c>,
    /// <c>session/writer-held</c>) surface as <see cref="DshRemoteException"/>.
    /// </summary>
    Task<DshModelSelection> SelectModelAsync(
        string sessionId,
        string provider,
        string model,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default);
}
