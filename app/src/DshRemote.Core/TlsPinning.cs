using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DshRemote.Core;

/// <summary>
/// TLS certificate pinning for the relay's self-signed certificate.
/// The pinned value is the lowercase hex SHA-256 of the certificate's SubjectPublicKeyInfo (SPKI) DER —
/// not the whole-certificate hash.
/// </summary>
public static class TlsPinning
{
    /// <summary>Computes the lowercase-hex SHA-256 of a certificate's SPKI DER.</summary>
    public static string ComputeSpkiSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    /// <summary>Normalizes a fingerprint (drops colons/whitespace, lowercases) for comparison.</summary>
    public static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        var chars = new List<char>(fingerprint.Length);
        foreach (var c in fingerprint)
        {
            if (c is ':' or ' ' or '-')
            {
                continue;
            }

            chars.Add(char.ToLowerInvariant(c));
        }

        return new string(chars.ToArray());
    }

    /// <summary>True when <paramref name="fingerprint"/> is a usable 64-char hex SPKI SHA-256 value.</summary>
    public static bool IsUsableFingerprint(string? fingerprint)
    {
        var normalized = NormalizeFingerprint(fingerprint);
        if (normalized.Length != 64)
        {
            return false;
        }

        foreach (var c in normalized)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates one certificate against the pinned fingerprint. A missing fingerprint falls back to the
    /// platform trust store (the M0 escape hatch — callers should log <see cref="WarningForMissingPin"/>).
    /// </summary>
    public static bool Validate(X509Certificate? certificate, string? pinnedFingerprint, SslPolicyErrors chainErrors, out string reason)
    {
        if (certificate is null)
        {
            reason = "server did not present a certificate";
            return false;
        }

        if (!IsUsableFingerprint(pinnedFingerprint))
        {
            reason = "no usable SPKI fingerprint configured; falling back to system trust";
            return chainErrors == SslPolicyErrors.None;
        }

        var actual = NormalizeFingerprint(ComputeSpkiSha256(ToX509Certificate2(certificate)));
        var expected = NormalizeFingerprint(pinnedFingerprint);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            reason = $"SPKI fingerprint mismatch: expected {expected}, got {actual}";
            return false;
        }

        reason = "pinned SPKI fingerprint matched";
        return true;
    }

    /// <summary>The warning to log when pairing carries no fingerprint (M0 fallback).</summary>
    public const string WarningForMissingPin =
        "pairing has no TLS SPKI fingerprint (fp); falling back to the system trust store. Re-pair with an fp before trusting this network.";

    /// <summary>
    /// Builds the <see cref="SocketsHttpHandler"/> / <see cref="ClientWebSocket"/> certificate callback for a
    /// pairing. A valid pinned fingerprint accepts exactly that certificate even though the relay is self-signed;
    /// otherwise the certificate must already be system-trusted.
    /// </summary>
    public static RemoteCertificateValidationCallback CreateValidationCallback(DshPairingInfo pairing, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var fingerprint = pairing.SpkiSha256;
        var pinned = IsUsableFingerprint(fingerprint);
        if (!pinned)
        {
            log?.Invoke(WarningForMissingPin);
        }

        return (_, certificate, _, chainErrors) =>
        {
            var ok = Validate(certificate, fingerprint, chainErrors, out var reason);
            if (!ok)
            {
                log?.Invoke($"TLS rejected: {reason}");
            }

            return ok;
        };
    }

    private static X509Certificate2 ToX509Certificate2(X509Certificate certificate) =>
        certificate as X509Certificate2 ?? new X509Certificate2(certificate);
}
