using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Reads the relay-generated <c>%USERPROFILE%\.dsh\remote\pairing.json</c> (schemaVersion 1).
/// Primary shape (relay-dev contract):
/// <code>
/// { "schemaVersion":1, "host":"192.0.2.10", "port":8443, "token":"…", "fp":"&lt;64 lowercase hex&gt;",
///   "pairingUri":"dshremote://pair?v=1&amp;host=…&amp;port=…&amp;token=…&amp;fp=…&amp;name=…",
///   "name":"…", "updatedAt":"…", "loopbackPort":8443, "upstreamPort":54306, "protocol":"https" }
/// </code>
/// Unknown fields are ignored; <c>pairingUri</c> wins when present, top-level fields fill any gap.
/// </summary>
public static class DshPairingFile
{
    /// <summary>The canonical relay pairing file path.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh",
        "remote",
        "pairing.json");

    /// <summary>Loads and parses the pairing file, returning a human-readable failure reason otherwise.</summary>
    public static bool TryLoad(string? path, out DshPairingInfo? pairing, out string? error)
    {
        pairing = null;
        error = null;
        var resolved = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        try
        {
            if (!File.Exists(resolved))
            {
                error = $"pairing file not found: {resolved}";
                return false;
            }

            return TryParse(File.ReadAllText(resolved), out pairing, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"cannot read pairing file {resolved}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Parses one pairing document.</summary>
    public static bool TryParse(string json, out DshPairingInfo? pairing, out string? error)
    {
        pairing = null;
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"pairing document is not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "pairing document must be a JSON object";
                return false;
            }

            var schemaVersion = GetInt(root, "schemaVersion", "schema", "version") ?? 1;

            DshPairingInfo? fromUri = null;
            var uriText = GetString(root, "pairingUri", "uri", "pairingURI");
            if (!string.IsNullOrWhiteSpace(uriText) && !DshPairingInfo.TryParse(uriText, out fromUri))
            {
                fromUri = null;
            }

            // v2 gateway block: { url, online, fp }. The gateway URL is the primary endpoint.
            TryGetObject(root, "gateway", out var gateway);
            var modeText = GetString(root, "mode");
            var isGatewayBlock = gateway.ValueKind == JsonValueKind.Object;
            var mode = isGatewayBlock || (modeText?.Equals("gateway", StringComparison.OrdinalIgnoreCase) ?? false)
                ? DshPairingMode.Gateway
                : schemaVersion >= 2 && modeText is null && fromUri?.Mode == DshPairingMode.Gateway
                    ? DshPairingMode.Gateway
                    : DshPairingMode.Local;

            var host = GetString(root, "host", "hostname", "address", "ip");
            var portText = GetString(root, "port");
            var port = int.TryParse(portText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort)
                ? parsedPort
                : 0;
            var token = GetString(root, "token", "remoteToken", "launchToken", "launch_token") ?? fromUri?.Token;
            var fingerprint = GetString(root, "fp", "spki", "spkiSha256", "fingerprint") ?? fromUri?.SpkiSha256;
            var name = GetString(root, "name", "displayName") ?? fromUri?.Name;

            if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            {
                host = fromUri?.Host ?? host;
                port = fromUri?.Port ?? port;
            }

            bool? gatewayOnline = null;
            if (isGatewayBlock)
            {
                var gatewayUrl = GetString(gateway, "url", "origin", "endpoint");
                if (TryParseHttpEndpoint(gatewayUrl, out var gatewayHost, out var gatewayPort))
                {
                    host = gatewayHost;
                    port = gatewayPort;
                }

                fingerprint = GetString(gateway, "fp", "spki", "spkiSha256", "fingerprint") ?? fingerprint;
                gatewayOnline = GetBool(gateway, "online");
            }

            var localFallback = ParseLocalFallback(root, token, fingerprint, name);

            if (string.IsNullOrWhiteSpace(host))
            {
                error = "pairing document has no host";
                return false;
            }

            if (port is <= 0 or > 65535)
            {
                error = "pairing document has no valid port";
                return false;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                error = "pairing document has no token";
                return false;
            }

            pairing = new DshPairingInfo(
                host!,
                port,
                token!,
                fingerprint,
                name,
                mode,
                gatewayOnline,
                localFallback);
            return true;
        }
    }

    private static DshPairingInfo? ParseLocalFallback(JsonElement root, string? token, string? fingerprint, string? name)
    {
        var localUri = GetString(root, "localPairingUri", "localUri");
        if (!string.IsNullOrWhiteSpace(localUri) &&
            DshPairingInfo.TryParse(localUri, out var fromLocalUri) &&
            fromLocalUri is not null)
        {
            return fromLocalUri with { Mode = DshPairingMode.Local, GatewayOnline = null, LocalFallback = null };
        }

        var localHost = GetString(root, "localHost", "lanHost", "localAddress");
        var localPortText = GetString(root, "localPort", "lanPort");
        if (string.IsNullOrWhiteSpace(localHost) ||
            !int.TryParse(localPortText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var localPort) ||
            localPort is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return new DshPairingInfo(
            localHost!,
            localPort,
            token!,
            GetString(root, "localFp", "fp", "spki", "spkiSha256", "fingerprint") ?? fingerprint,
            name);
    }

    private static bool TryParseHttpEndpoint(string? url, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        host = uri.Host;
        port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        return true;
    }

    private static bool TryGetObject(JsonElement root, string property, out JsonElement value)
    {
        if (root.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? GetBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>Reads an optional integer field (used for <c>loopbackPort</c> in the smoke tool).</summary>
    public static int? ReadOptionalInt(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return null;
    }
}
