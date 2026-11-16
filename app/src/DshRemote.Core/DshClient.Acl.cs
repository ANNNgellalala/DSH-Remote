using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Phone-side client for the relay-owned Windows ACL guard API
/// (<c>/__relay/acl/status</c>, <c>/__relay/acl/check</c>).
///
/// These routes are served by the relay plugin itself rather than the DSH Remote RPC namespace, so the
/// requests reuse the pinned TLS <see cref="HttpClient"/> and authenticate with the pairing token as a
/// bearer credential (the phone holds exactly one pairing credential, gateway or LAN).
/// </summary>
public sealed partial class DshClient : IAclGuardClient
{
    private const string AclStatusPath = "/__relay/acl/status";
    private const string AclCheckPath = "/__relay/acl/check";

    /// <inheritdoc />
    public Task<DshAclGuardStatus> GetAclStatusAsync(CancellationToken cancellationToken = default)
        => SendRelayApiAsync(HttpMethod.Get, AclStatusPath, null, ParseAclStatus, "ACL", cancellationToken);

    /// <inheritdoc />
    public Task<DshAclCheckResult> CheckAclAsync(bool repair = true, string? root = null, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?> { ["repair"] = repair };
        if (!string.IsNullOrWhiteSpace(root))
        {
            payload["root"] = root;
        }

        return SendRelayApiAsync(HttpMethod.Post, AclCheckPath, payload, ParseAclCheck, "ACL", cancellationToken);
    }

    private async Task<T> SendRelayApiAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        Func<JsonElement, T> parse,
        string feature,
        CancellationToken cancellationToken)
    {
        var pairing = Pairing
            ?? throw new DshRemoteException(DshRemoteException.Codes.Unauthorized, feature + " request needs a paired connection");
        var (connection, _) = await WaitForConnectionAsync(-1, cancellationToken).ConfigureAwait(false);
        var http = GetHttpClient();

        using var request = new HttpRequestMessage(method, new Uri(connection.BaseUri, path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + pairing.Token);
        // The public gateway routes a request to a desktop by its dsh-auth
        // cookie; without this header the gateway answers 404 unknown-session
        // before the request ever reaches the relay's ACL API. The relay itself
        // authenticates with the bearer above, so both hops are covered.
        request.Headers.TryAddWithoutValidation("Cookie", connection.CookieHeader);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new DshRemoteException(
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? DshRemoteException.Codes.Unauthorized
                    : DshRemoteException.Codes.Forbidden,
                $"{feature} request '{path}' answered HTTP {(int)response.StatusCode}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var requirement = feature == "ACL"
                ? "Windows 权限体检需要 relay 0.5.0+"
                : $"桌面自动审批需要 relay 0.7.1+（当前 {feature} 接口不存在）";
            throw new DshRemoteException(
                DshRemoteException.Codes.ConnectFailed,
                "请求没有到达桌面 relay：要么中继网关未能把该设备路由到桌面（请重新连接/重新扫码），要么桌面 relay 版本过低（" + requirement + "）");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.Transport,
                $"{feature} request '{path}' answered HTTP {(int)response.StatusCode}");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, $"{feature} API returned a non-object payload");
        }

        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, $"{feature} API error: " + (message ?? "unknown"));
        }

        return parse(root);
    }

    private static DshAclGuardStatus ParseAclStatus(JsonElement root)
    {
        var guard = root.TryGetProperty("guard", out var guardElement) && guardElement.ValueKind == JsonValueKind.Object
            ? guardElement
            : default;
        var enabled = guard.ValueKind == JsonValueKind.Object
            && guard.TryGetProperty("enabled", out var enabledElement)
            && enabledElement.ValueKind == JsonValueKind.True;
        var version = guard.ValueKind == JsonValueKind.Object
            && guard.TryGetProperty("version", out var versionElement)
            && versionElement.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 0;

        var roots = new List<DshAclRootStatus>();
        if (guard.ValueKind == JsonValueKind.Object
            && guard.TryGetProperty("roots", out var rootsElement)
            && rootsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rootsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                roots.Add(new DshAclRootStatus(
                    ReadString(item, "root") ?? string.Empty,
                    ReadString(item, "state") ?? "unknown",
                    ReadTime(item, "preparedAt"),
                    ReadTime(item, "lastSeenAt")));
            }
        }

        var candidateCount = root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array
            ? candidates.GetArrayLength()
            : roots.Count;

        return new DshAclGuardStatus(
            enabled,
            version,
            roots,
            candidateCount,
            ReadTime(root, "generatedAt"));
    }

    private static DshAclCheckResult ParseAclCheck(JsonElement root)
    {
        var repair = !(root.TryGetProperty("repair", out var repairElement) && repairElement.ValueKind == JsonValueKind.False);
        var roots = new List<DshAclRootCheck>();
        if (root.TryGetProperty("roots", out var rootsElement) && rootsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rootsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var ready = item.TryGetProperty("ready", out var readyElement) && readyElement.ValueKind == JsonValueKind.True;
                var repaired = item.TryGetProperty("repaired", out var repairedElement) && repairedElement.ValueKind == JsonValueKind.True;
                var duration = item.TryGetProperty("durationMs", out var durationElement) && durationElement.TryGetInt64(out var parsedDuration)
                    ? parsedDuration
                    : 0;
                roots.Add(new DshAclRootCheck(
                    ReadString(item, "root") ?? string.Empty,
                    ready,
                    repaired,
                    duration,
                    ReadString(item, "reason")));
            }
        }

        return new DshAclCheckResult(repair, roots, ReadTime(root, "generatedAt"));
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadTime(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        return !string.IsNullOrWhiteSpace(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }
}
