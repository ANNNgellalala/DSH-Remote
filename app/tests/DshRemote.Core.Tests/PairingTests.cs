using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class PairingTests
{
    [Fact]
    public void PairingUri_RoundTripsAllFields()
    {
        var original = new DshPairingInfo(
            "192.0.2.10",
            8443,
            "token/with+special=chars",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "DESKTOP-EXAMPLE");

        var uri = original.ToPairingUri();
        Assert.StartsWith("dshremote://pair?", uri);
        Assert.Contains("v=1", uri);
        Assert.Contains("fp=", uri);

        Assert.True(DshPairingInfo.TryParse(uri, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(original.Host, parsed!.Host);
        Assert.Equal(original.Port, parsed.Port);
        Assert.Equal(original.Token, parsed.Token);
        Assert.Equal(original.SpkiSha256, parsed.SpkiSha256);
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(new Uri("https://192.0.2.10:8443/"), parsed.BaseUri);
    }

    [Fact]
    public void PairingUri_ParsesHttpsConvenienceForm()
    {
        Assert.True(DshPairingInfo.TryParse(
            "https://127.0.0.1:18090/?token=abc123&fp=deadbeef",
            out var pairing));

        Assert.Equal("127.0.0.1", pairing!.Host);
        Assert.Equal(18090, pairing.Port);
        Assert.Equal("abc123", pairing.Token);
        Assert.Equal("deadbeef", pairing.SpkiSha256);
    }

    [Fact]
    public void PairingUri_RejectsIncompleteInput()
    {
        Assert.False(DshPairingInfo.TryParse(null, out _));
        Assert.False(DshPairingInfo.TryParse(string.Empty, out _));
        Assert.False(DshPairingInfo.TryParse("dshremote://pair?host=1.2.3.4", out _));
        Assert.False(DshPairingInfo.TryParse("https://1.2.3.4:8443/", out _));
        Assert.False(DshPairingInfo.TryParse("dshremote://pair?host=h&token=t&port=not-a-port", out _));
    }

    [Fact]
    public void PairingFile_ParsesRelaySchemaV1()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "host": "192.0.2.10",
          "port": 8443,
          "token": "pairing-token-example",
          "fp": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "pairingUri": "dshremote://pair?v=1&host=192.0.2.10&port=8443&token=pairing-token-example&fp=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef&name=DESKTOP-EXAMPLE",
          "name": "DESKTOP-EXAMPLE",
          "updatedAt": "2026-11-09T00:35:12.345Z",
          "loopbackPort": 8443,
          "upstreamPort": 54306,
          "protocol": "https"
        }
        """;

        Assert.True(DshPairingFile.TryParse(json, out var pairing, out var error), error);
        Assert.NotNull(pairing);
        Assert.Equal("192.0.2.10", pairing!.Host);
        Assert.Equal(8443, pairing.Port);
        Assert.Equal("pairing-token-example", pairing.Token);
        Assert.Equal(64, pairing.SpkiSha256!.Length);
        Assert.Equal("DESKTOP-EXAMPLE", pairing.Name);
        Assert.Equal(8443, DshPairingFile.ReadOptionalInt(json, "loopbackPort"));
        Assert.Equal(54306, DshPairingFile.ReadOptionalInt(json, "upstreamPort"));
    }

    [Fact]
    public void PairingFile_FallsBackToTopLevelFields()
    {
        const string json = """
        {"host":"10.0.0.5","port":"9443","launchToken":"abc","spkiSha256":"aa","displayName":"dev-box"}
        """;

        Assert.True(DshPairingFile.TryParse(json, out var pairing, out var error), error);
        Assert.Equal("10.0.0.5", pairing!.Host);
        Assert.Equal(9443, pairing.Port);
        Assert.Equal("abc", pairing.Token);
        Assert.Equal("aa", pairing.SpkiSha256);
        Assert.Equal("dev-box", pairing.Name);
    }

    [Fact]
    public void PairingFile_ReportsMissingHostTokenOrPort()
    {
        Assert.False(DshPairingFile.TryParse("""{"port":8443,"token":"t"}""", out _, out var noHost));
        Assert.Contains("host", noHost);

        Assert.False(DshPairingFile.TryParse("""{"host":"1.2.3.4","port":8443}""", out _, out var noToken));
        Assert.Contains("token", noToken);

        Assert.False(DshPairingFile.TryParse("""{"host":"1.2.3.4","token":"t","port":0}""", out _, out var badPort));
        Assert.Contains("port", badPort);

        Assert.False(DshPairingFile.TryParse("not json", out _, out var malformed));
        Assert.Contains("JSON", malformed);
    }

    [Fact]
    public void Endpoint_ResolvesSchemeFromFingerprintAndLoopback()
    {
        var pinned = new DshPairingInfo("192.0.2.10", 8443, "t", new string('a', 64));
        Assert.Equal("https", DshEndpoint.ResolveBaseUri(pinned).Scheme);

        var loopbackDev = new DshPairingInfo("127.0.0.1", 18090, "t");
        Assert.Equal("http", DshEndpoint.ResolveBaseUri(loopbackDev).Scheme);

        var lanWithoutPin = new DshPairingInfo("192.0.2.10", 8443, "t");
        Assert.Equal("https", DshEndpoint.ResolveBaseUri(lanWithoutPin).Scheme);
    }
}
