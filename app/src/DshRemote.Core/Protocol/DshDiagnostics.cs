using System.Diagnostics.Tracing;

namespace DshRemote.Core;

/// <summary>
/// Lightweight ETW/EventSource instrumentation for the phone client (no dependencies).
/// Listen with the provider name <c>DshRemote.Core</c>, e.g.
/// <c>dotnet-trace collect --providers DshRemote.Core:0xFFFFFFFF:5</c> or PerfView.
/// Times are milliseconds; history events distinguish <c>latest</c> (first screen) from <c>older</c> pages.
/// </summary>
[EventSource(Name = "DshRemote.Core")]
internal sealed class DshDiagnostics : EventSource
{
    public static readonly DshDiagnostics Instance = new();

    /// <summary>One history read (<paramref name="kind"/> is <c>latest</c> or <c>older</c>).</summary>
    [Event(1, Level = EventLevel.Informational)]
    public void HistoryPage(string kind, string sessionId, int records, long cursor, int hasMore, long elapsedMs) =>
        WriteEvent(1, kind, sessionId, records, cursor, hasMore, elapsedMs);

    /// <summary>One session open (<paramref name="kind"/> is <c>follow</c> or <c>lite</c>).</summary>
    [Event(2, Level = EventLevel.Informational)]
    public void SessionOpen(string kind, string sessionId, int records, long elapsedMs) =>
        WriteEvent(2, kind, sessionId, records, elapsedMs);

    /// <summary>One physical connect generation attempt.</summary>
    [Event(3, Level = EventLevel.Informational)]
    public void Connect(string host, int port, long elapsedMs, int ok) =>
        WriteEvent(3, host, port, elapsedMs, ok);
}
