using System.Net;
using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class DshClientTests
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

    private static string ItemFrame(string streamId, string innerJson) =>
        "{\"type\":\"item\",\"streamId\":\"" + streamId + "\",\"value\":" + innerJson + "}";

    [Fact]
    public void Options_DefaultClientTimeZone_IsIanaOrOmitted()
    {
        var options = new DshClientOptions();
        if (options.ClientTimeZone is { Length: > 0 } zone)
        {
            // The server rejects Windows ids ("China Standard Time") — only UTC or Area/Location is valid.
            Assert.True(zone == "UTC" || zone.Contains('/', StringComparison.Ordinal), $"unexpected clientTimeZone '{zone}'");
            Assert.DoesNotContain("Standard Time", zone, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConnectAsync_ReachesConnectedThenDisconnectReturnsToDisconnected()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        var states = new List<DshConnectionState>();

        await using var client = new DshClient(Options(launcher, handler));
        client.StateChanged += (_, state) => states.Add(state);

        await client.ConnectAsync(Pairing);

        Assert.Equal(DshConnectionState.Connected, client.State);
        Assert.Same(Pairing, client.Pairing);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(new[] { DshConnectionState.Connecting, DshConnectionState.Connected }, states);

        await client.DisconnectAsync();
        Assert.Equal(DshConnectionState.Disconnected, client.State);
        Assert.Contains(DshConnectionState.Disconnected, states);
    }

    [Fact]
    public async Task ConnectAsync_UnauthorizedLaunchFailsImmediately()
    {
        var launcher = new FakeLauncher { FailNextWithAuth = true };
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        var exception = await Assert.ThrowsAsync<DshRemoteException>(() => client.ConnectAsync(Pairing));

        Assert.Equal(DshRemoteException.Codes.Unauthorized, exception.Code);
        Assert.Equal(DshConnectionState.Failed, client.State);
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task SocketLoss_ReconnectsWithFreshGeneration()
    {
        var launcher = new FakeLauncher();
        var firstSocket = new FakeRemoteSocket();
        var secondSocket = new FakeRemoteSocket();
        launcher.Enqueue(firstSocket);
        launcher.Enqueue(secondSocket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request =>
            RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, """{"items":[]}"""));
        var logs = new List<string>();
        var states = new List<DshConnectionState>();

        await using var client = new DshClient(Options(launcher, handler, logs));
        client.StateChanged += (_, state) =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };

        await client.ConnectAsync(Pairing);
        Assert.Equal(DshConnectionState.Connected, client.State);

        firstSocket.Close();

        await WaitUntilAsync(() => client.State == DshConnectionState.Connected && launcher.LaunchCount >= 2);
        Assert.Equal(2, launcher.LaunchCount);
        Assert.True(firstSocket.Disposed);

        lock (states)
        {
            Assert.Contains(DshConnectionState.Reconnecting, states);
        }

        // The new generation is usable again.
        Assert.Empty(await client.ListSessionsAsync());
        Assert.Contains(logs, line => line.Contains("connection lost"));
    }

    [Fact]
    public async Task Rpc_RetriesOnceAfterUnauthorizedOnStaleCookie()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        var calls = 0;
        handler.Responder = request =>
        {
            calls++;
            return calls == 1
                ? RecordingHttpHandler.JsonResponse("unauthorized", HttpStatusCode.Unauthorized)
                : RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, """{"items":[]}"""));
        };

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var sessions = await client.ListSessionsAsync();

        Assert.Empty(sessions);
        Assert.Equal(2, calls);
        Assert.Equal(2, launcher.LaunchCount);
    }

    [Fact]
    public async Task ListSessionsAsync_PostsArgsEnvelopeToApiPath()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        const string sessionsJson = """
        {"items":[{"sessionId":"session-1","updatedAt":5,"running":true,"agentAvailable":true,"blank":false,
          "projections":{"values":{"title":"T","modelSelection":{"lastUsed":{"provider":"p","model":"m"}},
          "sessionListMetadata":{"lastPromptAt":5}}}}]}
        """;
        handler.Responder = request => RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, sessionsJson));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var sessions = await client.ListSessionsAsync();

        Assert.Single(sessions);
        Assert.Equal("session-1", sessions[0].SessionId);
        Assert.Equal("T", sessions[0].Title);
        Assert.Equal("m", sessions[0].Model);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("/api/session/list", recorded.Uri.AbsolutePath);
        Assert.Equal("dsh-auth-test=cookie-1", recorded.Cookie);

        using var document = JsonDocument.Parse(recorded.Body);
        Assert.Equal("client-request", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("session/list", document.RootElement.GetProperty("method").GetString());
        var args = document.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(new[] { "_request" }, args.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task GetHistory_GetState_AndPrompt_UseRequestWrapper()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        var promptIds = new List<string>();
        handler.Responder = request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/session/events", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    """{"cursor":3,"records":[{"type":"event","event":{"type":"turn/start","seq":3,"time":9,"data":{}}}],"hasMore":false}"""));
            }

            if (path.EndsWith("/session/state", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                    request.Body,
                    """{"cursor":3,"active":true,"running":true,"queue":null,"pendingInput":["question"]}"""));
            }

            if (path.EndsWith("/session/prompt", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(request.Body);
                promptIds.Add(document.RootElement.GetProperty("payload").GetProperty("args")
                    .GetProperty("request").GetProperty("requestId").GetString()!);
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, """{"accepted":true}"""));
            }

            if (path.EndsWith("/session/cancel", StringComparison.Ordinal))
            {
                return RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, """{"accepted":true}"""));
            }

            return RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.NotFound);
        };

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var history = await client.GetHistoryAsync("session-1", beforeSeq: 100, maxMessages: 3);
        Assert.Single(history.Records);
        Assert.Equal("turn/start", history.Records[0].Type);
        Assert.False(history.HasMore);

        var state = await client.GetStateAsync("session-1");
        Assert.True(state.Running);
        Assert.Equal(new[] { "question" }, state.PendingInput);

        var firstId = await client.SendPromptAsync("session-1", "hello");
        var secondId = await client.SendPromptAsync("session-1", "again", steer: true);
        Assert.True(Guid.TryParse(firstId, out _));
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, promptIds.Count);

        await client.CancelAsync("session-1");

        var historyRequest = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/session/events", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(historyRequest.Body))
        {
            var request = document.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            Assert.Equal("session-1", request.GetProperty("address").GetProperty("sessionId").GetString());
            Assert.Equal(100, request.GetProperty("beforeSeq").GetInt64());
            Assert.Equal(3, request.GetProperty("maxMessages").GetInt32());
        }

        var promptRequest = handler.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/session/prompt", StringComparison.Ordinal));
        using (var document = JsonDocument.Parse(promptRequest.Body))
        {
            var request = document.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            Assert.Equal("queue", request.GetProperty("mode").GetString());
            Assert.Equal("hello", request.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("Asia/Shanghai", request.GetProperty("clientTimeZone").GetString());
        }
    }

    [Fact]
    public async Task Rpc_ErrorEnvelopeSurfacesServerCode()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        handler.Responder = request =>
            RecordingHttpHandler.JsonResponse(TestJson.Failure(TestJson.RpcIdOf(request.Body), "session/not-found", "no such session"));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var exception = await Assert.ThrowsAsync<DshRemoteException>(() => client.GetStateAsync("missing"));
        Assert.Equal("session/not-found", exception.Code);
        Assert.Equal("no such session", exception.Message);
    }

    [Fact]
    public async Task ObserveEventsAsync_OpensMuxStreamAndDeduplicatesBySeq()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = client.ObserveEventsAsync("session-1", timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var firstMove = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => socket.SentFrames.Count >= 1);

            using (var open = JsonDocument.Parse(socket.SentFrames[0]))
            {
                Assert.Equal("open", open.RootElement.GetProperty("type").GetString());
                Assert.Equal("session/observeEvent", open.RootElement.GetProperty("endpoint").GetString());
                var request = open.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
                Assert.Equal("session-1", request.GetProperty("address").GetProperty("sessionId").GetString());
            }

            // The stream id is echoed by the server as a string.
            var streamId = JsonDocument.Parse(socket.SentFrames[0]).RootElement.GetProperty("streamId").GetString()!;

            socket.Push(ItemFrame(streamId, """{"type":"ready"}"""));
            socket.Push(ItemFrame(streamId, """{"type":"event","event":{"type":"turn/start","seq":4,"time":1,"data":{"turn":1}}}"""));
            socket.Push(ItemFrame(streamId, """{"type":"event","event":{"type":"turn/start","seq":4,"time":1,"data":{"turn":1}}}"""));
            socket.Push(ItemFrame(streamId, """{"type":"event","event":{"type":"turn/end","seq":5,"time":2,"data":{}}}"""));

            Assert.True(await firstMove);
            Assert.Equal(4, enumerator.Current.Seq);
            Assert.Equal("turn/start", enumerator.Current.Type);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(5, enumerator.Current.Seq);
            Assert.Equal("turn/end", enumerator.Current.Type);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // Disposing the iterator half-closes the remote stream.
        await WaitUntilAsync(() => socket.SentFrames.Any(frame => frame.Contains("\"type\":\"cancel\"")));
    }

    [Fact]
    public async Task DisposeAsync_RejectsFurtherUse()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        var client = new DshClient(Options(launcher, handler));

        await client.ConnectAsync(Pairing);
        await client.DisposeAsync();

        Assert.Equal(DshConnectionState.Disconnected, client.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync(Pairing));
    }
}
