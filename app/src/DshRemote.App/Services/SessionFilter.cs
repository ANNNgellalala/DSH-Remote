using DshRemote.Core;

namespace DshRemote.App.Services;

/// <summary>
/// Decides which persisted sessions belong in the phone's session list.
/// <para>
/// DSH exposes subagent (child) sessions in <c>session/list</c> too, but they can only be opened from their
/// parent (<c>session/agent-busy</c>), so the list hides them and excludes them from the visible counts.
/// </para>
/// </summary>
public static class SessionFilter
{
    /// <summary>True when the row is a child/subagent session that only its parent can open.</summary>
    public static bool IsSubagent(SessionSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ParentSessionId))
        {
            return true;
        }

        return string.Equals(summary.Origin, "subagent", StringComparison.OrdinalIgnoreCase);
    }
}
