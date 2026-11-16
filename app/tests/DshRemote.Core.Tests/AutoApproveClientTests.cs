using System.Net;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

/// <summary>
/// Phone-side client for the relay-owned desktop auto-approval API
/// (<c>GET/PUT /__relay/auto-approve</c>).
/// </summary>
public sealed class AutoApproveClientTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private const string ConfigJson = """
    {"ok":true,"relayVersion":"0.7.1","schemaVersion":2,
     "enabled":true,"all":false,"revision":7,
     "updatedAt":"2026-11-14T06:00:00.000Z","updatedBy":"phone",
     "workspaces":[{"id":"ws-1","path":"D:\\Code\\repo","title":"repo"}],
     "sessions":[{"id":"session-1","title":"demo","cwd":"D:\\Code\\repo","parentSessionId":null}],
     "stats":{"allowed":3,"errors":0,"lastAllowedAt":"2026-11-14T06:01:00.000Z"},
     "recent":[{"at":"2026-11-14T06:01:00.000Z","sessionId":"session-1","toolName":"pwsh","callId":"call-1","rule":"session:session-1"}],
     "error":null,"file":"C:\\Users\\me\\.dsh\\remote\\auto-approve.json"}
    """;

    private static DshClientOptions Options(FakeLauncher launcher, RecordingHttpHandler handler) =>
        new()
        {
            Launcher = launcher,
            HttpHandlerFactory = () => handler,
            Delay = (_, _) => Task.CompletedTask,
            ClientTimeZone = "Asia/Shanghai"
        };

    private static async Task<DshClient> ConnectAsync(FakeLauncher launcher, RecordingHttpHandler handler)
    {
        launcher.Enqueue(new FakeRemoteSocket());
        var client = new DshClient(Options(launcher, handler));
        await client.ConnectAsync(Pairing);
        return client;
    }

    [Fact]
    public async Task GetAutoApproveAsync_ParsesRulesStatsAndRecentDecisions()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.JsonResponse(ConfigJson)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var config = await client.GetAutoApproveAsync();

        Assert.True(config.Enabled);
        Assert.False(config.All);
        Assert.Equal(7, config.Revision);
        Assert.Equal("0.7.1", config.RelayVersion);
        Assert.Single(config.Workspaces);
        Assert.Equal("ws-1", config.Workspaces[0].WorkspaceId);
        Assert.Equal("D:\\Code\\repo", config.Workspaces[0].Path);
        Assert.Single(config.Sessions);
        Assert.Equal("session-1", config.Sessions[0].SessionId);
        Assert.Equal(3, config.Stats!.Allowed);
        Assert.NotNull(config.Stats.LastAllowedAt);
        Assert.Single(config.Recent!);
        Assert.Equal("session:session-1", config.Recent![0].Rule);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/__relay/auto-approve", request.Uri.AbsolutePath);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.Equal("dsh-auth-test=cookie-1", request.Cookie);
    }

    [Fact]
    public async Task SaveAutoApproveAsync_PutsTheCompleteRuleDocument()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.JsonResponse(ConfigJson)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var config = new DshAutoApproveConfig(
            true,
            false,
            new[] { new DshAutoApproveWorkspace("ws-1", "D:\\Code\\repo", "repo") },
            new[] { new DshAutoApproveSession("session-1", "demo") });

        var saved = await client.SaveAutoApproveAsync(config);
        Assert.False(saved.All);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/__relay/auto-approve", request.Uri.AbsolutePath);
        Assert.Contains("\"enabled\":true", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"all\":false", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"ws-1\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"D:\\\\Code\\\\repo\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"session-1\"", request.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer test-token", request.Authorization);
    }

    [Fact]
    public async Task GetAutoApproveAsync_OldRelayWithoutTheRouteReportsTheVersionRequirement()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var error = await Assert.ThrowsAsync<DshRemoteException>(() => client.GetAutoApproveAsync());

        Assert.Equal(DshRemoteException.Codes.ConnectFailed, error.Code);
        Assert.Contains("请求没有到达桌面 relay", error.Message, StringComparison.Ordinal);
        Assert.Contains("0.7.1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAutoApproveAsync_RelayValidationErrorSurfacesAsResultInvalid()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.JsonResponse("""{"ok":false,"error":"workspaces must be an array"}""")
        };

        await using var client = await ConnectAsync(launcher, handler);
        var error = await Assert.ThrowsAsync<DshRemoteException>(() =>
            client.SaveAutoApproveAsync(new DshAutoApproveConfig(true, true, Array.Empty<DshAutoApproveWorkspace>(), Array.Empty<DshAutoApproveSession>())));

        Assert.Equal(DshRemoteException.Codes.ResultInvalid, error.Code);
        Assert.Contains("workspaces must be", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAutoApproveAsync_UnauthorizedTokenMapsToTheUnauthorizedCode()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var error = await Assert.ThrowsAsync<DshRemoteException>(() => client.GetAutoApproveAsync());

        Assert.Equal(DshRemoteException.Codes.Unauthorized, error.Code);
    }
}
