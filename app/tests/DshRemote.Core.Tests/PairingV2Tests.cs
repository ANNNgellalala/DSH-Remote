using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class PairingV2Tests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string V2GatewayUri =
        "dshremote://pair?v=2&mode=gateway&host=203.0.113.10&port=9443&token=remote-token&fp=" + Fingerprint + "&name=DESKTOP-EXAMPLE";

    [Fact]
    public void V2GatewayUri_ParsesAllFields()
    {
        Assert.True(DshPairingInfo.TryParse(V2GatewayUri, out var pairing));
        Assert.NotNull(pairing);
        Assert.Equal("203.0.113.10", pairing!.Host);
        Assert.Equal(9443, pairing.Port);
        Assert.Equal("remote-token", pairing.Token);
        Assert.Equal(Fingerprint, pairing.SpkiSha256);
        Assert.Equal("DESKTOP-EXAMPLE", pairing.Name);
        Assert.Equal(DshPairingMode.Gateway, pairing.Mode);
        Assert.True(pairing.IsGateway);
        Assert.Null(pairing.LocalFallback);
    }

    [Fact]
    public void V2GatewayUri_RoundTripsThroughBuilder()
    {
        Assert.True(DshPairingInfo.TryParse(V2GatewayUri, out var parsed));
        var rebuilt = DshPairingInfo.BuildPairingUri(parsed!);

        Assert.Contains("v=2", rebuilt);
        Assert.Contains("mode=gateway", rebuilt);
        Assert.DoesNotContain("v=1", rebuilt);
        Assert.Equal(parsed!.ToPairingUri(), rebuilt);

        Assert.True(DshPairingInfo.TryParse(rebuilt, out var reparsed));
        Assert.Equal(parsed.Host, reparsed!.Host);
        Assert.Equal(parsed.Port, reparsed.Port);
        Assert.Equal(parsed.Token, reparsed.Token);
        Assert.Equal(parsed.SpkiSha256, reparsed.SpkiSha256);
        Assert.Equal(parsed.Name, reparsed.Name);
        Assert.Equal(DshPairingMode.Gateway, reparsed.Mode);
    }

    [Fact]
    public void V1Uri_RoundTripsAsLocalWithNoModeParameter()
    {
        var original = new DshPairingInfo("192.0.2.10", 8443, "lan-token", new string('a', 64), "PC");
        var uri = original.ToPairingUri();

        Assert.Contains("v=1", uri);
        Assert.DoesNotContain("mode=", uri);

        Assert.True(DshPairingInfo.TryParse(uri, out var parsed));
        Assert.Equal(DshPairingMode.Local, parsed!.Mode);
        Assert.False(parsed.IsGateway);
        Assert.Equal(original.Host, parsed.Host);
        Assert.Equal(original.Port, parsed.Port);
    }

    [Fact]
    public void ModeParsing_DefaultsV2ToGatewayAndUnknownToLocal()
    {
        Assert.Equal(DshPairingMode.Gateway, DshPairingInfo.ParseMode(null, 2));
        Assert.Equal(DshPairingMode.Local, DshPairingInfo.ParseMode(null, 1));
        Assert.Equal(DshPairingMode.Gateway, DshPairingInfo.ParseMode("gateway", 2));
        Assert.Equal(DshPairingMode.Gateway, DshPairingInfo.ParseMode("GATEWAY", 1));
        Assert.Equal(DshPairingMode.Local, DshPairingInfo.ParseMode("local", 2));
        Assert.Equal(DshPairingMode.Local, DshPairingInfo.ParseMode("something-else", 2));
    }

    [Fact]
    public void V2Uri_WithoutMode_IsTreatedAsGateway()
    {
        var uri = "dshremote://pair?v=2&host=1.2.3.4&port=9443&token=t&fp=" + Fingerprint;
        Assert.True(DshPairingInfo.TryParse(uri, out var pairing));
        Assert.Equal(DshPairingMode.Gateway, pairing!.Mode);
    }

    [Fact]
    public void V2Uri_WithExplicitLocalMode_StaysLocal()
    {
        var uri = "dshremote://pair?v=2&mode=local&host=192.0.2.10&port=8443&token=t";
        Assert.True(DshPairingInfo.TryParse(uri, out var pairing));
        Assert.Equal(DshPairingMode.Local, pairing!.Mode);
    }

    private const string V2Document = """
    {
      "schemaVersion": 2,
      "active": true,
      "name": "DESKTOP-EXAMPLE",
      "token": "remote-token",
      "gateway": {
        "url": "https://203.0.113.10:9443",
        "online": true,
        "fp": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
      },
      "localHost": "192.0.2.10",
      "localPort": 8443,
      "localPairingUri": "dshremote://pair?v=1&host=192.0.2.10&port=8443&token=remote-token&fp=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef&name=DESKTOP-EXAMPLE",
      "updatedAt": "2026-11-09T15:14:59.298Z"
    }
    """;

    [Fact]
    public void V2PairingDocument_ParsesGatewayBlockAndLocalFallback()
    {
        Assert.True(DshPairingFile.TryParse(V2Document, out var pairing, out var error), error);

        Assert.NotNull(pairing);
        Assert.Equal(DshPairingMode.Gateway, pairing!.Mode);
        Assert.Equal("203.0.113.10", pairing.Host);
        Assert.Equal(9443, pairing.Port);
        Assert.Equal("remote-token", pairing.Token);
        Assert.Equal(Fingerprint, pairing.SpkiSha256);
        Assert.Equal("DESKTOP-EXAMPLE", pairing.Name);
        Assert.True(pairing.GatewayOnline);

        Assert.NotNull(pairing.LocalFallback);
        Assert.Equal(DshPairingMode.Local, pairing.LocalFallback!.Mode);
        Assert.Equal("192.0.2.10", pairing.LocalFallback.Host);
        Assert.Equal(8443, pairing.LocalFallback.Port);
        Assert.Null(pairing.LocalFallback.LocalFallback);
    }

    [Fact]
    public void V2PairingDocument_OfflineGatewayExposesOnlineFalse()
    {
        var offline = V2Document.Replace("\"online\": true", "\"online\": false");
        Assert.True(DshPairingFile.TryParse(offline, out var pairing, out var error), error);
        Assert.False(pairing!.GatewayOnline);
        Assert.Equal(DshPairingMode.Gateway, pairing.Mode);
    }

    [Fact]
    public void V2PairingDocument_WithoutGatewayBlockFallsBackToTopLevel()
    {
        const string json = """
        {"schemaVersion":2,"host":"10.0.0.5","port":9443,"token":"t","fp":"aa","name":"n"}
        """;
        Assert.True(DshPairingFile.TryParse(json, out var pairing, out var error), error);
        Assert.Equal("10.0.0.5", pairing!.Host);
        Assert.Equal(9443, pairing.Port);
        Assert.Equal(DshPairingMode.Local, pairing.Mode);
        Assert.Null(pairing.LocalFallback);
    }

    [Fact]
    public void V2PairingDocument_InvalidGatewayUrlUsesTopLevelFields()
    {
        const string json = """
        {
          "schemaVersion": 2,
          "host": "10.0.0.9",
          "port": 9443,
          "token": "t",
          "fp": "aa",
          "gateway": {"url": "not a url", "online": false, "fp": "bb"}
        }
        """;
        Assert.True(DshPairingFile.TryParse(json, out var pairing, out var error), error);
        Assert.Equal("10.0.0.9", pairing!.Host);
        Assert.Equal(9443, pairing.Port);
        Assert.Equal("bb", pairing.SpkiSha256); // gateway fp still wins when present
        Assert.False(pairing.GatewayOnline);
    }

    [Fact]
    public void V1PairingDocument_StaysLocalWithNoFallback()
    {
        const string json = """
        {"schemaVersion":1,"active":true,"host":"192.0.2.10","port":8443,"token":"t","fp":"aa","name":"n"}
        """;
        Assert.True(DshPairingFile.TryParse(json, out var pairing, out var error), error);
        Assert.Equal(DshPairingMode.Local, pairing!.Mode);
        Assert.Null(pairing.GatewayOnline);
        Assert.Null(pairing.LocalFallback);
        Assert.Equal("192.0.2.10", pairing.Host);
    }

    [Fact]
    public void DocumentWithGatewayBlock_IsGatewayEvenWhenSchemaVersionSaysV1()
    {
        // The gateway block (not the schema version) is the authoritative gateway signal.
        Assert.True(DshPairingFile.TryParse(V2Document.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"), out var pairing, out var error), error);
        Assert.Equal(DshPairingMode.Gateway, pairing!.Mode);
        Assert.Equal("203.0.113.10", pairing.Host);
        Assert.Equal("192.0.2.10", pairing.LocalFallback!.Host);
    }

    [Fact]
    public void GatewayMode_UsesGatewayHostForTheTransport()
    {
        Assert.True(DshPairingFile.TryParse(V2Document, out var pairing, out _));
        Assert.Equal(new Uri("https://203.0.113.10:9443/"), pairing!.BaseUri);
        Assert.Equal("https://203.0.113.10:9443/", DshEndpoint.ResolveBaseUri(pairing).ToString());
    }
}
