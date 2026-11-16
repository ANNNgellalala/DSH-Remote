using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class FollowClientTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private static DshClientOptions Options(
        FakeLauncher launcher,
        HttpMessageHandler handler,
        List<string>? logs = null,
        TimeSpan? followAcquireTimeout = null) =>
        new()
        {
            Launcher = launcher,
            HttpHandlerFactory = () => handler,
            Delay = (_, _) => Task.CompletedTask,
            Log = logs is null ? null : logs.Add,
            ClientTimeZone = "Asia/Shanghai",
            FollowAcquireTimeout = followAcquireTimeout ?? TimeSpan.FromSeconds(3)
        };

    private static int OpenCount(FakeRemoteSocket socket) =>
        socket.SentFrames.Count(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal));

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

    /// <summary>The streamId of the most recent <c>open</c> frame (stream ids increment per connection).</summary>
    private static string LastStreamId(FakeRemoteSocket socket)
    {
        var open = socket.SentFrames.LastOrDefault(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal));
        if (open is null)
        {
            throw new InvalidOperationException("no open frame was sent yet");
        }

        using var document = JsonDocument.Parse(open);
        return document.RootElement.GetProperty("streamId").GetString()!;
    }

    private static string Item(FakeRemoteSocket socket, string innerJson) =>
        "{\"type\":\"item\",\"streamId\":\"" + LastStreamId(socket) + "\",\"value\":" + innerJson + "}";

    private static string Snapshot(int cursor, params (long Seq, string Type)[] events)
    {
        var records = string.Join(",", events.Select(e =>
            "{\"type\":\"event\",\"event\":{\"type\":\"" + e.Type + "\",\"seq\":" + e.Seq + ",\"time\":" + (e.Seq * 10) + ",\"data\":{}}}"));
        return "{\"type\":\"snapshot\",\"header\":{\"version\":1,\"id\":\"s\",\"createdAt\":1,\"isSeeded\":false},"
               + "\"cursor\":" + cursor + ",\"hasMore\":false,\"records\":[" + records + "],"
               + "\"projections\":{\"asOfSeq\":" + cursor + ",\"values\":{}}}";
    }

    private static string FollowEvent(long seq, string type = "turn/start") =>
        "{\"type\":\"event\",\"event\":{\"type\":\"" + type + "\",\"seq\":" + seq + ",\"time\":" + (seq * 10) + ",\"data\":{}}}";

    private static string StateJson(string body = """{"cursor":5,"active":true,"running":false,"queue":null,"pendingInput":[]}""") => body;

    [Fact]
    public async Task OpenSessionAsync_ReturnsSnapshotAndRuntimeState()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/session/state", StringComparison.Ordinal)
            ? RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, StateJson()))
            : RecordingHttpHandler.JsonResponse("{}", System.Net.HttpStatusCode.NotFound);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var openTask = client.OpenSessionAsync("session-1", maxMessages: 4);
        await WaitUntilAsync(() => OpenCount(socket) >= 1);
        socket.Push(Item(socket, Snapshot(5, (4, "turn/start"), (5, "assistant/message"))));

        var snapshot = await openTask;
        Assert.Equal(new long[] { 4, 5 }, snapshot.Events.Select(e => e.Seq));
        Assert.Equal(5, snapshot.Cursor);
        Assert.False(snapshot.HasMore);
        Assert.NotNull(snapshot.State);
        Assert.True(snapshot.State!.Active);

        using var openDocument = JsonDocument.Parse(socket.SentFrames[0]);
        Assert.Equal("session/follow", openDocument.RootElement.GetProperty("endpoint").GetString());
        var request = openDocument.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("session-1", request.GetProperty("address").GetProperty("sessionId").GetString());
        Assert.Equal(4, request.GetProperty("maxMessages").GetInt32());
        Assert.Equal(JsonValueKind.True, request.GetProperty("assistantStream").ValueKind);
    }

    [Fact]
    public async Task FollowEventsAsync_EmitsSnapshotBaselineThenLiveEventsWithoutDuplicates()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, Snapshot(5, (4, "turn/start"), (5, "assistant/message"))));
            Assert.True(await first);
            Assert.Equal(4, enumerator.Current.Seq);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(5, enumerator.Current.Seq);

            // A live frame that repeats 5 (duplicate) followed by 6.
            var third = enumerator.MoveNextAsync();
            socket.Push(Item(socket, FollowEvent(5)));
            socket.Push(Item(socket, FollowEvent(6, "user/message")));
            Assert.True(await third);
            Assert.Equal(6, enumerator.Current.Seq);

            // assistant-stream frames are ignored, not surfaced as SessionEvents.
            var fourth = enumerator.MoveNextAsync();
            socket.Push(Item(socket, """{"type":"assistant-stream","frame":{"type":"chunk","attemptId":"a","revision":1,"index":0,"time":1,"chunk":{"type":"text-delta","text":"x"}}}"""));
            socket.Push(Item(socket, FollowEvent(7)));
            Assert.True(await fourth);
            Assert.Equal(7, enumerator.Current.Seq);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_RepairsGapFromNewSnapshotAfterReconnect()
    {
        var launcher = new FakeLauncher();
        var firstSocket = new FakeRemoteSocket();
        var secondSocket = new FakeRemoteSocket();
        launcher.Enqueue(firstSocket);
        launcher.Enqueue(secondSocket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/session/state", StringComparison.Ordinal)
            ? RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, StateJson()))
            : RecordingHttpHandler.JsonResponse("{}", System.Net.HttpStatusCode.NotFound);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        // Baseline through the public open API (cursor 5).
        var openTask = client.OpenSessionAsync("session-1");
        await WaitUntilAsync(() => OpenCount(firstSocket) >= 1);
        firstSocket.Push(Item(firstSocket, Snapshot(5, (4, "turn/start"), (5, "assistant/message"))));
        await openTask;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(firstSocket) >= 2);
            // Opening snapshot repeats the baseline; nothing new to emit, so the first yield is live event 6.
            firstSocket.Push(Item(firstSocket, Snapshot(5, (4, "turn/start"), (5, "assistant/message"))));
            firstSocket.Push(Item(firstSocket, FollowEvent(6)));
            Assert.True(await first);
            Assert.Equal(6, enumerator.Current.Seq);

            var second = enumerator.MoveNextAsync();
            firstSocket.Push(Item(firstSocket, FollowEvent(7)));
            Assert.True(await second);
            Assert.Equal(7, enumerator.Current.Seq);

            // Drop the socket; the supervisor reconnects with the second queued socket.
            firstSocket.Close();
            await WaitUntilAsync(() => OpenCount(secondSocket) >= 1);

            // New follow generation opens with a snapshot past the missed events: 8 and 9 arrived while offline.
            secondSocket.Push(Item(secondSocket, Snapshot(9, (7, "turn/start"), (8, "tool/call"), (9, "tool/result"))));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(8, enumerator.Current.Seq);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(9, enumerator.Current.Seq);

            // Live frames resume.
            var next = enumerator.MoveNextAsync();
            secondSocket.Push(Item(secondSocket, FollowEvent(10)));
            Assert.True(await next);
            Assert.Equal(10, enumerator.Current.Seq);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_BackfillsGapFromSessionEventsPages()
    {
        var launcher = new FakeLauncher();
        var firstSocket = new FakeRemoteSocket();
        var secondSocket = new FakeRemoteSocket();
        launcher.Enqueue(firstSocket);
        launcher.Enqueue(secondSocket);

        var historyCalls = 0;
        var handler = new RecordingHttpHandler();
        handler.Responder = request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/session/state", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    StateJson("""{"cursor":11,"active":true,"running":true,"queue":null,"pendingInput":[]}""")));
            }

            if (path.EndsWith("/session/events", StringComparison.Ordinal))
            {
                historyCalls++;
                using var document = JsonDocument.Parse(request.Body);
                var beforeSeq = document.RootElement.GetProperty("payload").GetProperty("args")
                    .GetProperty("request").GetProperty("beforeSeq").GetInt64();
                const string records = """
                [{"type":"event","event":{"type":"tool/call","seq":8,"time":80,"data":{}}},
                 {"type":"event","event":{"type":"tool/result","seq":9,"time":90,"data":{}}},
                 {"type":"event","event":{"type":"assistant/message","seq":10,"time":100,"data":{}}}]
                """;
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    "{\"cursor\":" + (beforeSeq - 1) + ",\"hasMore\":false,\"records\":" + records + "}"));
            }

            return RecordingHttpHandler.JsonResponse("{}", System.Net.HttpStatusCode.NotFound);
        };

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var openTask = client.OpenSessionAsync("session-1");
        await WaitUntilAsync(() => OpenCount(firstSocket) >= 1);
        firstSocket.Push(Item(firstSocket, Snapshot(7, (6, "turn/start"), (7, "assistant/message"))));
        await openTask;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(firstSocket) >= 2);
            firstSocket.Push(Item(firstSocket, Snapshot(7, (6, "turn/start"), (7, "assistant/message"))));
            firstSocket.Push(Item(firstSocket, FollowEvent(8)));
            Assert.True(await first);
            Assert.Equal(8, enumerator.Current.Seq);

            // Reconnect: the new snapshot only carries 11, so 9 and 10 must be back-filled from the log.
            firstSocket.Close();
            await WaitUntilAsync(() => OpenCount(secondSocket) >= 1);
            secondSocket.Push(Item(secondSocket, Snapshot(11, (11, "turn/start"))));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(9, enumerator.Current.Seq);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(10, enumerator.Current.Seq);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(11, enumerator.Current.Seq);
            Assert.True(historyCalls >= 1);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_RaisesResyncWhenGapCannotBeFilled()
    {
        var launcher = new FakeLauncher();
        var firstSocket = new FakeRemoteSocket();
        var secondSocket = new FakeRemoteSocket();
        launcher.Enqueue(firstSocket);
        launcher.Enqueue(secondSocket);

        var handler = new RecordingHttpHandler();
        handler.Responder = request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/session/events", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(request.Body);
                var beforeSeq = document.RootElement.GetProperty("payload").GetProperty("args")
                    .GetProperty("request").GetProperty("beforeSeq").GetInt64();
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    "{\"cursor\":" + (beforeSeq - 1) + ",\"hasMore\":false,\"records\":[]}"));
            }

            if (path.EndsWith("/session/state", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    StateJson("""{"cursor":12,"active":true,"running":true,"queue":null,"pendingInput":[]}""")));
            }

            return RecordingHttpHandler.JsonResponse("{}", System.Net.HttpStatusCode.NotFound);
        };

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var openTask = client.OpenSessionAsync("session-1");
        await WaitUntilAsync(() => OpenCount(firstSocket) >= 1);
        firstSocket.Push(Item(firstSocket, Snapshot(7, (7, "assistant/message"))));
        await openTask;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(firstSocket) >= 2);
            firstSocket.Push(Item(firstSocket, Snapshot(7, (7, "assistant/message"))));
            firstSocket.Push(Item(firstSocket, FollowEvent(8)));
            Assert.True(await first);
            Assert.Equal(8, enumerator.Current.Seq);

            firstSocket.Close();
            await WaitUntilAsync(() => OpenCount(secondSocket) >= 1);
            // Snapshot jumped to 12 but the durable log cannot serve 9..11.
            secondSocket.Push(Item(secondSocket, Snapshot(12, (12, "turn/start"))));

            await Assert.ThrowsAsync<SessionFollowResyncException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_SurfacesServerStreamErrorToConsumer()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, Snapshot(5, (5, "assistant/message"))));
            Assert.True(await first);

            // A server-side stream rejection on a live transport must reach the consumer promptly;
            // waiting for a new connection generation here would stall the chat forever.
            var next = enumerator.MoveNextAsync();
            socket.Push(
                "{\"type\":\"error\",\"streamId\":\"" + LastStreamId(socket)
                + "\",\"error\":{\"code\":\"session/one-subscription\",\"message\":\"already active\"}}");

            var error = await Assert.ThrowsAsync<DshRemoteException>(async () => await next);
            Assert.Equal("session/one-subscription", error.Code);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_SurfacesCleanStreamEndInsteadOfWaitingForReconnect()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, Snapshot(5, (5, "assistant/message"))));
            Assert.True(await first);

            var next = enumerator.MoveNextAsync();
            socket.Push("{\"type\":\"end\",\"streamId\":\"" + LastStreamId(socket) + "\"}");

            var error = await Assert.ThrowsAsync<DshRemoteException>(async () => await next);
            Assert.Equal(DshRemoteException.Codes.Transport, error.Code);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SteerAndUpdateQueue_UseDocumentedEnvelopes()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, """{"accepted":true}"""));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        await client.SteerAsync("session-1", "do it differently");
        await client.UpdateQueueAsync("session-1", "item-1", QueueEditAction.Edit, "edited text");
        await client.UpdateQueueAsync("session-1", "item-1", QueueEditAction.Remove);
        await client.UpdateQueueAsync("session-1", "item-1", QueueEditAction.Steer);

        Assert.Equal(4, handler.Requests.Count);

        using var steerRequest = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("session/prompt", steerRequest.RootElement.GetProperty("method").GetString());
        var steerArgs = steerRequest.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("steer", steerArgs.GetProperty("mode").GetString());
        Assert.Equal("do it differently", steerArgs.GetProperty("content")[0].GetProperty("text").GetString());

        using var editRequest = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("session/updateQueue", editRequest.RootElement.GetProperty("method").GetString());
        var editArgs = editRequest.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("item-1", editArgs.GetProperty("itemId").GetString());
        Assert.Equal("edit", editArgs.GetProperty("action").GetProperty("kind").GetString());

        using var removeRequest = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("remove", removeRequest.RootElement.GetProperty("payload").GetProperty("args")
            .GetProperty("request").GetProperty("action").GetProperty("kind").GetString());

        using var steerQueueRequest = JsonDocument.Parse(handler.Requests[3].Body);
        Assert.Equal("steer", steerQueueRequest.RootElement.GetProperty("payload").GetProperty("args")
            .GetProperty("request").GetProperty("action").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task FollowEventsAsync_NewConsumerWaitsForPreviousSubscriptionAndThenSucceeds()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstEnumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        var secondEnumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = firstEnumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, Snapshot(5, (5, "assistant/message"))));
            Assert.True(await first);
            Assert.Equal(5, firstEnumerator.Current.Seq);

            // The second consumer must wait for the first subscription's slot instead of failing instantly
            // with the "already active" error (that throw used to poison every future retry).
            var second = secondEnumerator.MoveNextAsync();
            await Task.Delay(150);
            Assert.False(second.IsCompleted);

            await firstEnumerator.DisposeAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 2);
            socket.Push(Item(socket, Snapshot(6, (6, "turn/start"))));

            Assert.True(await second);
            Assert.Equal(6, secondEnumerator.Current.Seq);
        }
        finally
        {
            await firstEnumerator.DisposeAsync();
            await secondEnumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_ReportsTypedBusyErrorWhenAnotherConsumerStaysActive()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(
            Options(launcher, handler, followAcquireTimeout: TimeSpan.FromMilliseconds(250)));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstEnumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        var secondEnumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = firstEnumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, Snapshot(5, (5, "assistant/message"))));
            Assert.True(await first);

            var second = secondEnumerator.MoveNextAsync();
            var error = await Assert.ThrowsAsync<DshRemoteException>(async () => await second);
            Assert.Equal(DshRemoteException.Codes.FollowBusy, error.Code);
        }
        finally
        {
            await firstEnumerator.DisposeAsync();
            await secondEnumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_PumpRequestsMinimalSnapshotBudgetByDefault()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);

            var open = socket.SentFrames.First(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(open);
            Assert.Equal("session/follow", document.RootElement.GetProperty("endpoint").GetString());
            var request = document.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            // The pump already has the caller's baseline; one anchor message is enough (80 re-downloaded 1.44 MB).
            Assert.Equal(1, request.GetProperty("maxMessages").GetInt32());

            socket.Push(Item(socket, Snapshot(5, (5, "assistant/message"))));
            Assert.True(await first);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FollowEventsAsync_SeededBaselineSkipsEventsTheCacheAlreadyHas()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        // The App restored its local cache: events 1..5 are already on screen.
        client.SeedFollowBaseline("session-1", lastSeq: 5, cursor: 5);
        Assert.True(client.TryGetFollowBaseline("session-1", out var seededSeq, out var seededCursor));
        Assert.Equal(5, seededSeq);
        Assert.Equal(5, seededCursor);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.FollowEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, Snapshot(6, (4, "turn/start"), (5, "assistant/message"), (6, "turn/start"))));

            // The snapshot repeats cached events 4 and 5; only the new event 6 is surfaced.
            Assert.True(await first);
            Assert.Equal(6, enumerator.Current.Seq);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
