using System.Net;
using System.Text.Json;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class WireTests
{
    [Fact]
    public void ClientRequest_SerializesArgsWrapperAndExactFields()
    {
        var body = RpcWire.BuildClientRequest("session/list", DshArgs.List(), out var rpcId);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("client-request", root.GetProperty("type").GetString());
        Assert.Equal(rpcId, root.GetProperty("rpcId").GetString());
        Assert.True(Guid.TryParse(rpcId, out _));
        Assert.Equal("session/list", root.GetProperty("method").GetString());

        var payload = root.GetProperty("payload");
        Assert.Equal(new[] { "args" }, payload.EnumerateObject().Select(p => p.Name));
        var args = payload.GetProperty("args");
        Assert.Equal(new[] { "_request" }, args.EnumerateObject().Select(p => p.Name));
        Assert.Empty(args.GetProperty("_request").EnumerateObject());
    }

    [Fact]
    public void Args_Builder_WrapsRequestParameterForEveryEndpoint()
    {
        var events = DshArgs.Events("session-1", null, 80);
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(events)))
        {
            var request = document.RootElement.GetProperty("request");
            Assert.Equal("session", request.GetProperty("address").GetProperty("kind").GetString());
            Assert.Equal("session-1", request.GetProperty("address").GetProperty("sessionId").GetString());
            Assert.Equal(80, request.GetProperty("maxMessages").GetInt32());
            Assert.False(request.TryGetProperty("beforeSeq", out _));
        }

        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.Prompt("r1", "s1", "hello", "queue", "Asia/Shanghai"))))
        {
            var request = document.RootElement.GetProperty("request");
            Assert.Equal(new[] { "requestId", "sessionId", "mode", "content", "clientTimeZone" },
                request.EnumerateObject().Select(p => p.Name));
            Assert.Equal("hello", request.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("queue", request.GetProperty("mode").GetString());
        }

        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.ObserveEvent("s2"))))
        {
            var address = document.RootElement.GetProperty("request").GetProperty("address");
            Assert.Equal("s2", address.GetProperty("sessionId").GetString());
        }

        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(DshArgs.State("s3"))))
        {
            Assert.Equal("s3", document.RootElement.GetProperty("request").GetProperty("sessionId").GetString());
        }
    }

    [Fact]
    public void ServerResponse_ParsesSuccessValueAndEchoedRpcId()
    {
        const string body = """
        {"type":"server-response","rpcId":"rpc-1","result":{"ok":true,"value":{"items":[]}}}
        """;

        var value = RpcWire.ParseServerResponse(body, "rpc-1");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.True(value.TryGetProperty("items", out var items));
        Assert.Equal(0, items.GetArrayLength());
    }

    [Fact]
    public void ServerResponse_MapsFailureEnvelopeToException()
    {
        const string body = """
        {"type":"server-response","rpcId":"rpc-2","result":{"ok":false,
          "error":{"code":"gateway/arguments-invalid","message":"args fields do not match","details":{"field":"request"}}}}
        """;

        var exception = Assert.Throws<DshRemoteException>(() => RpcWire.ParseServerResponse(body, "rpc-2"));
        Assert.Equal("gateway/arguments-invalid", exception.Code);
        Assert.Equal("args fields do not match", exception.Message);
        Assert.NotNull(exception.Details);
        Assert.Equal("request", exception.Details!.Value.GetProperty("field").GetString());
    }

    [Fact]
    public void ServerResponse_RejectsRpcIdMismatchAndMalformedEnvelope()
    {
        var mismatch = Assert.Throws<DshRemoteException>(() => RpcWire.ParseServerResponse(
            """{"type":"server-response","rpcId":"other","result":{"ok":true,"value":{}}}""",
            "expected"));
        Assert.Equal(DshRemoteException.Codes.RpcIdMismatch, mismatch.Code);

        var malformed = Assert.Throws<DshRemoteException>(() => RpcWire.ParseServerResponse("{}", "expected"));
        Assert.Equal(DshRemoteException.Codes.ResultInvalid, malformed.Code);

        var notJson = Assert.Throws<DshRemoteException>(() => RpcWire.ParseServerResponse("<html>", "expected"));
        Assert.Equal(DshRemoteException.Codes.ResultInvalid, notJson.Code);
    }

    [Fact]
    public void ApiPath_UsesNamespaceSlashMethod()
    {
        Assert.Equal("api/session/list", RpcWire.ApiPath("session/list"));
        Assert.Equal("api/session/observeEvent", RpcWire.ApiPath("session/observeEvent"));
        Assert.Throws<ArgumentException>(() => RpcWire.ApiPath(""));
        Assert.Throws<ArgumentException>(() => RpcWire.ApiPath("/session/list"));
        Assert.Throws<ArgumentException>(() => RpcWire.ApiPath("session/../list"));
    }

    [Fact]
    public void SetCookie_ParsesNameValueAndDropsAttributes()
    {
        const string header = "dsh-auth-AbC123=v2.payload.signature; Max-Age=2592000; Path=/; HttpOnly; SameSite=Strict";

        Assert.Equal("dsh-auth-AbC123=v2.payload.signature", HttpConnectionLauncher.ParseSetCookie(header));
        Assert.Null(HttpConnectionLauncher.ParseSetCookie(null));
        Assert.Null(HttpConnectionLauncher.ParseSetCookie("   "));
        Assert.Null(HttpConnectionLauncher.ParseSetCookie("Path=/; HttpOnly"));
    }

    [Fact]
    public void MuxOpenFrame_SerializesEndpointAndArgsWrapper()
    {
        var frame = MuxWire.BuildOpen(7, MuxWire.ObserveEventEndpoint, DshArgs.ObserveEvent("session-x"));
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;

        Assert.Equal("open", root.GetProperty("type").GetString());
        Assert.Equal("7", root.GetProperty("streamId").GetString());
        Assert.Equal("session/observeEvent", root.GetProperty("endpoint").GetString());
        var request = root.GetProperty("payload").GetProperty("args").GetProperty("request");
        Assert.Equal("session-x", request.GetProperty("address").GetProperty("sessionId").GetString());

        Assert.Equal("end", JsonDocument.Parse(MuxWire.BuildEnd(7)).RootElement.GetProperty("type").GetString());
        Assert.Equal("cancel", JsonDocument.Parse(MuxWire.BuildCancel(7)).RootElement.GetProperty("type").GetString());
    }
}
