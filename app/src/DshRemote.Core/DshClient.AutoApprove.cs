using System.Globalization;
using System.Text.Json;

namespace DshRemote.Core;

/// <summary>
/// Phone-side client for the relay-owned desktop auto-approval API
/// (<c>GET/PUT /__relay/auto-approve</c>, relay 0.7.0+).
///
/// The rules live on the desktop so approvals keep flowing while the phone app is
/// closed. These requests reuse the pinned TLS <see cref="System.Net.Http.HttpClient"/>
/// and authenticate with the pairing token as a bearer credential (plus the
/// gateway cookie when connected through the public relay), exactly like the ACL API.
/// </summary>
public sealed partial class DshClient : IAutoApproveClient
{
    private const string AutoApprovePath = "/__relay/auto-approve";

    /// <inheritdoc />
    public Task<DshAutoApproveConfig> GetAutoApproveAsync(CancellationToken cancellationToken = default)
        => SendRelayApiAsync(HttpMethod.Get, AutoApprovePath, null, ParseAutoApprove, "auto-approve", cancellationToken);

    /// <inheritdoc />
    public Task<DshAutoApproveConfig> SaveAutoApproveAsync(
        DshAutoApproveConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var payload = new Dictionary<string, object?>
        {
            ["enabled"] = config.Enabled,
            ["all"] = config.All,
            ["workspaces"] = config.Workspaces
                .Select(workspace => new Dictionary<string, object?>
                {
                    ["id"] = workspace.WorkspaceId,
                    ["path"] = workspace.Path,
                    ["title"] = workspace.Title
                })
                .ToList(),
            ["sessions"] = config.Sessions
                .Select(session => new Dictionary<string, object?>
                {
                    ["id"] = session.SessionId,
                    ["title"] = session.Title,
                    ["cwd"] = session.Cwd,
                    ["parentSessionId"] = session.ParentSessionId
                })
                .ToList()
        };

        return SendRelayApiAsync(HttpMethod.Put, AutoApprovePath, payload, ParseAutoApprove, "auto-approve", cancellationToken);
    }

    private static DshAutoApproveConfig ParseAutoApprove(JsonElement root)
    {
        var enabled = root.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
        var all = root.TryGetProperty("all", out var allElement) && allElement.ValueKind == JsonValueKind.True;
        var workspaces = new List<DshAutoApproveWorkspace>();
        if (root.TryGetProperty("workspaces", out var workspacesElement) && workspacesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in workspacesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                workspaces.Add(new DshAutoApproveWorkspace(
                    ReadAutoString(item, "id"),
                    ReadAutoString(item, "path"),
                    ReadAutoString(item, "title")));
            }
        }

        var sessions = new List<DshAutoApproveSession>();
        if (root.TryGetProperty("sessions", out var sessionsElement) && sessionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sessionsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadAutoString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                sessions.Add(new DshAutoApproveSession(
                    id!,
                    ReadAutoString(item, "title"),
                    ReadAutoString(item, "cwd"),
                    ReadAutoString(item, "parentSessionId")));
            }
        }

        DshAutoApproveStats? stats = null;
        if (root.TryGetProperty("stats", out var statsElement) && statsElement.ValueKind == JsonValueKind.Object)
        {
            stats = new DshAutoApproveStats(
                ReadAutoInt(statsElement, "allowed"),
                ReadAutoInt(statsElement, "errors"),
                ReadAutoTime(statsElement, "lastAllowedAt"));
        }

        var recent = new List<DshAutoApproveAudit>();
        if (root.TryGetProperty("recent", out var recentElement) && recentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in recentElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var at = ReadAutoTime(item, "at");
                if (at is null)
                {
                    continue;
                }

                recent.Add(new DshAutoApproveAudit(
                    at.Value,
                    ReadAutoString(item, "sessionId"),
                    ReadAutoString(item, "toolName"),
                    ReadAutoString(item, "callId"),
                    ReadAutoString(item, "rule")));
            }
        }

        return new DshAutoApproveConfig(
            enabled,
            all,
            workspaces,
            sessions,
            ReadAutoLong(root, "revision"),
            ReadAutoTime(root, "updatedAt"),
            ReadAutoString(root, "updatedBy"),
            stats,
            recent,
            ReadAutoString(root, "error"),
            ReadAutoString(root, "relayVersion"),
            ReadAutoString(root, "file"));
    }

    private static string? ReadAutoString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadAutoInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static long ReadAutoLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static DateTimeOffset? ReadAutoTime(JsonElement element, string name)
    {
        var text = ReadAutoString(element, name);
        return !string.IsNullOrWhiteSpace(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }
}
