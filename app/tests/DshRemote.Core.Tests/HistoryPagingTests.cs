using System.Net;
using System.Text;
using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class HistoryPagingTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private static DshClientOptions Options(FakeLauncher launcher, HttpMessageHandler handler, List<string>? logs = null) =>
        new()
        {
            Launcher = launcher,
            HttpHandlerFactory = () => handler,
            Delay = (_, _) => Task.CompletedTask,
            Log = logs is null ? null : logs.Add,
            ClientTimeZone = "Asia/Shanghai"
        };

    private static async Task<(DshClient Client, FakeRemoteSocket Socket, RecordingHttpHandler Handler)> ConnectedAsync(
        List<string>? logs = null)
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        var client = new DshClient(Options(launcher, handler, logs));
        await client.ConnectAsync(Pairing);
        return (client, socket, handler);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("condition was not reached in time");
            }

            await Task.Delay(10);
        }
    }

    private static string EventJson(long seq, string type = "turn/start") =>
        "{\"type\":\"event\",\"event\":{\"type\":\"" + type + "\",\"seq\":" + seq + ",\"time\":" + (seq * 10) + ",\"data\":{}}}";

    private static string PageJson(long cursor, IEnumerable<long> seqs, bool hasMore)
    {
        var records = string.Join(",", seqs.Select(seq => EventJson(seq)));
        return "{\"cursor\":" + cursor + ",\"hasMore\":" + (hasMore ? "true" : "false") + ",\"records\":[" + records + "]}";
    }

    private static JsonElement RequestArgs(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request").Clone();
    }

    [Fact]
    public async Task GetLatestHistoryAsync_OmitsBeforeSeqAndComputesPagingFields()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, PageJson(120, new long[] { 118, 119, 120 }, hasMore: true)));

        var page = await client.GetLatestHistoryAsync("session-1", maxMessages: 30);

        Assert.Equal(new long[] { 118, 119, 120 }, page.Records.Select(r => r.Seq));
        Assert.Equal(120, page.Cursor);
        Assert.True(page.HasMore);
        Assert.True(page.HasMoreOlder);
        Assert.Equal(118, page.BeforeSeq);

        using var requestArgs = JsonDocument.Parse(handler.Requests[0].Body);
        var request = requestArgs.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.False(request.TryGetProperty("beforeSeq", out _));
        Assert.Equal(30, request.GetProperty("maxMessages").GetInt32());
        Assert.Equal("session-1", request.GetProperty("address").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task GetLatestHistoryAsync_EmptySessionHasNoBeforeSeq()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, """{"cursor":0,"hasMore":false,"records":[]}"""));

        var page = await client.GetLatestHistoryAsync("session-1");

        Assert.Empty(page.Records);
        Assert.False(page.HasMoreOlder);
        Assert.Null(page.BeforeSeq);
    }

    [Fact]
    public async Task GetOlderHistoryAsync_SendsBeforeSeqAndDropsInclusiveBoundary()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        // Simulate an inclusive server: the boundary event (118) comes back inside the page.
        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, PageJson(118, new long[] { 115, 116, 117, 118 }, hasMore: true)));

        var page = await client.GetOlderHistoryAsync("session-1", beforeSeq: 118, maxMessages: 30);

        Assert.Equal(new long[] { 115, 116, 117 }, page.Records.Select(r => r.Seq));
        Assert.Equal(118, page.Cursor); // server-reported log cut, not the page max
        Assert.True(page.HasMoreOlder);
        Assert.Equal(115, page.BeforeSeq);

        using var requestArgs = JsonDocument.Parse(handler.Requests[0].Body);
        var request = requestArgs.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal(118, request.GetProperty("beforeSeq").GetInt64());
    }

    [Fact]
    public async Task GetOlderHistoryAsync_SortsAndDeduplicatesRecords()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, PageJson(117, new long[] { 117, 115, 116, 116 }, hasMore: false)));

        var page = await client.GetOlderHistoryAsync("session-1", beforeSeq: 118);

        Assert.Equal(new long[] { 115, 116, 117 }, page.Records.Select(r => r.Seq));
        Assert.Equal(115, page.BeforeSeq);
        Assert.False(page.HasMoreOlder);
    }

    [Fact]
    public async Task GetOlderHistoryAsync_NonPositiveBeforeSeqReturnsEmptyWithoutRequest()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        var page = await client.GetOlderHistoryAsync("session-1", beforeSeq: 0);

        Assert.Empty(page.Records);
        Assert.Null(page.BeforeSeq);
        Assert.False(page.HasMoreOlder);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetOlderHistoryAsync_EmptyOlderPageClampsCursor()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, """{"cursor":4,"hasMore":false,"records":[]}"""));

        var page = await client.GetOlderHistoryAsync("session-1", beforeSeq: 5);

        Assert.Empty(page.Records);
        Assert.Null(page.BeforeSeq);
        Assert.False(page.HasMoreOlder);
        Assert.Equal(4, page.Cursor);
    }

    [Fact]
    public async Task GetOlderHistoryAsync_RepeatedRequestIsStable()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, PageJson(99, Enumerable.Range(90, 10).Select(i => (long)i), hasMore: true)));

        var first = await client.GetOlderHistoryAsync("session-1", beforeSeq: 100, maxMessages: 10);
        var second = await client.GetOlderHistoryAsync("session-1", beforeSeq: 100, maxMessages: 10);

        Assert.Equal(first.Records.Select(r => r.Seq), second.Records.Select(r => r.Seq));
        Assert.Equal(first.BeforeSeq, second.BeforeSeq);
        Assert.Equal(first.Cursor, second.Cursor);
    }

    [Fact]
    public async Task FullWalk_PagesBackwardsContiguouslyWithoutDuplicatesOrHoles()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        const long lastSeq = 99;
        const int pageSize = 10;
        handler.Responder = request =>
        {
            var requestArgs = RequestArgs(request.Body);
            var hasBefore = requestArgs.TryGetProperty("beforeSeq", out var beforeElement);
            var end = hasBefore ? Math.Min(beforeElement.GetInt64() - 1, lastSeq) : lastSeq;
            var start = Math.Max(0, end - pageSize + 1);
            var seqs = Enumerable.Range((int)start, (int)(end - start + 1)).Select(i => (long)i);
            var hasMore = start > 0;
            return RecordingHttpHandler.JsonResponse(
                TestJson.SuccessFromRequest(request.Body, PageJson(end, seqs, hasMore)));
        };

        var latest = await client.GetLatestHistoryAsync("session-1", pageSize);
        var all = latest.Records.Select(r => r.Seq).ToList();
        var page = latest;
        var pages = 1;
        while (page.HasMoreOlder && page.BeforeSeq is { } before)
        {
            page = await client.GetOlderHistoryAsync("session-1", before, pageSize);
            Assert.NotEmpty(page.Records);
            all.InsertRange(0, page.Records.Select(r => r.Seq));
            pages++;
            Assert.True(pages < 50, "pagination did not terminate");
        }

        Assert.Equal(Enumerable.Range(0, (int)lastSeq + 1).Select(i => (long)i), all);
        Assert.Equal(10, pages);
        Assert.False(page.HasMoreOlder);
        Assert.Equal(0, page.BeforeSeq);
        Assert.Equal(10, handler.Requests.Count); // one request per page, no retries
    }

    [Fact]
    public async Task GetHistoryAsync_DelegatesToLatestAndOlder()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, PageJson(9, new long[] { 8, 9 }, hasMore: true)));

        var latest = await client.GetHistoryAsync("session-1", beforeSeq: null, maxMessages: 5);
        Assert.False(handler.Requests[0].Body.Contains("\"beforeSeq\"", StringComparison.Ordinal));
        Assert.Equal(8, latest.BeforeSeq);

        var older = await client.GetHistoryAsync("session-1", beforeSeq: 8, maxMessages: 5);
        Assert.Contains("\"beforeSeq\":8", handler.Requests[1].Body);
        Assert.Equal(new long[] { 8, 9 }, latest.Records.Select(r => r.Seq));
        Assert.Empty(older.Records); // the fake no longer has anything strictly older than 8
        Assert.Equal(9, older.Cursor); // server-reported log cut
    }

    [Fact]
    public void SessionHistoryPage_ThreeArgumentConstructionStaysCompatible()
    {
        var page = new SessionHistoryPage(Array.Empty<SessionEventRecord>(), 1, false);
        Assert.False(page.HasMoreOlder);
        Assert.Null(page.BeforeSeq);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task OpenSessionLiteAsync_ReturnsLatestPageAndState()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/session/events", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(
                    TestJson.SuccessFromRequest(request.Body, PageJson(12, new long[] { 10, 11, 12 }, hasMore: true)));
            }

            if (path.EndsWith("/session/state", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    """{"cursor":12,"active":true,"running":false,"queue":null,"pendingInput":[]}"""));
            }

            return RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var lite = await client.OpenSessionLiteAsync("session-1", maxMessages: 30);

        Assert.Equal(new long[] { 10, 11, 12 }, lite.Page.Records.Select(r => r.Seq));
        Assert.NotNull(lite.State);
        Assert.Equal(12, lite.State!.Cursor);
        Assert.Equal(2, handler.Requests.Count); // one events read + one state read
    }

    [Fact]
    public async Task OpenSessionLiteAsync_ToleratesStateFailure()
    {
        var (client, _, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/session/events", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(
                    TestJson.SuccessFromRequest(request.Body, PageJson(3, new long[] { 3 }, hasMore: false)));
            }

            return RecordingHttpHandler.JsonResponse(
                TestJson.Failure(TestJson.RpcIdOf(request.Body), "session/agent-busy", "child session"));
        };

        var lite = await client.OpenSessionLiteAsync("session-1");

        Assert.Null(lite.State);
        Assert.Single(lite.Page.Records);
    }

    [Fact]
    public async Task OpenSessionLiteAsync_BaselineDedupesTheFollowSnapshot()
    {
        var (client, socket, handler) = await ConnectedAsync();
        await using var clientScope = client;

        handler.Responder = request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/session/events", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(
                    TestJson.SuccessFromRequest(request.Body, PageJson(12, new long[] { 10, 11, 12 }, hasMore: true)));
            }

            return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                request.Body,
                """{"cursor":12,"active":true,"running":false,"queue":null,"pendingInput":[]}"""));
        };

        var lite = await client.OpenSessionLiteAsync("session-1", maxMessages: 30);
        Assert.Equal(12, lite.Page.Cursor);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => socket.SentFrames.Any(f => f.Contains("\"endpoint\":\"session/follow\"", StringComparison.Ordinal)));

            var open = socket.SentFrames.Last(f => f.Contains("\"type\":\"open\"", StringComparison.Ordinal));
            using var openDocument = JsonDocument.Parse(open);
            var streamId = openDocument.RootElement.GetProperty("streamId").GetString()!;

            string Item(string inner) => "{\"type\":\"item\",\"streamId\":\"" + streamId + "\",\"value\":" + inner + "}";

            // The follow snapshot repeats 10..13; only 13 is new relative to the lite baseline.
            var records = string.Join(",", new long[] { 10, 11, 12, 13 }.Select(seq => EventJson(seq)));
            socket.Push(Item("{\"type\":\"snapshot\",\"header\":{\"version\":1,\"id\":\"s\",\"createdAt\":1,\"isSeeded\":false},"
                             + "\"cursor\":13,\"hasMore\":false,\"records\":[" + records + "],"
                             + "\"projections\":{\"asOfSeq\":13,\"values\":{}}}"));

            Assert.True(await first);
            Assert.Equal(13, enumerator.Current.Seq);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
