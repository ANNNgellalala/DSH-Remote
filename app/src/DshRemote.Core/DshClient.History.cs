using System.Diagnostics;

namespace DshRemote.Core;

/// <summary>
/// M6: first-screen history paging. The UI shows <see cref="IHistoryPagingClient.GetLatestHistoryAsync"/>
/// (one small page) immediately and pages upwards on scroll; live updates come from
/// <see cref="ISessionFollowClient.FollowEventsAsync"/> without waiting for its snapshot.
/// </summary>
public sealed partial class DshClient
{
    /// <inheritdoc />
    public async Task<SessionHistoryPage> GetLatestHistoryAsync(
        string sessionId,
        int maxMessages = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var stopwatch = Stopwatch.StartNew();
        var page = await QueryHistoryPageAsync(sessionId, beforeSeq: null, maxMessages, cancellationToken)
            .ConfigureAwait(false);
        page = page with
        {
            HasMoreOlder = page.HasMore,
            BeforeSeq = page.Records.Count > 0 ? page.Records[0].Seq : null
        };
        stopwatch.Stop();
        ReportHistoryPage("latest", sessionId, page, stopwatch.ElapsedMilliseconds);
        return page;
    }

    /// <inheritdoc />
    public async Task<SessionHistoryPage> GetOlderHistoryAsync(
        string sessionId,
        long beforeSeq,
        int maxMessages = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (beforeSeq <= 0)
        {
            return EmptyPage();
        }

        var stopwatch = Stopwatch.StartNew();
        var page = await QueryHistoryPageAsync(sessionId, beforeSeq, maxMessages, cancellationToken)
            .ConfigureAwait(false);
        // The server's `beforeSeq` is documented as a backward cursor; stay strict (`seq < beforeSeq`) so an
        // inclusive server implementation can never duplicate the boundary event.
        if (page.Records.Count > 0)
        {
            _options.Log?.Invoke(
                $"history older raw: beforeSeq={beforeSeq} rawFirst={page.Records[0].Seq} "
                + $"rawLast={page.Records[^1].Seq} rawCount={page.Records.Count} cursor={page.Cursor} hasMore={page.HasMore}");
        }

        var records = NormalizeAscending(page.Records.Where(record => record.Seq < beforeSeq));
        var hasOlder = page.HasMore;
        // `cursor` is the server-reported log cut (not necessarily this page's max); keep it as the read cut.
        var cursor = page.Cursor > 0 ? page.Cursor : (records.Count > 0 ? records[^1].Seq : Math.Min(page.Cursor, beforeSeq - 1));
        var normalized = new SessionHistoryPage(
            records,
            cursor,
            hasOlder,
            hasOlder,
            records.Count > 0 ? records[0].Seq : null);
        stopwatch.Stop();
        ReportHistoryPage("older", sessionId, normalized, stopwatch.ElapsedMilliseconds);
        return normalized;
    }

    /// <inheritdoc />
    public async Task<SessionLiteSnapshot> OpenSessionLiteAsync(
        string sessionId,
        int maxMessages = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var stopwatch = Stopwatch.StartNew();

        // Run the two cheap unary reads in parallel; neither needs the session/follow snapshot.
        var pageTask = GetLatestHistoryAsync(sessionId, maxMessages, cancellationToken);
        var stateTask = TryGetStateForLiteAsync(sessionId, cancellationToken);
        var page = await pageTask.ConfigureAwait(false);
        var state = await stateTask.ConfigureAwait(false);

        var lastSeq = page.Records.Count > 0
            ? Math.Max(page.Records[^1].Seq, page.Cursor)
            : -1;
        _followBaselines[sessionId] = new FollowBaseline(lastSeq, page.Cursor);

        stopwatch.Stop();
        DshDiagnostics.Instance.SessionOpen("lite", sessionId, page.Records.Count, stopwatch.ElapsedMilliseconds);
        _options.Log?.Invoke(
            $"open lite: {stopwatch.ElapsedMilliseconds}ms session={sessionId} records={page.Records.Count} "
            + $"cursor={page.Cursor} state={(state is null ? "null" : "ok")}");
        return new SessionLiteSnapshot(page, state);
    }

    /// <summary>
    /// One <c>session/events</c> read. Pages are ascending by seq; duplicates (defensive) are dropped.
    /// </summary>
    private async Task<SessionHistoryPage> QueryHistoryPageAsync(
        string sessionId,
        long? beforeSeq,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        var value = await CallAsync(
            "session/events",
            DshArgs.Events(sessionId, beforeSeq, Math.Max(1, maxMessages)),
            cancellationToken).ConfigureAwait(false);
        var page = DshParsing.ParseHistoryPage(value);
        return page with { Records = NormalizeAscending(page.Records) };
    }

    private async Task<SessionRuntimeState?> TryGetStateForLiteAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return await GetStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DshRemoteException ex)
        {
            // The first screen must not fail because the passive state read is refused (subagent session,
            // cold resume, …): history remains useful on its own.
            _options.Log?.Invoke($"open lite: state unavailable for {sessionId}: {ex.Code} {ex.Message}");
            return null;
        }
    }

    private void ReportHistoryPage(string kind, string sessionId, SessionHistoryPage page, long elapsedMs)
    {
        DshDiagnostics.Instance.HistoryPage(kind, sessionId, page.Records.Count, page.Cursor, page.HasMore ? 1 : 0, elapsedMs);
        _options.Log?.Invoke(
            $"history {kind}: {elapsedMs}ms session={sessionId} records={page.Records.Count} "
            + $"cursor={page.Cursor} hasMore={page.HasMore} beforeSeq={(page.BeforeSeq?.ToString() ?? "-")}");
    }

    private static SessionHistoryPage EmptyPage() =>
        new(Array.Empty<SessionEventRecord>(), 0, false, false, null);

    /// <summary>Sorts by seq ascending and drops repeated seqs (keeping the last copy).</summary>
    private static List<SessionEventRecord> NormalizeAscending(IEnumerable<SessionEventRecord> records)
    {
        var bySeq = new SortedDictionary<long, SessionEventRecord>();
        foreach (var record in records)
        {
            bySeq[record.Seq] = record;
        }

        return bySeq.Values.ToList();
    }
}
