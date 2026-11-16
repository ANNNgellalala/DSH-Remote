using System.Net;
using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class WorkspaceClientTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private const string BaselineJson = """
    {"type":"baseline","value":{
      "items":[{"workspaceId":"ws-1","path":"D:\\Code\\VibeCoding\\DSHRemote","title":"DSHRemote",
        "sessionIds":["session-1","session-2"],"createdAt":"2026-11-08T10:00:00.000Z",
        "updatedAt":"2026-11-09T02:30:00.000Z"}],
      "archivedSessionIds":["session-2"],
      "pinnedSessionIds":["session-1"]
    }}
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

    private static HttpResponseMessage Ok(string requestBody, string valueJson) =>
        RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(requestBody, valueJson));

    [Fact]
    public async Task GetWorkspaceStateAsync_ReadsBaselineThenClosesTheStream()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var task = client.GetWorkspaceStateAsync();
        await WaitUntilAsync(() => OpenCount(socket) >= 1);
        socket.Push(Item(socket, BaselineJson));

        var state = await task;
        Assert.Single(state.Workspaces);
        Assert.Equal("ws-1", state.Workspaces[0].WorkspaceId);
        Assert.Equal("session-2", Assert.Single(state.ArchivedSessionIds));
        Assert.Equal("session-1", Assert.Single(state.PinnedSessionIds));
        Assert.Equal(1794132000000, state.Workspaces[0].CreatedAt);

        using var open = JsonDocument.Parse(socket.SentFrames.First(frame => frame.Contains("\"type\":\"open\"")));
        Assert.Equal("workspace/follow", open.RootElement.GetProperty("endpoint").GetString());
        Assert.Empty(open.RootElement.GetProperty("payload").GetProperty("args").EnumerateObject());
        Assert.Contains(socket.SentFrames, frame => frame.Contains("\"type\":\"cancel\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArchiveSessionAsync_SendsRequestAndReturnsFreshState()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/workspace/archiveSession", StringComparison.Ordinal)
            ? Ok(request.Body, """{"archivedSessionIds":["session-2","session-1"]}""")
            : RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.NotFound);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var task = client.ArchiveSessionAsync("session-1");
        await WaitUntilAsync(() => OpenCount(socket) >= 1);
        socket.Push(Item(socket, BaselineJson));
        var state = await task;

        Assert.Equal("session-2", Assert.Single(state.ArchivedSessionIds));
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("/api/workspace/archiveSession", recorded.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(recorded.Body);
        var request = body.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal(new[] { "sessionId" }, request.EnumerateObject().Select(p => p.Name));
        Assert.Equal("session-1", request.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task ArchiveSessionAsync_StopActivityIsForwarded()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/workspace/archiveSession", StringComparison.Ordinal)
            ? Ok(request.Body, """{"archivedSessionIds":["session-1"]}""")
            : RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.NotFound);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var task = client.ArchiveSessionAsync("session-1", stopActivity: true);
        await WaitUntilAsync(() => OpenCount(socket) >= 1);
        socket.Push(Item(socket, BaselineJson));
        await task;

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var request = body.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.True(request.GetProperty("stopActivity").GetBoolean());
    }

    [Fact]
    public async Task ArchiveSessionAsync_PreservesServerErrorCode()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.Failure(TestJson.RpcIdOf(request.Body), "workspace/session-active", "session is running"));

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var exception = await Assert.ThrowsAsync<DshRemoteException>(() => client.ArchiveSessionAsync("session-1"));
        Assert.Equal("workspace/session-active", exception.Code);
    }

    [Fact]
    public async Task UnarchiveSessionAsync_SendsRequestAndReturnsFreshState()
    {
        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/workspace/unarchiveSession", StringComparison.Ordinal)
            ? Ok(request.Body, """{"archivedSessionIds":[]}""")
            : RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.NotFound);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var task = client.UnarchiveSessionAsync("session-2");
        await WaitUntilAsync(() => OpenCount(socket) >= 1);
        socket.Push(Item(socket, """{"type":"baseline","value":{"items":[],"archivedSessionIds":[],"pinnedSessionIds":[]}}"""));
        var state = await task;

        Assert.Empty(state.ArchivedSessionIds);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("/api/workspace/unarchiveSession", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("session-2", body.RootElement.GetProperty("payload").GetProperty("args")
            .GetProperty("request").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task CreateSessionAsync_UsesWorkspaceOrCwdAndReturnsSessionId()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        handler.Responder = request => Ok(request.Body, """{"sessionId":"session-new","agentPreset":"standard"}""");

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        Assert.Equal("session-new", await client.CreateSessionAsync(workspaceId: "ws-1"));
        Assert.Equal("session-new", await client.CreateSessionAsync(cwd: @"D:\scratch\ws"));
        Assert.Equal("session-new", await client.CreateSessionAsync());

        Assert.Equal(3, handler.Requests.Count);
        using (var first = JsonDocument.Parse(handler.Requests[0].Body))
        {
            var request = first.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            Assert.Equal(new[] { "workspaceId" }, request.EnumerateObject().Select(p => p.Name));
            Assert.Equal("ws-1", request.GetProperty("workspaceId").GetString());
            Assert.Equal("session/create", first.RootElement.GetProperty("method").GetString());
        }

        using (var second = JsonDocument.Parse(handler.Requests[1].Body))
        {
            var request = second.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            Assert.Equal(new[] { "cwd" }, request.EnumerateObject().Select(p => p.Name));
            Assert.Equal(@"D:\scratch\ws", request.GetProperty("cwd").GetString());
        }

        using (var third = JsonDocument.Parse(handler.Requests[2].Body))
        {
            var request = third.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
            Assert.Empty(request.EnumerateObject());
        }

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSessionAsync(workspaceId: "ws-1", cwd: @"D:\x"));
    }

    [Fact]
    public async Task SendPromptWithImages_BuildsTextThenImageContent()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        handler.Responder = request => Ok(request.Body, """{"accepted":true}""");

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5 };
        var requestId = await client.SendPromptAsync("session-1", "看图", new[]
        {
            new DshImageAttachment(png, "image/png", "one.png"),
            new DshImageAttachment(jpeg, "image/jpeg")
        });

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var request = body.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal(requestId, request.GetProperty("requestId").GetString());
        Assert.Equal("queue", request.GetProperty("mode").GetString());
        var content = request.GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("看图", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mediaType").GetString());
        Assert.Equal(Convert.ToBase64String(png), content[1].GetProperty("data").GetString());
        Assert.Equal("one.png", content[1].GetProperty("name").GetString());
        Assert.Equal("image", content[2].GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(jpeg), content[2].GetProperty("data").GetString());
        Assert.False(content[2].TryGetProperty("name", out _));

        // Image-only prompt: empty text is allowed when at least one image is attached.
        await client.SendPromptAsync("session-1", string.Empty, new[] { new DshImageAttachment(png, "image/png") }, steer: true);
        using var imageOnly = JsonDocument.Parse(handler.Requests[1].Body);
        var imageOnlyRequest = imageOnly.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("steer", imageOnlyRequest.GetProperty("mode").GetString());
        Assert.Equal("image", imageOnlyRequest.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(1, imageOnlyRequest.GetProperty("content").GetArrayLength());

        // Invalid inputs fail before any HTTP call.
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendPromptAsync("session-1", string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendPromptAsync("session-1", "x", new[] { new DshImageAttachment(png, "image/bmp") }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendPromptAsync("session-1", "x", new[] { new DshImageAttachment(Array.Empty<byte>(), "image/png") }));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetImageAsync_PostsReferenceAndDecodesBase64()
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var handler = new RecordingHttpHandler();
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var response = """
        {"attachment":{"attachmentId":"sha256:abc","mediaType":"image/png","bytes":7,"width":64,"height":32,
          "name":"shot.png","originalDimensions":{"width":128,"height":64}},"data":"BASE64"}
        """.Replace("BASE64", Convert.ToBase64String(bytes));
        handler.Responder = request => Ok(request.Body, response);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var reference = new DshImageRef("sha256:abc", "image/png", 7, 64, 32, "shot.png");
        var image = await client.GetImageAsync("session-1", reference);

        Assert.Equal(bytes, image.Bytes);
        Assert.Equal("sha256:abc", image.Image.AttachmentId);
        Assert.Equal("shot.png", image.Image.Name);

        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("session/attachment", body.RootElement.GetProperty("method").GetString());
        var request = body.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("session-1", request.GetProperty("sessionId").GetString());
        var attachment = request.GetProperty("attachment");
        Assert.Equal(new[] { "attachmentId", "mediaType", "bytes", "width", "height", "name" },
            attachment.EnumerateObject().Select(p => p.Name));
        Assert.False(attachment.TryGetProperty("data", out _));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetImageAsync("", reference));
    }
}
