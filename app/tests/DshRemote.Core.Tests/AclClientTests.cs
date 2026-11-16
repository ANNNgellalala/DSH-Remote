using System.Net;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

/// <summary>
/// The phone-side client for the relay-owned Windows ACL guard API
/// (<c>/__relay/acl/status</c> and <c>/__relay/acl/check</c>).
/// </summary>
public sealed class AclClientTests
{
    private static readonly DshPairingInfo Pairing = new("127.0.0.1", 18090, "test-token");

    private const string StatusJson = """
    {"ok":true,
     "guard":{"enabled":true,"version":1,
       "roots":[
         {"root":"D:\\Code\\VibeCoding\\DSHRemote","state":"ready","preparedAt":"2026-11-11T12:00:00.000Z","lastSeenAt":"2026-11-11T13:00:00.000Z"},
         {"root":"D:\\Code\\VibeCoding\\Arc.Avalonia","state":"failed","preparedAt":null,"lastSeenAt":null}],
       "candidates":["D:\\Code\\VibeCoding\\DSHRemote","D:\\Code\\VibeCoding\\Arc.Avalonia"]},
     "candidates":["D:\\Code\\VibeCoding\\DSHRemote","D:\\Code\\VibeCoding\\Arc.Avalonia"],
     "generatedAt":"2026-11-11T13:01:00.000Z"}
    """;

    private const string CheckJson = """
    {"ok":true,"repair":true,
     "roots":[
       {"root":"D:\\Code\\VibeCoding\\DSHRemote","ready":true,"repaired":true,"durationMs":42},
       {"root":"D:\\Code\\VibeCoding\\Arc.Avalonia","ready":false,"repaired":false,"durationMs":7,"reason":"reparse-root"}],
     "generatedAt":"2026-11-11T13:02:00.000Z"}
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
    public async Task GetAclStatusAsync_ParsesGuardStateAndSendsBearerToken()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.JsonResponse(StatusJson)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var status = await client.GetAclStatusAsync();

        Assert.True(status.Enabled);
        Assert.Equal(1, status.Version);
        Assert.Equal(2, status.CandidateCount);
        Assert.Equal(2, status.Roots.Count);
        Assert.Equal("D:\\Code\\VibeCoding\\DSHRemote", status.Roots[0].Root);
        Assert.Equal("ready", status.Roots[0].State);
        Assert.NotNull(status.Roots[0].PreparedAt);
        Assert.Equal("failed", status.Roots[1].State);
        Assert.Null(status.Roots[1].PreparedAt);
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-11T13:01:00.000Z", System.Globalization.CultureInfo.InvariantCulture),
            status.CheckedAt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/__relay/acl/status", request.Uri.AbsolutePath);
        Assert.Equal("Bearer test-token", request.Authorization);
        // The public gateway routes by the owner cookie; both hops must be covered.
        Assert.Equal("dsh-auth-test=cookie-1", request.Cookie);
    }

    [Fact]
    public async Task CheckAclAsync_PostsRepairAndParsesPerRootOutcomes()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.JsonResponse(CheckJson)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var result = await client.CheckAclAsync(repair: true);

        Assert.True(result.Repair);
        Assert.Equal(2, result.Roots.Count);
        Assert.True(result.Roots[0].Ready);
        Assert.True(result.Roots[0].Repaired);
        Assert.Equal(42, result.Roots[0].DurationMs);
        Assert.False(result.Roots[1].Ready);
        Assert.Equal("reparse-root", result.Roots[1].Reason);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/__relay/acl/check", request.Uri.AbsolutePath);
        Assert.Contains("\"repair\":true", request.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer test-token", request.Authorization);
    }

    [Fact]
    public async Task GetAclStatusAsync_OldRelayWithoutTheRouteReportsTheVersionRequirement()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var error = await Assert.ThrowsAsync<DshRemoteException>(() => client.GetAclStatusAsync());

        Assert.Equal(DshRemoteException.Codes.ConnectFailed, error.Code);
        Assert.Contains("请求没有到达桌面 relay", error.Message, StringComparison.Ordinal);
        Assert.Contains("0.5.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAclAsync_RelayErrorPayloadSurfacesAsResultInvalid()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => RecordingHttpHandler.JsonResponse("""{"ok":false,"error":"guard disabled"}""")
        };

        await using var client = await ConnectAsync(launcher, handler);
        var error = await Assert.ThrowsAsync<DshRemoteException>(() => client.CheckAclAsync());

        Assert.Equal(DshRemoteException.Codes.ResultInvalid, error.Code);
        Assert.Contains("guard disabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAclStatusAsync_UnauthorizedTokenMapsToTheUnauthorizedCode()
    {
        var launcher = new FakeLauncher();
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };

        await using var client = await ConnectAsync(launcher, handler);
        var error = await Assert.ThrowsAsync<DshRemoteException>(() => client.GetAclStatusAsync());

        Assert.Equal(DshRemoteException.Codes.Unauthorized, error.Code);
    }
}
