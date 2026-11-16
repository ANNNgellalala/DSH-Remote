using System.Globalization;
using System.Text.Json;

namespace DshRemote.Core;

/// <summary>Kind of one <c>workspace/follow</c> frame.</summary>
internal enum WorkspaceFrameKind
{
    Baseline,
    Replace,
    Upsert,
    Remove,
    Order,
    Archived,
    Pinned
}

/// <summary>One decoded <c>workspace/follow</c> frame.</summary>
internal sealed record WorkspaceFrame(
    WorkspaceFrameKind Kind,
    DshWorkspaceState? State = null,
    DshWorkspace? Workspace = null,
    string? WorkspaceId = null,
    IReadOnlyList<string>? Ids = null);

/// <summary>
/// Parsers for the <c>workspace/follow</c> stream and the M5 mutation results
/// (<c>workspace/archiveSession</c>, <c>session/create</c>, <c>session/attachment</c>).
/// </summary>
/// <remarks>
/// <c>workspace/follow</c> frames (mux <c>item.value</c>):
/// <code>
/// {"type":"baseline","value":{"items":[Workspace…],"archivedSessionIds":[…],"pinnedSessionIds":[…]}}
/// {"type":"replace","value":{…same…}}
/// {"type":"upsert","workspace":Workspace}
/// {"type":"remove","workspaceId":"…"}
/// {"type":"order","workspaceIds":[…]}
/// {"type":"archived","archivedSessionIds":[…]}
/// {"type":"pinned","pinnedSessionIds":[…]}
/// </code>
/// Workspace <c>createdAt</c>/<c>updatedAt</c> are ISO-8601 strings; they are normalized to epoch milliseconds.
/// </remarks>
internal static class WorkspaceParsing
{
    public static WorkspaceFrame? ParseFrame(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = DshParsing.GetString(value, "type");
        switch (type)
        {
            case "baseline":
            case "replace":
                if (!value.TryGetProperty("value", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var state = ParseState(payload);
                return state is null
                    ? null
                    : new WorkspaceFrame(type == "baseline" ? WorkspaceFrameKind.Baseline : WorkspaceFrameKind.Replace, State: state);
            case "upsert":
                if (!value.TryGetProperty("workspace", out var workspace) || workspace.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var parsed = ParseWorkspace(workspace);
                return parsed is null ? null : new WorkspaceFrame(WorkspaceFrameKind.Upsert, Workspace: parsed);
            case "remove":
                var removedId = DshParsing.GetString(value, "workspaceId");
                return string.IsNullOrEmpty(removedId)
                    ? null
                    : new WorkspaceFrame(WorkspaceFrameKind.Remove, WorkspaceId: removedId);
            case "order":
                return new WorkspaceFrame(WorkspaceFrameKind.Order, Ids: ParseStringArray(value, "workspaceIds"));
            case "archived":
                return new WorkspaceFrame(WorkspaceFrameKind.Archived, Ids: ParseStringArray(value, "archivedSessionIds"));
            case "pinned":
                return new WorkspaceFrame(WorkspaceFrameKind.Pinned, Ids: ParseStringArray(value, "pinnedSessionIds"));
            default:
                return null;
        }
    }

    /// <summary>Parses a <c>baseline</c>/<c>replace</c> <c>value</c> object.</summary>
    public static DshWorkspaceState? ParseState(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var workspaces = new List<DshWorkspace>();
        if (value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var workspace = ParseWorkspace(item);
                if (workspace is not null)
                {
                    workspaces.Add(workspace);
                }
            }
        }

        return new DshWorkspaceState(
            Workspaces: workspaces,
            ArchivedSessionIds: ParseStringArray(value, "archivedSessionIds"),
            PinnedSessionIds: ParseStringArray(value, "pinnedSessionIds"));
    }

    /// <summary>Parses one Workspace row; <c>null</c> when the id or title is missing.</summary>
    public static DshWorkspace? ParseWorkspace(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var workspaceId = DshParsing.GetString(item, "workspaceId");
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        return new DshWorkspace(
            WorkspaceId: workspaceId!,
            Path: DshParsing.GetString(item, "path"),
            Title: DshParsing.GetString(item, "title") ?? workspaceId!,
            SessionIds: ParseStringArray(item, "sessionIds"),
            CreatedAt: ParseTimestamp(item, "createdAt"),
            UpdatedAt: ParseTimestamp(item, "updatedAt"));
    }

    /// <summary>Reads the <c>archivedSessionIds</c> of an archive/unarchive mutation result.</summary>
    public static IReadOnlyList<string> ParseArchivedIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "workspace archive result must be an object");
        }

        return ParseStringArray(value, "archivedSessionIds");
    }

    /// <summary>Reads <c>session/create</c>'s <c>sessionId</c>.</summary>
    public static string ParseCreatedSessionId(JsonElement value)
    {
        var sessionId = value.ValueKind == JsonValueKind.Object ? DshParsing.GetString(value, "sessionId") : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/create result has no sessionId");
        }

        return sessionId!;
    }

    /// <summary>
    /// Parses <c>session/attachment</c> → <c>{attachment:{…},data:&lt;base64&gt;}</c>. The response attachment is
    /// authoritative; the caller's reference is used only if the server omits display fields.
    /// </summary>
    public static DshImageData ParseAttachmentResult(JsonElement value, DshImageRef fallback)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/attachment result must be an object");
        }

        var image = fallback;
        if (value.TryGetProperty("attachment", out var attachment) && attachment.ValueKind == JsonValueKind.Object &&
            DshContent.ParseImageRef(attachment) is { } parsed)
        {
            image = parsed;
        }

        var data = DshParsing.GetString(value, "data");
        if (string.IsNullOrEmpty(data))
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, "session/attachment result has no data");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ResultInvalid,
                "session/attachment data is not valid base64",
                null,
                ex);
        }

        return new DshImageData(image, bytes);
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, string property)
    {
        var result = new List<string>();
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
                {
                    result.Add(item.GetString()!);
                }
            }
        }

        return result;
    }

    private static long ParseTimestamp(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out var number) ? number : 0;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.ToUnixTimeMilliseconds();
        }

        return 0;
    }
}
