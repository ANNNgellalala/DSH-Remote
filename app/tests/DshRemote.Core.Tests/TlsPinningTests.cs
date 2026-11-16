using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DshRemote.Core;
using Xunit;

namespace DshRemote.Core.Tests;

public sealed class TlsPinningTests
{
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Parse("127.0.0.1"));
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Round-trip through DER so the returned certificate has no private-key association.
        return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
    }

    [Fact]
    public void SpkiFingerprint_IsLowercaseHexSha256OfSubjectPublicKeyInfo()
    {
        using var certificate = CreateSelfSignedCertificate();
        var fingerprint = TlsPinning.ComputeSpkiSha256(certificate);

        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint.ToLowerInvariant(), fingerprint);

        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        var expected = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
        Assert.Equal(expected, fingerprint);

        // Must not be the whole-certificate hash.
        Assert.NotEqual(certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(), fingerprint);
    }

    [Fact]
    public void Validate_AcceptsPinnedCertificateEvenWithChainErrors()
    {
        using var certificate = CreateSelfSignedCertificate();
        var fingerprint = TlsPinning.ComputeSpkiSha256(certificate);

        Assert.True(TlsPinning.Validate(certificate, fingerprint, SslPolicyErrors.RemoteCertificateChainErrors, out var reason));
        Assert.Contains("matched", reason);

        // Colons/uppercase are normalized.
        var formatted = string.Join(":", fingerprint.ToUpperInvariant().Chunk(2).Select(chunk => new string(chunk)));
        Assert.True(TlsPinning.Validate(certificate, formatted, SslPolicyErrors.RemoteCertificateChainErrors, out _));
    }

    [Fact]
    public void Validate_RejectsWrongFingerprintAndMissingCertificate()
    {
        using var certificate = CreateSelfSignedCertificate();

        Assert.False(TlsPinning.Validate(certificate, new string('a', 64), SslPolicyErrors.RemoteCertificateChainErrors, out var mismatch));
        Assert.Contains("mismatch", mismatch);

        Assert.False(TlsPinning.Validate(null, new string('a', 64), SslPolicyErrors.None, out var missing));
        Assert.Contains("did not present", missing);
    }

    [Fact]
    public void Validate_EmptyFingerprintFallsBackToSystemTrust()
    {
        using var certificate = CreateSelfSignedCertificate();

        // Self-signed certificate is not system-trusted.
        Assert.False(TlsPinning.Validate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors, out var fallback));
        Assert.Contains("system trust", fallback);

        // A publicly trusted certificate (no policy errors) passes the fallback.
        Assert.True(TlsPinning.Validate(certificate, null, SslPolicyErrors.None, out _));
    }

    [Fact]
    public void ValidationCallback_UsesPinnedFingerprint()
    {
        using var certificate = CreateSelfSignedCertificate();
        var fingerprint = TlsPinning.ComputeSpkiSha256(certificate);
        var pairing = new DshPairingInfo("192.0.2.10", 8443, "token", fingerprint);
        var logs = new List<string>();
        var callback = TlsPinning.CreateValidationCallback(pairing, logs.Add);

        Assert.True(callback(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Empty(logs);

        var wrong = new DshPairingInfo("192.0.2.10", 8443, "token", new string('b', 64));
        var wrongCallback = TlsPinning.CreateValidationCallback(wrong, logs.Add);
        Assert.False(wrongCallback(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains(logs, line => line.Contains("TLS rejected"));
    }

    [Fact]
    public void ValidationCallback_WarnsWhenPairingHasNoPin()
    {
        var pairing = new DshPairingInfo("127.0.0.1", 18090, "token");
        var logs = new List<string>();
        var callback = TlsPinning.CreateValidationCallback(pairing, logs.Add);

        Assert.Contains(TlsPinning.WarningForMissingPin, logs);

        // No pin: fall back to the system trust verdict (here: a certificate with no reported policy errors).
        using var certificate = CreateSelfSignedCertificate();
        Assert.True(callback(this, certificate, null, SslPolicyErrors.None));
    }

    [Fact]
    public void IsUsableFingerprint_Requires64HexChars()
    {
        Assert.True(TlsPinning.IsUsableFingerprint(new string('a', 64)));
        Assert.True(TlsPinning.IsUsableFingerprint(string.Join(":", Enumerable.Repeat("AB", 32))));
        Assert.False(TlsPinning.IsUsableFingerprint(null));
        Assert.False(TlsPinning.IsUsableFingerprint(""));
        Assert.False(TlsPinning.IsUsableFingerprint(new string('a', 63)));
        Assert.False(TlsPinning.IsUsableFingerprint(new string('z', 64)));
    }
}
