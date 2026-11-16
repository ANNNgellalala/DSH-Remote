using System.Net;
using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

/// <summary>
/// M8 wire tests: <c>session/modelCatalog</c> shape/tolerance, <c>session/selectModel</c> request/response,
/// the per-generation catalog cache and the 404 / endpoint-missing capability signal.
/// </summary>
public sealed class ModelClientTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private static DshClientOptions Options(FakeLauncher launcher, HttpMessageHandler handler) =>
        new()
        {
            Launcher = launcher,
            HttpHandlerFactory = () => handler,
            Delay = (_, _) => Task.CompletedTask,
            ClientTimeZone = "Asia/Shanghai"
        };

    private static async Task<DshClient> ConnectedClientAsync(RecordingHttpHandler handler)
    {
        var launcher = new FakeLauncher();
        launcher.Enqueue(new FakeRemoteSocket());
        var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);
        return client;
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

    private static string ItemFrame(FakeRemoteSocket socket, string innerJson)
    {
        var open = socket.SentFrames.Last(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(open);
        var streamId = document.RootElement.GetProperty("streamId").GetString();
        return "{\"type\":\"item\",\"streamId\":\"" + streamId + "\",\"value\":" + innerJson + "}";
    }

    // Realistic full catalog (shape derived from the session-controller's buildModelCatalog).
    private const string FullCatalogJson = """
    {
      "default": { "provider": "opencode-go", "model": "deepseek-v4.1-flash", "reasoningEffort": "high" },
      "routableProviders": ["deepseek-account", "opencode-go", 7],
      "groups": [
        {
          "id": "deepseek-account",
          "name": "DeepSeek Account",
          "models": [
            {
              "id": "deepseek-v4.1-flash",
              "name": "DeepSeek V4.1 Flash",
              "description": "快速模型",
              "reasoning": {
                "efforts": [
                  { "id": "off", "name": "Off" },
                  { "id": "low", "name": "Low", "description": "低" },
                  { "id": "high", "name": "High" }
                ],
                "defaultEffort": "high"
              },
              "badge": { "text": "New", "tone": "info" }
            },
            { "id": "deepseek-v4.1", "name": "DeepSeek V4.1" }
          ]
        },
        {
          "id": "opencode-go",
          "name": "OpenCode Go",
          "models": [
            {
              "id": "qwen3-coder",
              "name": "Qwen3 Coder",
              "badge": { "text": { "en": "Beta", "zh": "测试" }, "tone": "warning" }
            }
          ]
        }
      ],
      "failures": [
        { "id": "pi-ai", "name": "Pi AI", "message": "no api key" },
        { "name": "broken failure without id" }
      ],
      "unknownTopLevelField": { "ignored": true }
    }
    """;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ModelCatalog_ParsesFullShapeAndToleratesUnknownFields()
    {
        var catalog = DshParsing.ParseModelCatalog(Parse(FullCatalogJson));

        Assert.NotNull(catalog.Default);
        Assert.Equal("opencode-go", catalog.Default!.Provider);
        Assert.Equal("deepseek-v4.1-flash", catalog.Default.Model);
        Assert.Equal("high", catalog.Default.ReasoningEffort);
        Assert.Equal(new[] { "deepseek-account", "opencode-go" }, catalog.RoutableProviders);

        Assert.Equal(2, catalog.Groups.Count);
        var account = catalog.Groups[0];
        Assert.Equal("deepseek-account", account.Id);
        Assert.Equal("DeepSeek Account", account.Name);
        Assert.Equal(2, account.Models.Count);

        var flash = account.Models[0];
        Assert.Equal("deepseek-v4.1-flash", flash.Id);
        Assert.Equal("DeepSeek V4.1 Flash", flash.Name);
        Assert.Equal("快速模型", flash.Description);
        Assert.NotNull(flash.Reasoning);
        Assert.Equal(new[] { "off", "low", "high" }, flash.Reasoning!.Efforts.Select(e => e.Id));
        Assert.Equal("Low", flash.Reasoning.Efforts[1].Name);
        Assert.Equal("低", flash.Reasoning.Efforts[1].Description);
        Assert.Equal("high", flash.Reasoning.DefaultEffort);
        Assert.Equal("New", flash.Badge!.Text);
        Assert.Equal("info", flash.Badge.Tone);

        var plain = account.Models[1];
        Assert.Null(plain.Description);
        Assert.Null(plain.Reasoning);
        Assert.Null(plain.Badge);

        var badge = catalog.Groups[1].Models[0].Badge!;
        Assert.Equal("测试", badge.Text);
        Assert.Equal("warning", badge.Tone);

        var failure = Assert.Single(catalog.Failures);
        Assert.Equal("pi-ai", failure.Id);
        Assert.Equal("Pi AI", failure.Name);
        Assert.Equal("no api key", failure.Message);
    }

    [Fact]
    public void ModelCatalog_MinimalShapeLeavesOptionalFieldsEmpty()
    {
        const string json = """
        {"groups":[{"id":"p1","models":[{"id":"m1"}]}]}
        """;

        var catalog = DshParsing.ParseModelCatalog(Parse(json));

        Assert.Null(catalog.Default);
        Assert.Empty(catalog.RoutableProviders);
        Assert.Empty(catalog.Failures);
        var group = Assert.Single(catalog.Groups);
        Assert.Equal("p1", group.Id);
        Assert.Equal("p1", group.Name);
        var model = Assert.Single(group.Models);
        Assert.Equal("m1", model.Id);
        Assert.Equal("m1", model.Name);
        Assert.Null(model.Description);
        Assert.Null(model.Reasoning);
        Assert.Null(model.Badge);
    }

    [Theory]
    [InlineData("""{"text":{"en":"New","zh":"新"},"tone":"info"}""", "新")]
    [InlineData("""{"text":{"en":"Fresh"}}""", "Fresh")]
    [InlineData("""{"text":{"ja":"新着","en":"Fresh"}}""", "Fresh")]
    [InlineData("""{"text":{"ja":"新着","de":"Neu"}}""", "新着")]
    [InlineData("""{"text":{"zh-Hans":"简体"}}""", "简体")]
    [InlineData("""{"text":"literal"}""", "literal")]
    public void ModelCatalog_BadgeTextAcceptsStringOrI18nObject(string badgeJson, string expected)
    {
        var json = """{"groups":[{"id":"p","models":[{"id":"m","badge":""" + badgeJson + "}]}]}";
        var catalog = DshParsing.ParseModelCatalog(Parse(json));

        Assert.Equal(expected, catalog.Groups[0].Models[0].Badge!.Text);
        // A missing tone falls back to the neutral display value.
        Assert.Equal(
            badgeJson.Contains("\"tone\"", StringComparison.Ordinal) ? "info" : "neutral",
            catalog.Groups[0].Models[0].Badge!.Tone);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"default":{"provider":"p","model":"m"}}""")]
    [InlineData("""{"groups":{}}""")]
    [InlineData("""{"groups":["nope"]}""")]
    [InlineData("""{"groups":[{"name":"P","models":[]}]}""")]
    [InlineData("""{"groups":[{"id":"p","name":"P"}]}""")]
    [InlineData("""{"groups":[{"id":"p","models":{}}]}""")]
    public void ModelCatalog_BrokenCoreShapeThrowsResultInvalid(string json)
    {
        var exception = Assert.Throws<DshRemoteException>(() => DshParsing.ParseModelCatalog(Parse(json)));
        Assert.Equal(DshRemoteException.Codes.ResultInvalid, exception.Code);
    }

    [Fact]
    public void ModelCatalog_SkipsMalformedLeafRows()
    {
        const string json = """
        {
          "groups": [
            { "id": "p", "models": [
              { "name": "no id" },
              "not an object",
              { "id": "good", "name": "Good",
                "reasoning": { "efforts": [ { "name": "no id" }, { "id": "high", "name": "High" } ] },
                "badge": { "text": 42 } }
            ] }
          ],
          "failures": [ { "id": "p2", "name": "P2", "message": "boom" }, "nope" ]
        }
        """;

        var catalog = DshParsing.ParseModelCatalog(Parse(json));

        var model = Assert.Single(catalog.Groups[0].Models);
        Assert.Equal("good", model.Id);
        Assert.Equal(new[] { "high" }, model.Reasoning!.Efforts.Select(e => e.Id));
        Assert.Null(model.Badge); // non-string, non-object badge text is ignored
        Assert.Single(catalog.Failures);
    }

    [Fact]
    public void SelectModelResult_ParsesSelectedAndRejectsMissingSelection()
    {
        var selected = DshParsing.ParseSelectModelResult(Parse(
            """{"selected":{"provider":"opencode-go","model":"deepseek-v4.1-flash","reasoningEffort":"high"}}"""));
        Assert.Equal("opencode-go", selected.Provider);
        Assert.Equal("deepseek-v4.1-flash", selected.Model);
        Assert.Equal("high", selected.ReasoningEffort);

        var withoutEffort = DshParsing.ParseSelectModelResult(Parse("""{"selected":{"provider":"p","model":"m"}}"""));
        Assert.Null(withoutEffort.ReasoningEffort);

        foreach (var broken in new[] { "{}", """{"selected":{}}""", """{"selected":{"provider":"p"}}""", "[]" })
        {
            var exception = Assert.Throws<DshRemoteException>(() => DshParsing.ParseSelectModelResult(Parse(broken)));
            Assert.Equal(DshRemoteException.Codes.ResultInvalid, exception.Code);
        }
    }

    [Fact]
    public void TryParseModelSelection_ToleratesMissingFields()
    {
        Assert.False(DshParsing.TryParseModelSelection(null, out var none));
        Assert.Null(none);

        Assert.False(DshParsing.TryParseModelSelection(Parse("""{"provider":"p"}"""), out _));
        Assert.False(DshParsing.TryParseModelSelection(Parse("""{"provider":"","model":"m"}"""), out _));
        Assert.False(DshParsing.TryParseModelSelection(Parse("\"m\""), out _));

        Assert.True(DshParsing.TryParseModelSelection(Parse("""{"provider":"p","model":"m"}"""), out var selection));
        Assert.Equal("p", selection!.Provider);
        Assert.Equal("m", selection.Model);
        Assert.Null(selection.ReasoningEffort);

        Assert.True(
            DshParsing.TryParseModelSelection(
                Parse("""{"provider":"p","model":"m","reasoningEffort":"  "}"""),
                out var blankEffort));
        Assert.Null(blankEffort!.ReasoningEffort);
    }

    [Fact]
    public void ModelCatalogArgs_AreExactlyEmptyObject()
    {
        var args = DshArgs.ModelCatalog();

        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Empty(args.EnumerateObject());
        Assert.Equal("{}", JsonSerializer.Serialize(args));
    }

    [Fact]
    public void SelectModelArgs_MatchStrictRequestShape()
    {
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.SelectModel("s1", "p1", "m1"))))
        {
            var request = document.RootElement.GetProperty("request");
            Assert.Equal(new[] { "sessionId", "provider", "model" }, request.EnumerateObject().Select(p => p.Name));
            Assert.Equal("s1", request.GetProperty("sessionId").GetString());
            Assert.Equal("p1", request.GetProperty("provider").GetString());
            Assert.Equal("m1", request.GetProperty("model").GetString());
            Assert.False(request.TryGetProperty("reasoningEffort", out _));
        }

        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.SelectModel("s1", "p1", "m1", "high"))))
        {
            var request = document.RootElement.GetProperty("request");
            Assert.Equal(
                new[] { "sessionId", "provider", "model", "reasoningEffort" },
                request.EnumerateObject().Select(p => p.Name));
            Assert.Equal("high", request.GetProperty("reasoningEffort").GetString());
        }

        Assert.Throws<ArgumentException>(() => DshArgs.SelectModel("", "p1", "m1"));
        Assert.Throws<ArgumentException>(() => DshArgs.SelectModel("s1", " ", "m1"));
        Assert.Throws<ArgumentException>(() => DshArgs.SelectModel("s1", "p1", ""));
    }

    [Fact]
    public async Task GetModelCatalogAsync_PostsEmptyArgsAndReturnsSupportedCatalog()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request =>
            RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, FullCatalogJson));

        await using var client = await ConnectedClientAsync(handler);
        var result = await client.GetModelCatalogAsync();

        Assert.Equal(DshModelCatalogSupport.Supported, result.Support);
        Assert.Null(result.Message);
        Assert.NotNull(result.Catalog);
        Assert.Equal("opencode-go", result.Catalog!.Default!.Provider);
        Assert.Equal(2, result.Catalog.Groups.Count);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("/api/session/modelCatalog", recorded.Uri.AbsolutePath);

        using var document = JsonDocument.Parse(recorded.Body);
        Assert.Equal("session/modelCatalog", document.RootElement.GetProperty("method").GetString());
        var args = document.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Empty(args.EnumerateObject());
    }

    [Fact]
    public async Task GetModelCatalogAsync_CachesPerGenerationAndForceRefreshBypasses()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request =>
            RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(request.Body, FullCatalogJson));

        await using var client = await ConnectedClientAsync(handler);

        var first = await client.GetModelCatalogAsync();
        var second = await client.GetModelCatalogAsync();
        Assert.Same(first.Catalog, second.Catalog);
        Assert.Single(handler.Requests);

        var refreshed = await client.GetModelCatalogAsync(forceRefresh: true);
        Assert.Equal(DshModelCatalogSupport.Supported, refreshed.Support);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnaryCall_Http404ThrowsEndpointUnavailableNotTransport()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = _ => RecordingHttpHandler.JsonResponse("not found", HttpStatusCode.NotFound);

        await using var client = await ConnectedClientAsync(handler);
        var exception = await Assert.ThrowsAsync<DshRemoteException>(() => client.GetStateAsync("session-1"));

        Assert.Equal(DshRemoteException.Codes.EndpointUnavailable, exception.Code);
        Assert.Equal("gateway/endpoint-unavailable", exception.Code);
        // A missing endpoint is a capability signal: the connection is still alive.
        Assert.False(exception.IsTransportFailure);
    }

    [Fact]
    public async Task GetModelCatalogAsync_Http404MapsToUnsupportedAndIsCached()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = _ => RecordingHttpHandler.JsonResponse("not found", HttpStatusCode.NotFound);

        await using var client = await ConnectedClientAsync(handler);
        var result = await client.GetModelCatalogAsync();

        Assert.Equal(DshModelCatalogSupport.Unsupported, result.Support);
        Assert.Null(result.Catalog);
        Assert.Contains("404", result.Message, StringComparison.Ordinal);

        // The failure result is cached for this connection generation too.
        var second = await client.GetModelCatalogAsync();
        Assert.Equal(DshModelCatalogSupport.Unsupported, second.Support);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetModelCatalogAsync_UnknownEndpointCodeMapsToUnsupported()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(
            TestJson.Failure(TestJson.RpcIdOf(request.Body), "gateway/invocation-unavailable", "Unknown endpoint"));

        await using var client = await ConnectedClientAsync(handler);
        var result = await client.GetModelCatalogAsync();

        Assert.Equal(DshModelCatalogSupport.Unsupported, result.Support);
        Assert.Null(result.Catalog);
        Assert.Equal("Unknown endpoint", result.Message);
    }

    [Fact]
    public async Task GetModelCatalogAsync_TransientFailureMapsToUnavailableWithMessage()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = _ => RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.InternalServerError);

        await using var client = await ConnectedClientAsync(handler);
        var result = await client.GetModelCatalogAsync();

        Assert.Equal(DshModelCatalogSupport.Unavailable, result.Support);
        Assert.Null(result.Catalog);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task SelectModelAsync_PostsExactRequestAndReturnsSelected()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
            request.Body,
            """{"selected":{"provider":"opencode-go","model":"deepseek-v4.1-flash","reasoningEffort":"high"}}"""));

        await using var client = await ConnectedClientAsync(handler);
        var selected = await client.SelectModelAsync("session-1", "opencode-go", "deepseek-v4.1-flash");

        // Omitted effort: the server normalises the adapter default into `selected`; the request stays clean.
        Assert.Equal("opencode-go", selected.Provider);
        Assert.Equal("deepseek-v4.1-flash", selected.Model);
        Assert.Equal("high", selected.ReasoningEffort);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("/api/session/selectModel", recorded.Uri.AbsolutePath);

        using var document = JsonDocument.Parse(recorded.Body);
        Assert.Equal("session/selectModel", document.RootElement.GetProperty("method").GetString());
        var args = document.RootElement.GetProperty("payload").GetProperty("args");
        Assert.Equal(new[] { "request" }, args.EnumerateObject().Select(p => p.Name));
        var request = args.GetProperty("request");
        Assert.Equal(new[] { "sessionId", "provider", "model" }, request.EnumerateObject().Select(p => p.Name));
        Assert.Equal("session-1", request.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task SelectModelAsync_IncludesEffortWhenGiven()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
            request.Body,
            """{"selected":{"provider":"p","model":"m","reasoningEffort":"max"}}"""));

        await using var client = await ConnectedClientAsync(handler);
        var selected = await client.SelectModelAsync("session-1", "p", "m", "max");
        Assert.Equal("max", selected.ReasoningEffort);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var request = document.RootElement.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal(
            new[] { "sessionId", "provider", "model", "reasoningEffort" },
            request.EnumerateObject().Select(p => p.Name));
        Assert.Equal("max", request.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public async Task SelectModelAsync_PropagatesServerErrorCode()
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = request => RecordingHttpHandler.JsonResponse(TestJson.Failure(
            TestJson.RpcIdOf(request.Body),
            "session/model-unavailable",
            "Select an available model before sending a message."));

        await using var client = await ConnectedClientAsync(handler);
        var exception = await Assert.ThrowsAsync<DshRemoteException>(
            () => client.SelectModelAsync("session-1", "gone", "gone"));

        Assert.Equal("session/model-unavailable", exception.Code);
        Assert.Contains("available model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionList_ParsesProviderAndEffortIntoModelSelection()
    {
        const string json = """
        {"items":[{"sessionId":"s1","updatedAt":1,"projections":{"values":{
          "title":"t",
          "modelSelection":{
            "lastUsed":{"provider":"deepseek-account","model":"deepseek-v4.1"},
            "next":{"provider":"opencode-go","model":"deepseek-v4.1-flash","reasoningEffort":"max"}}}}}]}
        """;

        var sessions = DshParsing.ParseSessionList(Parse(json));

        var summary = Assert.Single(sessions);
        Assert.Equal("deepseek-v4.1", summary.Model); // legacy string field keeps the old lastUsed-first rule
        Assert.NotNull(summary.ModelSelection);
        Assert.Equal("opencode-go", summary.ModelSelection!.Provider);
        Assert.Equal("deepseek-v4.1-flash", summary.ModelSelection.Model);
        Assert.Equal("max", summary.ModelSelection.ReasoningEffort);
    }

    [Fact]
    public async Task OpenSessionAsync_CarriesSnapshotModelSelection()
    {
        const string snapshotJson = """
        {"type":"snapshot","header":{"version":1,"id":"session-1","createdAt":1,"isSeeded":false},
         "cursor":3,"hasMore":false,"records":[],
         "projections":{"kind":"sequenced","asOfSeq":3,"values":{"title":"t",
           "modelSelection":{
             "lastUsed":{"provider":"opencode-go","model":"deepseek-v4.1-flash"},
             "next":{"provider":"deepseek-account","model":"deepseek-v4.1-flash","reasoningEffort":"max"}}}}}
        """;

        var launcher = new FakeLauncher();
        var socket = new FakeRemoteSocket();
        launcher.Enqueue(socket);
        var handler = new RecordingHttpHandler();
        handler.Responder = request => request.Uri.AbsolutePath.EndsWith("/session/state", StringComparison.Ordinal)
            ? RecordingHttpHandler.JsonResponse(TestJson.SuccessFromRequest(
                request.Body,
                """{"cursor":3,"active":false,"running":false,"queue":null,"pendingInput":[]}"""))
            : RecordingHttpHandler.JsonResponse("{}", HttpStatusCode.NotFound);

        await using var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);

        var openTask = client.OpenSessionAsync("session-1");
        await WaitUntilAsync(() => socket.SentFrames.Any(frame => frame.Contains("\"type\":\"open\"", StringComparison.Ordinal)));
        socket.Push(ItemFrame(socket, snapshotJson));

        var snapshot = await openTask;

        Assert.NotNull(snapshot.ModelSelection);
        Assert.Equal("deepseek-account", snapshot.ModelSelection!.Provider);
        Assert.Equal("deepseek-v4.1-flash", snapshot.ModelSelection.Model);
        Assert.Equal("max", snapshot.ModelSelection.ReasoningEffort);
    }
}
