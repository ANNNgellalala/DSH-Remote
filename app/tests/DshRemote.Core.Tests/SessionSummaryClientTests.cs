using System.Net;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

/// <summary>
/// Compact relay-owned session list (<c>GET /__relay/sessions/summary</c>) and the follow-baseline seed used by
/// the App's local session cache. The compact endpoint is relay 0.6.0+ only: every unusable response must
/// silently fall back to the official <c>session/list</c> RPC.
/// </summary>
public sealed class SessionSummaryClientTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private const string CompactJson = """
    {"ok":true,"schema":1,"fields":["sessionId","updatedAt","running","blank","agentAvailable","title","cwd","model","lastPromptAt","parentSessionId","origin"],
     "generatedAt":"2026-11-12T05:00:00.000Z",
     "cursor":"cursor-1",
     "counts":{"total":3,"hiddenSubagents":1,"returned":2},
     "items":[
       {"sessionId":"session-1","updatedAt":101,"running":true,"blank":false,"agentAvailable":true,
        "title":"Fix the build","cwd":"D:\\Code\\one","model":"deepseek-v4","lastPromptAt":100,
        "parentSessionId":null,"origin":null,
        "projections":{"ignored":true}},
       {"sessionId":"session-2","updatedAt":202,"running":false,"blank":true,"agentAvailable":false,
        "title":"Child task","cwd":"D:\\Code\\two","model":null,"lastPromptAt":201,
        "parentSessionId":"session-1","origin":"subagent"}
     ]}
    """;

    private const string FallbackListJson = """
    {"items":[
      {"sessionId":"fallback-1","updatedAt":7,"running":true,"agentAvailable":true,"blank":false,
       "projections":{"values":{"title":"Fallback title","workingDirectory":"D:\\fallback"}}}
    ]}
    """;

    private static async Task<DshClient> ConnectAsync(RecordingHttpHandler handler, List<string>? logs = null)
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var client = new DshClient(new DshClientOptions
        {
            Launcher = launcher,
            HttpHandlerFactory = () => handler,
            Delay = (_, _) => Task.CompletedTask,
            Log = logs is null ? null : logs.Add,
            ClientTimeZone = "Asia/Shanghai"
        });
        await client.ConnectAsync(Pairing);
        return client;
    }

    /// <summary>Compact route answers <paramref name="compact"/>, the official list answers the fallback payload.</summary>
    private static RecordingHttpHandler FallbackHandler(Func<HttpResponseMessage> compact)
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/__relay/sessions/summary", StringComparison.Ordinal)
            ? compact()
            : RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, FallbackListJson));
        return handler;
    }

    private static HttpResponseMessage CompactResponse(string json) => RecordingHttpHandler.JsonResponse(json);

    private static async Task<SessionListSnapshot> ListThroughFallbackAsync(HttpResponseMessage compactResponse)
    {
        var handler = FallbackHandler(() => compactResponse);
        await using var client = await ConnectAsync(handler);
        var snapshot = await client.ListSessionSummariesAsync();

        // The official RPC must have answered: one compact GET followed by one session/list POST.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/__relay/sessions/summary", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/session/list", handler.Requests[1].Uri.AbsolutePath);
        return snapshot;
    }

    private static void AssertFallbackResult(SessionListSnapshot snapshot)
    {
        Assert.False(snapshot.UsedCompactEndpoint);
        Assert.Equal(0, snapshot.SchemaVersion);
        Assert.Null(snapshot.Cursor);
        Assert.Equal(0, snapshot.HiddenSubagentCount);
        Assert.Equal(snapshot.Items.Count, snapshot.TotalCount);
        Assert.Equal("fallback-1", Assert.Single(snapshot.Items).SessionId);
    }

    // ---- compact success ----------------------------------------------------------------------------

    [Fact]
    public async Task ListSessionSummariesAsync_ParsesCompactPayloadAndSendsBearerWithAcceptJson()
    {
        var handler = new RecordingHttpHandler { Responder = _ => CompactResponse(CompactJson) };
        var logs = new List<string>();
        await using var client = await ConnectAsync(handler, logs);

        var snapshot = await client.ListSessionSummariesAsync();

        Assert.True(snapshot.UsedCompactEndpoint);
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("cursor-1", snapshot.Cursor);
        Assert.Equal(3, snapshot.TotalCount);
        Assert.Equal(1, snapshot.HiddenSubagentCount);
        Assert.Equal(2, snapshot.Items.Count);

        var first = snapshot.Items[0];
        Assert.Equal("session-1", first.SessionId);
        Assert.Equal("Fix the build", first.Title);
        Assert.Equal("D:\\Code\\one", first.Cwd);
        Assert.Equal("deepseek-v4", first.Model);
        Assert.True(first.Running);
        Assert.True(first.AgentAvailable);
        Assert.False(first.Blank);
        Assert.Equal(101, first.UpdatedAt);
        Assert.Equal(100, first.LastPromptAt);
        Assert.Null(first.ParentSessionId);
        Assert.Null(first.Origin);

        var second = snapshot.Items[1];
        Assert.Equal("session-2", second.SessionId);
        Assert.Null(second.Model);
        Assert.False(second.Running);
        Assert.True(second.Blank);
        Assert.Equal("session-1", second.ParentSessionId);
        Assert.Equal("subagent", second.Origin);

        // The compact route alone answered: no session/list RPC on the success path.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/__relay/sessions/summary", request.Uri.AbsolutePath);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.Equal("application/json", request.Accept);
        // The public gateway routes by the owner cookie; both hops must be covered.
        Assert.Equal("dsh-auth-test=cookie-1", request.Cookie);
        Assert.Contains(logs, line => line.Contains("session summary: compact 2/3 items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListSessionSummariesAsync_MissingFieldsFallBackToDocumentedDefaults()
    {
        const string minimal = """
        {"ok":true,"schema":1,
         "items":[{"sessionId":"session-1","unknown":{"nested":true}},
                  {"sessionId":"","running":true},
                  {"updatedAt":9}]}
        """;
        var handler = FallbackHandler(() => CompactResponse(minimal));
        await using var client = await ConnectAsync(handler);

        var snapshot = await client.ListSessionSummariesAsync();

        Assert.True(snapshot.UsedCompactEndpoint);
        Assert.Null(snapshot.Cursor);
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Equal(0, snapshot.HiddenSubagentCount);
        // Rows without a usable sessionId are dropped (same tolerance as session/list).
        var item = Assert.Single(snapshot.Items);
        Assert.Equal("session-1", item.SessionId);
        Assert.Null(item.Title);
        Assert.Null(item.Cwd);
        Assert.Null(item.Model);
        Assert.Null(item.ParentSessionId);
        Assert.Null(item.Origin);
        Assert.False(item.Running);
        Assert.False(item.AgentAvailable);
        Assert.False(item.Blank);
        Assert.Equal(0, item.UpdatedAt);
        Assert.Equal(0, item.LastPromptAt);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_ParsesEmptyListAndNullCursor()
    {
        var handler = FallbackHandler(() => CompactResponse("""{"ok":true,"schema":1,"cursor":null,"items":[]}"""));
        await using var client = await ConnectAsync(handler);

        var snapshot = await client.ListSessionSummariesAsync();

        Assert.True(snapshot.UsedCompactEndpoint);
        Assert.Empty(snapshot.Items);
        Assert.Null(snapshot.Cursor);
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Equal(0, snapshot.HiddenSubagentCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_ParsesNumericCursorAsText()
    {
        var handler = FallbackHandler(() => CompactResponse("""{"ok":true,"schema":1,"cursor":42,"items":[]}"""));
        await using var client = await ConnectAsync(handler);

        var snapshot = await client.ListSessionSummariesAsync();

        Assert.Equal("42", snapshot.Cursor);
        Assert.True(snapshot.UsedCompactEndpoint);
    }

    // ---- fallbacks ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task ListSessionSummariesAsync_UnroutableStatusFallsBackToSessionList(HttpStatusCode status)
    {
        var snapshot = await ListThroughFallbackAsync(new HttpResponseMessage(status));
        AssertFallbackResult(snapshot);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_UnknownSchemaFallsBackToSessionList()
    {
        var snapshot = await ListThroughFallbackAsync(CompactResponse("""{"ok":true,"schema":2,"items":[]}"""));
        AssertFallbackResult(snapshot);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_MalformedJsonFallsBackToSessionList()
    {
        var snapshot = await ListThroughFallbackAsync(CompactResponse("<html><body>bad gateway</body></html>"));
        AssertFallbackResult(snapshot);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_NonArrayItemsFallsBackToSessionList()
    {
        var snapshot = await ListThroughFallbackAsync(
            CompactResponse("""{"ok":true,"schema":1,"items":{"sessionId":"session-1"}}"""));
        AssertFallbackResult(snapshot);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_NonObjectPayloadFallsBackToSessionList()
    {
        var snapshot = await ListThroughFallbackAsync(CompactResponse("[1,2,3]"));
        AssertFallbackResult(snapshot);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_OkFalseFallsBackToSessionList()
    {
        var snapshot = await ListThroughFallbackAsync(CompactResponse("""{"ok":false,"error":"disabled"}"""));
        AssertFallbackResult(snapshot);
    }

    [Fact]
    public async Task ListSessionSummariesAsync_TransportFailureSurfacesAsTransportError()
    {
        var handler = new RecordingHttpHandler
        {
            Responder = _ => throw new HttpRequestException("connection reset")
        };
        await using var client = await ConnectAsync(handler);

        var error = await Assert.ThrowsAsync<DshRemoteException>(() => client.ListSessionSummariesAsync());

        Assert.Equal(DshRemoteException.Codes.Transport, error.Code);
        Assert.Contains("compact session list", error.Message, StringComparison.Ordinal);
    }

    // ---- follow-baseline seed -----------------------------------------------------------------------

    [Fact]
    public async Task FollowBaselineSeed_RoundTripsThroughTryGet()
    {
        await using var client = new DshClient(new DshClientOptions { ClientTimeZone = "Asia/Shanghai" });

        Assert.False(client.TryGetFollowBaseline("session-1", out var lastSeq, out var cursor));
        Assert.Equal(-1, lastSeq);
        Assert.Equal(-1, cursor);

        client.SeedFollowBaseline("session-1", lastSeq: 10, cursor: 20);

        Assert.True(client.TryGetFollowBaseline("session-1", out lastSeq, out cursor));
        Assert.Equal(10, lastSeq);
        Assert.Equal(20, cursor);

        // Unknown sessions stay unknown: the seed is per session id.
        Assert.False(client.TryGetFollowBaseline("session-2", out _, out _));
    }

    [Fact]
    public async Task FollowBaselineSeed_NeverMovesAnExistingBaselineBackwards()
    {
        await using var client = new DshClient(new DshClientOptions { ClientTimeZone = "Asia/Shanghai" });

        client.SeedFollowBaseline("session-1", lastSeq: 10, cursor: 20);
        // A stale local cache must not make the pump re-deliver events it already covered.
        client.SeedFollowBaseline("session-1", lastSeq: 5, cursor: 15);

        Assert.True(client.TryGetFollowBaseline("session-1", out var lastSeq, out var cursor));
        Assert.Equal(10, lastSeq);
        Assert.Equal(20, cursor);

        // Advancing is allowed.
        client.SeedFollowBaseline("session-1", lastSeq: 12, cursor: 25);

        Assert.True(client.TryGetFollowBaseline("session-1", out lastSeq, out cursor));
        Assert.Equal(12, lastSeq);
        Assert.Equal(25, cursor);
    }

    [Fact]
    public async Task FollowBaselineSeed_RejectsEmptySessionId()
    {
        await using var client = new DshClient(new DshClientOptions { ClientTimeZone = "Asia/Shanghai" });

        Assert.Throws<ArgumentException>(() => client.SeedFollowBaseline("", 1, 2));
    }

    // ---- defaults -----------------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultFollowMaxMessagesIsOne()
    {
        // The live pump must not re-download the history the caller already loaded (1.44 MB snapshot).
        Assert.Equal(1, new DshClientOptions().FollowMaxMessages);
    }

    [Fact]
    public void Options_OpenSessionDefaultBudgetStaysAtEighty()
    {
        // The explicit first-screen open keeps its own 80-message default.
        Assert.Equal(80, DefaultValueOf(typeof(DshClient), nameof(DshClient.OpenSessionAsync), parameterIndex: 1));
        Assert.Equal(
            80,
            DefaultValueOf(typeof(ISessionFollowClient), nameof(ISessionFollowClient.OpenSessionAsync), parameterIndex: 1));
    }

    private static object? DefaultValueOf(Type type, string methodName, int parameterIndex)
    {
        var method = type.GetMethod(methodName) ?? throw new InvalidOperationException($"{type.Name}.{methodName} missing");
        return method.GetParameters()[parameterIndex].DefaultValue;
    }
}
