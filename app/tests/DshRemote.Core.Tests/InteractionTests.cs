using System.Net;
using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class InteractionTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private const string ReadyJson = """
    {"type":"ready","clientId":"client-1","host":{"home":"C:\\Users\\user"}}
    """;

    private const string ApprovalJson = """
    {"type":"waterfall","event":"approval/request","eventId":"event-approval-1","agentId":"session-1",
     "request":{"toolName":"pwsh","callId":"call_1","reason":"escalate sandbox to danger-full-access: write outside cwd",
       "displayReason":{"en":"Allow this operation once?","zh":"允许本次操作？"}}}
    """;

    private const string QuestionJson = """
    {"type":"waterfall","event":"user-questions/request","eventId":"event-question-1","agentId":"session-2",
     "request":{"questions":[
       {"id":"ab-choice","question":"你喜欢 A 还是 B？","header":"选择",
        "options":[{"label":"A","description":"选择 A。"},{"label":"B","description":"选择 B。"}]},
       {"id":"multi","question":"多选？","multiSelect":true,
        "options":["X","Y","Z"]}]}}
    """;

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

    private static int OpenCount(FakeRemoteSocket socket) =>
        socket.SentFrames.Count(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal));

    private static string LastStreamId(FakeRemoteSocket socket)
    {
        var open = socket.SentFrames.Last(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(open);
        return document.RootElement.GetProperty("streamId").GetString()!;
    }

    private static string Item(FakeRemoteSocket socket, string innerJson) =>
        "{\"type\":\"item\",\"streamId\":\"" + LastStreamId(socket) + "\",\"value\":" + innerJson + "}";

    // ---- pure frame parsing --------------------------------------------------------------------------

    [Fact]
    public void ReadyFrame_ParsesClientIdAndHome()
    {
        using var document = JsonDocument.Parse(ReadyJson);
        Assert.True(InteractionParsing.TryParseReady(document.RootElement, out var clientId, out var home));
        Assert.Equal("client-1", clientId);
        Assert.Equal(@"C:\Users\user", home);
        Assert.Equal("ready", InteractionParsing.FrameType(document.RootElement));
    }

    [Fact]
    public void ApprovalFrame_ParsesRealRuntimeShape()
    {
        using var document = JsonDocument.Parse(ApprovalJson);
        Assert.True(InteractionParsing.TryParseApproval(document.RootElement, out var approval));

        Assert.Equal("event-approval-1", approval.RequestId);
        Assert.Equal("session-1", approval.SessionId);
        Assert.Equal("pwsh", approval.ToolName);
        Assert.Equal("call_1", approval.CallId);
        Assert.Contains("danger-full-access", approval.Reason);
        Assert.Equal("允许本次操作？", approval.DisplayReason);
        Assert.Null(approval.ArgumentsJson);
        Assert.Null(approval.ExpiresAt);
    }

    [Fact]
    public void ApprovalFrame_FallsBackToReasonWhenDisplayReasonMissing()
    {
        const string json = """
        {"type":"waterfall","event":"approval/request","eventId":"e2","agentId":"session-3",
         "request":{"toolName":"write","reason":"plain reason"}}
        """;
        using var document = JsonDocument.Parse(json);
        Assert.True(InteractionParsing.TryParseApproval(document.RootElement, out var approval));
        Assert.Equal("plain reason", approval.DisplayReason);
        Assert.Null(approval.CallId);
    }

    [Fact]
    public void QuestionFrame_ParsesOptionsMultiSelectAndDetail()
    {
        using var document = JsonDocument.Parse(QuestionJson);
        Assert.True(InteractionParsing.TryParseQuestion(document.RootElement, out var question));

        Assert.Equal("event-question-1", question.RequestId);
        Assert.Equal("session-2", question.SessionId);
        Assert.Equal(2, question.Questions.Count);

        var first = question.Questions[0];
        Assert.Equal("ab-choice", first.Id);
        Assert.Equal("你喜欢 A 还是 B？", first.Text);
        Assert.Equal(new[] { "A", "B" }, first.Options);
        Assert.False(first.AllowMultiple);
        Assert.True(first.AllowFreeText);
        Assert.Equal("选择", first.Detail);

        var second = question.Questions[1];
        Assert.True(second.AllowMultiple);
        Assert.Equal(new[] { "X", "Y", "Z" }, second.Options);
    }

    [Fact]
    public void CancelFrame_ParsesEventIdAndOtherFramesAreRejected()
    {
        using var cancel = JsonDocument.Parse("""{"type":"cancel","eventId":"event-1"}""");
        Assert.True(InteractionParsing.TryParseCancel(cancel.RootElement, out var eventId));
        Assert.Equal("event-1", eventId);

        using var emit = JsonDocument.Parse("""{"type":"emit","event":"settings/document-updated","args":[]}""");
        Assert.False(InteractionParsing.TryParseCancel(emit.RootElement, out _));
        Assert.False(InteractionParsing.TryParseApproval(emit.RootElement, out _));
        Assert.False(InteractionParsing.TryParseQuestion(emit.RootElement, out _));
        Assert.Equal("emit", InteractionParsing.FrameType(emit.RootElement));

        using var otherWaterfall = JsonDocument.Parse(
            """{"type":"waterfall","event":"approval/request","eventId":"e","agentId":"s","request":{}}""");
        Assert.True(InteractionParsing.TryParseApproval(otherWaterfall.RootElement, out var emptyRequest));
        Assert.Equal("unknown", emptyRequest.ToolName);
        Assert.Null(emptyRequest.DisplayReason);
    }

    [Fact]
    public void EventsResultArgs_HaveGatewayShape()
    {
        var value = DshArgs.ApprovalOutcomeValue("allowed-once");
        var args = DshArgs.EventsResult("client-1", "event-1", value);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(args));
        var root = document.RootElement;

        Assert.Equal(new[] { "clientId", "eventId", "outcome" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("client-1", root.GetProperty("clientId").GetString());
        Assert.Equal("event-1", root.GetProperty("eventId").GetString());
        Assert.Equal("result", root.GetProperty("outcome").GetProperty("kind").GetString());
        Assert.Equal("allowed-once", root.GetProperty("outcome").GetProperty("value").GetString());

        var answers = DshArgs.QuestionAnswersValue(new[]
        {
            new DshQuestionAnswerItem("ab-choice", new[] { "A" }),
            new DshQuestionAnswerItem("free", Array.Empty<string>(), "typed")
        });
        using var answerDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            DshArgs.EventsResult("client-1", "event-2", answers)));
        var answerRoot = answerDocument.RootElement.GetProperty("outcome").GetProperty("value");
        Assert.Equal(2, answerRoot.GetProperty("answers").GetArrayLength());
        Assert.Equal("A", answerRoot.GetProperty("answers")[0].GetProperty("selected")[0].GetString());
        Assert.Equal("typed", answerRoot.GetProperty("answers")[1].GetProperty("custom").GetString());

        var cancelled = DshArgs.QuestionCancelledValue();
        using var cancelledDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            DshArgs.EventsResult("client-1", "event-3", cancelled)));
        Assert.True(cancelledDocument.RootElement.GetProperty("outcome").GetProperty("value")
            .GetProperty("cancelled").GetBoolean());
    }

    // ---- client integration --------------------------------------------------------------------------

    [Fact]
    public async Task ApprovalsAsync_SurfacesWaterfallAndRespondsExactlyOnce()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.SuccessFromRequest(request.Body, "null"));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.ApprovalsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var move = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            Assert.Contains(socket.SentFrames, frame => frame.Contains("\"endpoint\":\"$events\"", StringComparison.Ordinal));

            socket.Push(Item(socket, ReadyJson));
            socket.Push(Item(socket, ApprovalJson));

            Assert.True(await move);
            var approval = enumerator.Current;
            Assert.Equal("event-approval-1", approval.RequestId);
            Assert.Equal("session-1", approval.SessionId);
            Assert.Equal("pwsh", approval.ToolName);

            await client.RespondApprovalAsync(approval.RequestId, DshApprovalOutcome.AllowedOnce);

            var recorded = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, recorded.Method);
            Assert.Equal("/api/$events/result", recorded.Uri.AbsolutePath);
            using var body = JsonDocument.Parse(recorded.Body);
            Assert.Equal("client-request", body.RootElement.GetProperty("type").GetString());
            var args = body.RootElement.GetProperty("payload").GetProperty("args");
            Assert.Equal("client-1", args.GetProperty("clientId").GetString());
            Assert.Equal("event-approval-1", args.GetProperty("eventId").GetString());
            Assert.Equal("result", args.GetProperty("outcome").GetProperty("kind").GetString());
            Assert.Equal("allowed-once", args.GetProperty("outcome").GetProperty("value").GetString());

            // Second response for the same request is a client-side no-op.
            await client.RespondApprovalAsync(approval.RequestId, DshApprovalOutcome.Rejected);
            Assert.Single(handler.Requests);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task QuestionsAsync_SurfacesBatchAndAnswersWithWireValue()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, "null"));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.QuestionsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var move = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, ReadyJson));
            socket.Push(Item(socket, QuestionJson));

            Assert.True(await move);
            var question = enumerator.Current;
            Assert.Equal("event-question-1", question.RequestId);
            Assert.Equal(2, question.Questions.Count);

            await client.RespondQuestionAsync(question.RequestId, new[]
            {
                new DshQuestionAnswerItem("ab-choice", new[] { "A" }),
                new DshQuestionAnswerItem("multi", new[] { "X", "Z" })
            });

            var recorded = Assert.Single(handler.Requests);
            using var body = JsonDocument.Parse(recorded.Body);
            var args = body.RootElement.GetProperty("payload").GetProperty("args");
            var answers = args.GetProperty("outcome").GetProperty("value").GetProperty("answers");
            Assert.Equal(2, answers.GetArrayLength());
            Assert.Equal("A", answers[0].GetProperty("selected")[0].GetString());
            Assert.Equal("Z", answers[1].GetProperty("selected")[1].GetString());

            // Cancelling a second request sends {cancelled:true}.
            socket.Push(Item(socket, """
            {"type":"waterfall","event":"user-questions/request","eventId":"event-question-2","agentId":"session-2",
             "request":{"questions":[{"id":"q","question":"sure?"}]}}
            """));
            Assert.True(await enumerator.MoveNextAsync());
            await client.RespondQuestionAsync(enumerator.Current.RequestId, Array.Empty<DshQuestionAnswerItem>(), cancelled: true);

            Assert.Equal(2, handler.Requests.Count);
            using var cancelledBody = JsonDocument.Parse(handler.Requests[1].Body);
            var cancelledArgs = cancelledBody.RootElement.GetProperty("payload").GetProperty("args");
            Assert.True(cancelledArgs.GetProperty("outcome").GetProperty("value").GetProperty("cancelled").GetBoolean());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelledWaterfall_RemovesPendingRequestSoLaterResponseIsNoOp()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = client.ApprovalsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var move = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(socket) >= 1);
            socket.Push(Item(socket, ReadyJson));
            socket.Push(Item(socket, ApprovalJson));

            Assert.True(await move);
            Assert.Equal("event-approval-1", enumerator.Current.RequestId);

            // The desktop answered first: a cancel frame settles the request on this client too.
            socket.Push(Item(socket, """{"type":"cancel","eventId":"event-approval-1"}"""));
            socket.Push(Item(socket, """
            {"type":"waterfall","event":"approval/request","eventId":"event-approval-2","agentId":"session-4",
             "request":{"toolName":"write","reason":"second"}}
            """));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("event-approval-2", enumerator.Current.RequestId);

            // The cancelled request is gone: answering it is a local no-op (no HTTP call).
            await client.RespondApprovalAsync("event-approval-1", DshApprovalOutcome.AllowedOnce);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconnect_ReplaysPendingRequestsWithoutDuplicates()
    {
        var launcher = new FakeLauncher();
        var firstSocket = new FakeRemoteSocket();
        var secondSocket = new FakeRemoteSocket();
        launcher.Enqueue(firstSocket);
        launcher.Enqueue(secondSocket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, "null"));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enumerator = client.ApprovalsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        try
        {
            var first = enumerator.MoveNextAsync();
            await WaitUntilAsync(() => OpenCount(firstSocket) >= 1);
            firstSocket.Push(Item(firstSocket, ReadyJson));
            firstSocket.Push(Item(firstSocket, ApprovalJson));
            Assert.True(await first);
            Assert.Equal("event-approval-1", enumerator.Current.RequestId);

            // Drop the connection; the host replays the still-pending request to the new answering stream.
            firstSocket.Close();
            await WaitUntilAsync(() => OpenCount(secondSocket) >= 1);
            secondSocket.Push(Item(secondSocket, ReadyJson));
            secondSocket.Push(Item(secondSocket, ApprovalJson));
            secondSocket.Push(Item(secondSocket, """
            {"type":"waterfall","event":"approval/request","eventId":"event-approval-2","agentId":"session-5",
             "request":{"toolName":"write","reason":"new while reconnecting"}}
            """));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("event-approval-2", enumerator.Current.RequestId);

            // The replayed request can still be answered, now with the new generation's clientId.
            await client.RespondApprovalAsync("event-approval-1", DshApprovalOutcome.AllowedOnce);
            var recorded = Assert.Single(handler.Requests);
            using var body = JsonDocument.Parse(recorded.Body);
            Assert.Equal("client-1", body.RootElement.GetProperty("payload").GetProperty("args").GetProperty("clientId").GetString());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
