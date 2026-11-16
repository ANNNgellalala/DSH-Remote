using System.Text;
using System.Text.Json;
using DshRemote.Core;

namespace DshRemote.Smoke;

/// <summary>
/// End-to-end smoke for <see cref="DshClient"/> against a real DSH desktop runtime (directly over the
/// relay's TLS pairing, or against a loopback dev instance without TLS).
/// </summary>
internal static class Program
{
    private sealed class Options
    {
        public string? PairingPath { get; set; }
        public string? PairingUri { get; set; }
        public string? DevUrl { get; set; }
        public bool Loopback { get; set; }
        public string? SessionId { get; set; }
        public string? WatchSessionId { get; set; }
        public string? FollowSessionId { get; set; }
        public string? Prompt { get; set; }
        public bool Events { get; set; }
        public string? ApproveRequestId { get; set; }
        public bool ApproveAny { get; set; }
        public string? Outcome { get; set; }
        public string? AnswerRequestId { get; set; }
        public string? AnswersJson { get; set; }
        public string? AnswerAnySelections { get; set; }
        public bool Steer { get; set; }
        public int WatchTimeoutSeconds { get; set; } = 60;
        public int WatchLimit { get; set; } = 20;
        public int SessionsToShow { get; set; } = 5;
        public int HistoryEvents { get; set; } = 3;
        public bool Verbose { get; set; }
        public bool Json { get; set; }
        public bool Workspaces { get; set; }
        public string? CreateSessionTarget { get; set; }
        public string? ArchiveSessionId { get; set; }
        public bool Unarchive { get; set; }
        public List<string> ImagePaths { get; } = new();
        public string? ImagesSessionId { get; set; }
        public string? GetImageSessionId { get; set; }
        public string? PageTestSessionId { get; set; }
        public int PageSize { get; set; } = 100;
        public int MaxPages { get; set; } = 60;
        public string? PerfOpenSessionId { get; set; }
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Options options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        var previousColor = Console.ForegroundColor;
        try
        {
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (DshRemoteException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FAILED [{ex.Code}] {ex.Message}");
            if (ex.Details is { ValueKind: not JsonValueKind.Undefined } details)
            {
                Console.Error.WriteLine("details: " + details.GetRawText());
            }

            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine("inner: " + ex.InnerException.Message);
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FAILED {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        var pairing = ResolvePairing(options, out var loopbackPort);
        if (options.Loopback)
        {
            // v2 (gateway) pairings carry the LAN relay as LocalFallback; the loopback mirror speaks
            // the LAN relay's TLS identity and token, not the gateway's remote token.
            if (pairing.LocalFallback is { } local)
            {
                pairing = local with { Host = "127.0.0.1", Port = loopbackPort ?? local.Port };
            }
            else if (loopbackPort is { } port)
            {
                pairing = pairing with { Host = "127.0.0.1", Port = port };
            }
        }

        Console.WriteLine("== DshRemote.Smoke ==");
        Console.WriteLine($"target   : {SchemeOf(pairing)}://{pairing.Host}:{pairing.Port}");
        Console.WriteLine($"token    : {Mask(pairing.Token)}");
        Console.WriteLine($"pin (fp) : {(pairing.SpkiSha256 is { Length: > 0 } fp ? fp[..Math.Min(16, fp.Length)] + "…" : "<none: dev fallback, system trust>")}");
        Console.WriteLine($"name     : {pairing.Name ?? "-"}");
        Console.WriteLine($"mode     : {pairing.Mode.ToString().ToLowerInvariant()}"
                          + (pairing.Mode == DshPairingMode.Gateway
                              ? $"  online={(pairing.GatewayOnline?.ToString().ToLowerInvariant() ?? "unknown")}"
                              : string.Empty)
                          + (pairing.LocalFallback is { } fallback
                              ? $"  localFallback={fallback.Host}:{fallback.Port}"
                              : string.Empty));
        Console.WriteLine();

        var logs = options.Verbose ? (Action<string>)(line => Console.Error.WriteLine("  [log] " + line)) : null;
        await using var client = new DshClient(new DshClientOptions
        {
            Log = logs,
            Reconnect = new ReconnectPolicy { MaxAttempts = 5 }
        });
        client.StateChanged += (_, state) => Console.Error.WriteLine($"  [state] {state}");

        Console.WriteLine("-- connect --");
        var connectStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.ConnectAsync(pairing);
        connectStopwatch.Stop();
        Console.WriteLine($"state: {client.State}  ({connectStopwatch.ElapsedMilliseconds}ms)");
        Console.WriteLine();

        if (options.PageTestSessionId is { Length: > 0 } pageTestSession)
        {
            await RunPageTestAsync(client, pageTestSession, options.PageSize, options.MaxPages).ConfigureAwait(false);
        }

        if (options.PerfOpenSessionId is { Length: > 0 } perfSession)
        {
            await RunPerfOpenAsync(client, perfSession).ConfigureAwait(false);
        }

        if (options.Workspaces)
        {
            await PrintWorkspaceStateAsync(client, "-- workspace/follow (baseline) --").ConfigureAwait(false);
        }

        if (options.CreateSessionTarget is { Length: > 0 } createTarget)
        {
            Console.WriteLine();
            Console.WriteLine("-- session/create --");
            var created = await CreateSessionFromTargetAsync(client, createTarget).ConfigureAwait(false);
            Console.WriteLine($"created: {created}");
        }

        if (options.ArchiveSessionId is { Length: > 0 } archiveTarget)
        {
            Console.WriteLine();
            Console.WriteLine($"-- workspace/archiveSession ({archiveTarget}) --");
            var archivedState = await client.ArchiveSessionAsync(archiveTarget, stopActivity: true).ConfigureAwait(false);
            Console.WriteLine($"archived ids: {archivedState.ArchivedSessionIds.Count}  "
                              + $"contains target: {archivedState.ArchivedSessionIds.Contains(archiveTarget)}");
            if (options.Unarchive)
            {
                Console.WriteLine($"-- workspace/unarchiveSession ({archiveTarget}) --");
                var restored = await client.UnarchiveSessionAsync(archiveTarget).ConfigureAwait(false);
                Console.WriteLine($"archived ids after unarchive: {restored.ArchivedSessionIds.Count}  "
                                  + $"contains target: {restored.ArchivedSessionIds.Contains(archiveTarget)}");
            }
        }

        if (options.ImagesSessionId is { Length: > 0 } imagesSession)
        {
            Console.WriteLine();
            Console.WriteLine($"-- image refs ({imagesSession}) --");
            await PrintImagesAsync(client, imagesSession).ConfigureAwait(false);
        }

        if (options.GetImageSessionId is { Length: > 0 } getImageSession)
        {
            Console.WriteLine();
            Console.WriteLine($"-- session/attachment ({getImageSession}) --");
            var fetched = await FetchFirstImageAsync(client, getImageSession).ConfigureAwait(false);
            if (fetched is null)
            {
                Console.WriteLine("  (no image attachment found in the latest history page)");
            }
            else
            {
                Console.WriteLine($"  mediaType={fetched.Image.MediaType} bytes={fetched.Bytes.Length} "
                                  + $"dimensions={fetched.Image.Width}x{fetched.Image.Height} name={fetched.Image.Name ?? "-"}");
                Console.WriteLine("  magic=" + Convert.ToHexString(fetched.Bytes.Take(8).ToArray()));
            }
        }
        Console.WriteLine("-- session/list --");
        var sessions = await client.ListSessionsAsync();
        Console.WriteLine($"sessions: {sessions.Count}");
        foreach (var session in sessions.Take(options.SessionsToShow))
        {
            var title = string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title;
            var age = session.UpdatedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(session.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "-";
            Console.WriteLine($"  {(session.Running ? "●" : "○")} {session.SessionId}");
            Console.WriteLine($"      title: {title}");
            Console.WriteLine($"      cwd  : {session.Cwd ?? "-"}");
            Console.WriteLine($"      model: {session.Model ?? "-"}  updated: {age}  blank: {session.Blank}");
            if (session.Origin is not null || session.ParentSessionId is not null)
            {
                Console.WriteLine($"      origin: {session.Origin ?? "-"}"
                                  + (session.ParentSessionId is null ? string.Empty : $"  parent: {session.ParentSessionId}"));
            }
        }

        if (options.Json)
        {
            Console.WriteLine();
            Console.WriteLine("-- session/list (raw projection) --");
            Console.WriteLine(JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
        }

        // Prefer top-level sessions; child (subagent) sessions reject durable history and are skipped.
        var candidates = options.SessionId is { Length: > 0 } explicitId
            ? new List<string> { explicitId }
            : sessions.Where(s => !s.Blank)
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => s.SessionId)
                .Take(10)
                .ToList();

        SessionHistoryPage? history = null;
        SessionRuntimeState? state = null;
        string? chosen = null;
        foreach (var candidate in candidates)
        {
            try
            {
                history = await client.GetHistoryAsync(candidate, maxMessages: Math.Max(1, options.HistoryEvents));
                state = await client.GetStateAsync(candidate);
                chosen = candidate;
                break;
            }
            catch (DshRemoteException ex) when (ex.Code.StartsWith("session/", StringComparison.Ordinal))
            {
                Console.WriteLine($"  (skip {candidate}: {ex.Code} — {OneLine(ex.Message, 120)})");
            }
        }

        if (chosen is null || history is null || state is null)
        {
            Console.WriteLine();
            Console.WriteLine("(no inspectable session; use --session <sessionId> or --watch <sessionId>)");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"-- session/events ({chosen}) --");
            Console.WriteLine($"records: {history.Records.Count}  cursor: {history.Cursor}  hasMore: {history.HasMore}");
            foreach (var record in history.Records.TakeLast(options.HistoryEvents))
            {
                var text = OneLine(record.TextOf(), 160);
                Console.WriteLine($"  seq={record.Seq} type={record.Type} time={record.Time}"
                                  + (text.Length > 0 ? $" text=\"{text}\"" : string.Empty));
            }

            Console.WriteLine();
            Console.WriteLine($"-- session/state ({chosen}) --");
            Console.WriteLine($"cursor: {state.Cursor}  active: {state.Active}  running: {state.Running}");
            Console.WriteLine($"queue: {state.Queue.Count}  pendingInput: [{string.Join(", ", state.PendingInput)}]");
            foreach (var queued in state.Queue)
            {
                Console.WriteLine($"  queue[{queued.Placement}] {OneLine(queued.Text, 100)}");
            }
        }

        var watchTarget = options.WatchSessionId;

        if (options.Prompt is { Length: > 0 } || options.ImagePaths.Count > 0)
        {
            var promptText = options.Prompt ?? string.Empty;
            var promptSession = chosen ?? options.SessionId;
            if (promptSession is null)
            {
                Console.WriteLine();
                Console.WriteLine("(no session to prompt; pass --session <sessionId>)");
            }
            else
            {
                var images = options.ImagePaths.Select(LoadImageAttachment).ToList();
                Console.WriteLine();
                Console.WriteLine($"-- session/prompt ({promptSession}, {(options.Steer ? "steer" : "queue")}, "
                                  + $"images={images.Count}) --");
                var requestId = images.Count == 0
                    ? await client.SendPromptAsync(promptSession, promptText, options.Steer)
                    : await client.SendPromptAsync(promptSession, promptText, images, options.Steer);
                Console.WriteLine($"accepted prompt requestId: {requestId}");
                foreach (var image in images)
                {
                    Console.WriteLine($"  attached {image.Name ?? "-"} {image.MediaType} bytes={image.Data.Length}");
                }
            }
        }

        if (options.FollowSessionId is { Length: > 0 } followSession)
        {
            Console.WriteLine();
            Console.WriteLine($"-- session/follow ({followSession}) --");
            var snapshot = await client.OpenSessionAsync(followSession, maxMessages: Math.Max(1, options.HistoryEvents));
            Console.WriteLine($"snapshot: records={snapshot.Events.Count} cursor={snapshot.Cursor} hasMore={snapshot.HasMore} "
                              + $"state={(snapshot.State is null ? "null" : $"running={snapshot.State.Running} active={snapshot.State.Active}")}");
            foreach (var record in snapshot.Events.TakeLast(options.HistoryEvents))
            {
                var text = OneLine(record.TextOf(), 160);
                Console.WriteLine($"  snapshot seq={record.Seq} type={record.Type}"
                                  + (text.Length > 0 ? $" text=\"{text}\"" : string.Empty));
            }

            using var followStop = new CancellationTokenSource();
            if (options.WatchTimeoutSeconds > 0)
            {
                followStop.CancelAfter(TimeSpan.FromSeconds(options.WatchTimeoutSeconds));
            }

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                followStop.Cancel();
            };

            var followed = 0;
            try
            {
                await foreach (var record in client.FollowEventsAsync(followSession, followStop.Token))
                {
                    followed++;
                    var text = OneLine(record.TextOf(), 160);
                    Console.WriteLine($"  follow seq={record.Seq} type={record.Type}"
                                      + (text.Length > 0 ? $" text=\"{text}\"" : string.Empty));
                    if (followed >= options.WatchLimit)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  (follow cancelled)");
            }
            catch (SessionFollowResyncException ex)
            {
                Console.WriteLine($"  (follow resync required: lastSeq={ex.LastSeq} throughSeq={ex.ThroughSeq})");
            }

            Console.WriteLine($"followed events: {followed}");
        }

        if (watchTarget is not null && options.WatchLimit > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"-- session/observeEvent ({watchTarget}) — Ctrl+C to stop --");
            using var stop = new CancellationTokenSource();
            if (options.WatchTimeoutSeconds > 0)
            {
                stop.CancelAfter(TimeSpan.FromSeconds(options.WatchTimeoutSeconds));
            }

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.Cancel();
            };

            var seen = 0;
            try
            {
                await foreach (var record in client.ObserveEventsAsync(watchTarget, stop.Token))
                {
                    seen++;
                    var text = OneLine(record.TextOf(), 160);
                    Console.WriteLine($"  {DateTimeOffset.FromUnixTimeMilliseconds(record.Time):HH:mm:ss} seq={record.Seq} "
                                      + $"{record.Type}" + (text.Length > 0 ? $" \"{text}\"" : string.Empty));
                    if (seen >= options.WatchLimit)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  (watch cancelled)");
            }

            Console.WriteLine($"watched events: {seen}");
        }

        if (options.Events || options.ApproveAny || options.AnswerAnySelections is not null || options.ApproveRequestId is not null || options.AnswerRequestId is not null)
        {
            await RunEventsAsync(client, options).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("-- disconnect --");
        await client.DisconnectAsync();
        Console.WriteLine($"state: {client.State}");
        Console.WriteLine("OK");
        return 0;
    }

    private static async Task RunEventsAsync(DshClient client, Options options)
    {
        Console.WriteLine();
        Console.WriteLine("-- $events (approvals/questions) --");
        using var stop = new CancellationTokenSource();
        if (options.WatchTimeoutSeconds > 0)
        {
            stop.CancelAfter(TimeSpan.FromSeconds(options.WatchTimeoutSeconds));
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        var approvals = Task.Run(async () =>
        {
            try
            {
                await foreach (var approval in client.ApprovalsAsync(stop.Token))
                {
                    Console.WriteLine($"  [approval] requestId={approval.RequestId}");
                    Console.WriteLine($"      session={approval.SessionId} tool={approval.ToolName} callId={approval.CallId ?? "-"}");
                    Console.WriteLine($"      reason={OneLine(approval.DisplayReason ?? approval.Reason, 200)}");
                    if (options.ApproveRequestId == approval.RequestId || options.ApproveAny)
                    {
                        var outcome = ParseOutcome(options.Outcome);
                        await client.RespondApprovalAsync(approval.RequestId, outcome);
                        Console.WriteLine($"  -> approval {approval.RequestId} answered {outcome}");
                        if (!options.ApproveAny && options.AnswerRequestId is null)
                        {
                            stop.Cancel();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout / Ctrl+C.
            }
        });

        var questions = Task.Run(async () =>
        {
            try
            {
                await foreach (var question in client.QuestionsAsync(stop.Token))
                {
                    Console.WriteLine($"  [question] requestId={question.RequestId} session={question.SessionId}");
                    foreach (var item in question.Questions)
                    {
                        Console.WriteLine($"      q[{item.Id}] {OneLine(item.Text, 160)}"
                                          + $" options=[{string.Join(", ", item.Options)}] multiple={item.AllowMultiple}");
                    }

                    if (options.AnswerAnySelections is not null)
                    {
                        var selected = options.AnswerAnySelections
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var autoAnswers = question.Questions
                            .Select(item => new DshQuestionAnswerItem(item.Id, selected))
                            .ToList();
                        await client.RespondQuestionAsync(question.RequestId, autoAnswers);
                        Console.WriteLine($"  -> question {question.RequestId} auto-answered with [{string.Join(", ", selected)}]");
                        stop.Cancel();
                    }
                    else if (options.AnswerRequestId == question.RequestId)
                    {
                        var (answers, cancelled) = ParseAnswers(options.AnswersJson);
                        await client.RespondQuestionAsync(question.RequestId, answers, cancelled);
                        Console.WriteLine($"  -> question {question.RequestId} answered (cancelled={cancelled})");
                        if (options.ApproveRequestId is null)
                        {
                            stop.Cancel();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout / Ctrl+C.
            }
        });

        try
        {
            await Task.WhenAll(approvals, questions).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout.
        }

        if (!options.ApproveAny && (options.ApproveRequestId is not null || options.AnswerRequestId is not null))
        {
            Console.WriteLine($"  (requested {options.ApproveRequestId ?? options.AnswerRequestId}; "
                              + "if it was not listed above the host no longer holds it as pending)");
        }
        else
        {
            Console.WriteLine("  ($events subscription ended)");
        }
    }

    private static DshApprovalOutcome ParseOutcome(string? outcome) => outcome?.ToLowerInvariant() switch
    {
        "rejected" or "reject" => DshApprovalOutcome.Rejected,
        "cancelled" or "cancel" => DshApprovalOutcome.Cancelled,
        _ => DshApprovalOutcome.AllowedOnce
    };

    private static (IReadOnlyList<DshQuestionAnswerItem> Answers, bool Cancelled) ParseAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (Array.Empty<DshQuestionAnswerItem>(), false);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
            {
                return (Array.Empty<DshQuestionAnswerItem>(), true);
            }

            if (root.TryGetProperty("answers", out var nested))
            {
                root = nested;
            }
        }

        var answers = new List<DshQuestionAnswerItem>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var selected = new List<string>();
                if (item.TryGetProperty("selected", out var selectedElement) && selectedElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in selectedElement.EnumerateArray())
                    {
                        if (choice.ValueKind == JsonValueKind.String && choice.GetString() is { } text)
                        {
                            selected.Add(text);
                        }
                    }
                }

                var custom = item.TryGetProperty("custom", out var customElement) && customElement.ValueKind == JsonValueKind.String
                    ? customElement.GetString()
                    : null;
                answers.Add(new DshQuestionAnswerItem(id, selected, custom));
            }
        }

        return (answers, false);
    }

    private static async Task RunPageTestAsync(DshClient client, string sessionId, int pageSize, int maxPages)
    {
        pageSize = Math.Max(1, pageSize);
        maxPages = Math.Max(2, maxPages);
        Console.WriteLine();
        Console.WriteLine($"-- history paging ({sessionId}, pageSize={pageSize}, maxPages={maxPages}) --");

        var latestWatch = System.Diagnostics.Stopwatch.StartNew();
        var latest = await client.GetLatestHistoryAsync(sessionId, pageSize).ConfigureAwait(false);
        latestWatch.Stop();
        Console.WriteLine($"latest: {latestWatch.ElapsedMilliseconds}ms records={latest.Records.Count} "
                          + $"cursor={latest.Cursor} hasMoreOlder={latest.HasMoreOlder} beforeSeq={latest.BeforeSeq?.ToString() ?? "-"}");
        if (latest.Records.Count > 0)
        {
            Console.WriteLine($"  seq range: {latest.Records[0].Seq}..{latest.Records[^1].Seq}  "
                              + $"ascending={IsAscending(latest.Records.Select(r => r.Seq))}");
        }

        if (latest.BeforeSeq is { } repeatedBefore)
        {
            var firstRead = await client.GetOlderHistoryAsync(sessionId, repeatedBefore, pageSize).ConfigureAwait(false);
            var secondRead = await client.GetOlderHistoryAsync(sessionId, repeatedBefore, pageSize).ConfigureAwait(false);
            var identical = string.Join(",", firstRead.Records.Select(r => r.Seq)) ==
                            string.Join(",", secondRead.Records.Select(r => r.Seq));
            Console.WriteLine($"idempotent repeat beforeSeq={repeatedBefore}: {identical} (records={firstRead.Records.Count})");
        }

        var seen = new HashSet<long>();
        foreach (var record in latest.Records)
        {
            seen.Add(record.Seq);
        }

        var duplicates = 0;
        var overlaps = 0;
        var gaps = 0;
        var pages = 1;
        var total = latest.Records.Count;
        var current = latest;
        var walked = System.Diagnostics.Stopwatch.StartNew();
        while (current.HasMoreOlder && current.BeforeSeq is { } before && pages < maxPages)
        {
            var older = await client.GetOlderHistoryAsync(sessionId, before, pageSize).ConfigureAwait(false);
            pages++;
            if (older.Records.Count == 0)
            {
                Console.WriteLine($"  page {pages}: empty page while hasMoreOlder=true (before={before})");
                break;
            }

            if (!IsAscending(older.Records.Select(r => r.Seq)))
            {
                Console.WriteLine($"  page {pages}: NOT ascending");
            }

            foreach (var record in older.Records)
            {
                if (record.Seq >= before)
                {
                    overlaps++;
                }

                if (!seen.Add(record.Seq))
                {
                    duplicates++;
                }
            }

            // A contiguous log means the newest event of the older page is exactly before-1.
            if (older.Records[^1].Seq != before - 1)
            {
                gaps++;
            }

            total += older.Records.Count;
            current = older;
        }

        walked.Stop();
        Console.WriteLine($"walk: pages={pages} records={total} duplicates={duplicates} overlaps={overlaps} "
                          + $"boundaryGaps={gaps} stoppedHasMoreOlder={current.HasMoreOlder} "
                          + $"walkMs={walked.ElapsedMilliseconds}");
        Console.WriteLine($"oldestReached={current.Records.FirstOrDefault()?.Seq.ToString() ?? "-"} "
                          + $"remainingHasMoreOlder={current.HasMoreOlder}");
    }

    private static async Task RunPerfOpenAsync(DshClient client, string sessionId)
    {
        Console.WriteLine();
        Console.WriteLine($"-- open perf ({sessionId}) --");

        SessionSnapshot? followSnapshot = null;
        var followWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            followSnapshot = await client.OpenSessionAsync(sessionId, 80).ConfigureAwait(false);
        }
        catch (DshRemoteException ex)
        {
            Console.WriteLine($"  follow snapshot failed: [{ex.Code}] {ex.Message}");
        }

        followWatch.Stop();

        var latestWatch = System.Diagnostics.Stopwatch.StartNew();
        var latest = await client.GetLatestHistoryAsync(sessionId, 30).ConfigureAwait(false);
        latestWatch.Stop();

        var stateWatch = System.Diagnostics.Stopwatch.StartNew();
        SessionRuntimeState? state = null;
        try
        {
            state = await client.GetStateAsync(sessionId).ConfigureAwait(false);
        }
        catch (DshRemoteException ex)
        {
            Console.WriteLine($"  session/state failed: [{ex.Code}] {ex.Message}");
        }

        stateWatch.Stop();

        var liteWatch = System.Diagnostics.Stopwatch.StartNew();
        SessionLiteSnapshot? lite = null;
        try
        {
            lite = await client.OpenSessionLiteAsync(sessionId, 30).ConfigureAwait(false);
        }
        catch (DshRemoteException ex)
        {
            Console.WriteLine($"  lite open failed: [{ex.Code}] {ex.Message}");
        }

        liteWatch.Stop();

        var olderWatch = System.Diagnostics.Stopwatch.StartNew();
        if (latest.BeforeSeq is { } before)
        {
            await client.GetOlderHistoryAsync(sessionId, before, 30).ConfigureAwait(false);
        }

        olderWatch.Stop();

        Console.WriteLine($"  follow snapshot (old first screen): {followWatch.ElapsedMilliseconds}ms "
                          + $"records={followSnapshot?.Events.Count ?? 0}");
        Console.WriteLine($"  latest page only                  : {latestWatch.ElapsedMilliseconds}ms records={latest.Records.Count}");
        Console.WriteLine($"  session/state only                : {stateWatch.ElapsedMilliseconds}ms");
        Console.WriteLine($"  OpenSessionLite (latest+state)    : {liteWatch.ElapsedMilliseconds}ms "
                          + $"records={lite?.Page.Records.Count ?? 0} hasState={lite?.State is not null}");
        Console.WriteLine($"  one older page                    : {olderWatch.ElapsedMilliseconds}ms");

        if (followWatch.ElapsedMilliseconds > 0)
        {
            var saved = followWatch.ElapsedMilliseconds - liteWatch.ElapsedMilliseconds;
            var percent = saved * 100.0 / followWatch.ElapsedMilliseconds;
            Console.WriteLine($"  first-screen improvement          : {saved}ms ({percent:F0}% faster than follow snapshot)");
        }
    }

    private static bool IsAscending(IEnumerable<long> values)
    {
        var previous = long.MinValue;
        foreach (var value in values)
        {
            if (value < previous)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }
    private static async Task<DshWorkspaceState> PrintWorkspaceStateAsync(DshClient client, string heading)
    {
        Console.WriteLine();
        Console.WriteLine(heading);
        var state = await client.GetWorkspaceStateAsync().ConfigureAwait(false);
        Console.WriteLine($"workspaces: {state.Workspaces.Count}  archived: {state.ArchivedSessionIds.Count}  "
                          + $"pinned: {state.PinnedSessionIds.Count}");
        foreach (var workspace in state.Workspaces.Take(20))
        {
            Console.WriteLine($"  {workspace.WorkspaceId}  sessions={workspace.SessionIds.Count}  "
                              + $"title={workspace.Title}  path={workspace.Path}");
        }

        if (state.ArchivedSessionIds.Count > 0)
        {
            Console.WriteLine("  archived ids: " + string.Join(", ", state.ArchivedSessionIds.Take(10))
                              + (state.ArchivedSessionIds.Count > 10 ? " …" : string.Empty));
        }

        if (state.PinnedSessionIds.Count > 0)
        {
            Console.WriteLine("  pinned ids: " + string.Join(", ", state.PinnedSessionIds.Take(10)));
        }

        return state;
    }

    private static async Task<string> CreateSessionFromTargetAsync(DshClient client, string target)
    {
        var looksLikePath = target.Contains('\\') || target.Contains('/') ||
                            Directory.Exists(target) || File.Exists(target);
        return looksLikePath
            ? await client.CreateSessionAsync(cwd: target).ConfigureAwait(false)
            : await client.CreateSessionAsync(workspaceId: target).ConfigureAwait(false);
    }

    private static DshImageAttachment LoadImageAttachment(string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"image file not found: {path}");
        }

        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            var extension => throw new ArgumentException(
                $"unsupported image extension '{extension}' (expected .png/.jpg/.jpeg/.webp/.gif)")
        };
        return new DshImageAttachment(File.ReadAllBytes(path), mediaType, Path.GetFileName(path));
    }

    private static async Task PrintImagesAsync(DshClient client, string sessionId)
    {
        var page = await client.GetHistoryAsync(sessionId, maxMessages: 40).ConfigureAwait(false);
        var count = 0;
        foreach (var record in page.Records)
        {
            foreach (var image in DshContent.ImagesOf(record))
            {
                count++;
                Console.WriteLine($"  seq={record.Seq} type={record.Type} attachment={image.AttachmentId} "
                                  + $"mediaType={image.MediaType} dimensions={image.Width}x{image.Height} "
                                  + $"bytes={image.Bytes} name={image.Name ?? "-"}");
            }
        }

        Console.WriteLine($"image refs: {count} (records scanned: {page.Records.Count})");
    }

    private static async Task<DshImageData?> FetchFirstImageAsync(DshClient client, string sessionId)
    {
        var page = await client.GetHistoryAsync(sessionId, maxMessages: 80).ConfigureAwait(false);
        for (var index = page.Records.Count - 1; index >= 0; index--)
        {
            var record = page.Records[index];
            var image = DshContent.ImagesOf(record).FirstOrDefault();
            if (image is null)
            {
                continue;
            }

            Console.WriteLine($"  found image on seq={record.Seq} type={record.Type}");
            return await client.GetImageAsync(sessionId, image).ConfigureAwait(false);
        }

        return null;
    }
    private static DshPairingInfo ResolvePairing(Options options, out int? loopbackPort)
    {
        loopbackPort = null;

        if (options.DevUrl is { Length: > 0 } devUrl)
        {
            if (!DshPairingInfo.TryParse(devUrl, out var fromUrl) || fromUrl is null)
            {
                throw new ArgumentException($"cannot parse --dev URL '{devUrl}' (expected http(s)://host:port/?token=...)");
            }

            return fromUrl;
        }

        if (options.PairingUri is { Length: > 0 } uriText)
        {
            if (!DshPairingInfo.TryParse(uriText, out var fromUri) || fromUri is null)
            {
                throw new ArgumentException($"cannot parse --uri pairing URI '{uriText}'");
            }

            return fromUri;
        }

        var path = options.PairingPath ?? DshPairingFile.DefaultPath;
        if (!DshPairingFile.TryLoad(path, out var pairing, out var error) || pairing is null)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ConnectFailed,
                $"{error} — pass --pairing <path>, --uri <dshremote://…> or --dev <http://127.0.0.1:port/?token=…>");
        }

        try
        {
            var json = File.ReadAllText(path);
            loopbackPort = DshPairingFile.ReadOptionalInt(json, "loopbackPort");
        }
        catch (IOException)
        {
            loopbackPort = null;
        }

        return pairing;
    }

    private static Options ParseArgs(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--pairing":
                    options.PairingPath = NextArg(args, ref i, arg);
                    break;
                case "--uri":
                    options.PairingUri = NextArg(args, ref i, arg);
                    break;
                case "--dev":
                    options.DevUrl = NextArg(args, ref i, arg);
                    break;
                case "--loopback":
                    options.Loopback = true;
                    break;
                case "--session":
                    options.SessionId = NextArg(args, ref i, arg);
                    break;
                case "--watch":
                    options.WatchSessionId = NextArg(args, ref i, arg);
                    break;
                case "--follow":
                    options.FollowSessionId = NextArg(args, ref i, arg);
                    break;
                case "--prompt":
                    options.Prompt = NextArg(args, ref i, arg);
                    break;
                case "--events":
                    options.Events = true;
                    break;
                case "--approve":
                    options.ApproveRequestId = NextArg(args, ref i, arg);
                    break;
                case "--approve-any":
                    options.ApproveAny = true;
                    break;
                case "--answer-any":
                    options.AnswerAnySelections = NextArg(args, ref i, arg);
                    break;
                case "--outcome":
                    options.Outcome = NextArg(args, ref i, arg);
                    break;
                case "--answer":
                    options.AnswerRequestId = NextArg(args, ref i, arg);
                    options.AnswersJson = NextArg(args, ref i, arg);
                    break;
                case "--steer":
                    options.Steer = true;
                    break;
                case "--watch-limit":
                    options.WatchLimit = ParseInt(NextArg(args, ref i, arg), arg);
                    break;
                case "--watch-timeout":
                    options.WatchTimeoutSeconds = ParseInt(NextArg(args, ref i, arg), arg);
                    break;
                case "--sessions":
                    options.SessionsToShow = ParseInt(NextArg(args, ref i, arg), arg);
                    break;
                case "--history":
                    options.HistoryEvents = ParseInt(NextArg(args, ref i, arg), arg);
                    break;
                case "--workspaces":
                    options.Workspaces = true;
                    break;
                case "--create-session":
                    options.CreateSessionTarget = NextArg(args, ref i, arg);
                    break;
                case "--archive":
                    options.ArchiveSessionId = NextArg(args, ref i, arg);
                    break;
                case "--unarchive":
                    options.Unarchive = true;
                    break;
                case "--image":
                    options.ImagePaths.Add(NextArg(args, ref i, arg));
                    break;
                case "--images":
                    options.ImagesSessionId = NextArg(args, ref i, arg);
                    break;
                case "--get-image":
                    options.GetImageSessionId = NextArg(args, ref i, arg);
                    break;
                case "--page-test":
                    options.PageTestSessionId = NextArg(args, ref i, arg);
                    break;
                case "--page-size":
                    options.PageSize = ParseInt(NextArg(args, ref i, arg), arg);
                    break;
                case "--max-pages":
                    options.MaxPages = ParseInt(NextArg(args, ref i, arg), arg);
                    break;
                case "--perf-open":
                    options.PerfOpenSessionId = NextArg(args, ref i, arg);
                    break;
                case "--verbose":
                case "-v":
                    options.Verbose = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{arg}'");
            }
        }

        return options;
    }

    private static string NextArg(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} requires a value");
        }

        return args[++index];
    }

    private static int ParseInt(string text, string name) =>
        int.TryParse(text, out var value) && value >= 0 ? value : throw new ArgumentException($"{name} requires a non-negative integer");

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
        usage: DshRemote.Smoke [options]

          --pairing <path>     pairing.json path (default: %USERPROFILE%\.dsh\remote\pairing.json)
          --uri <pairingUri>   dshremote://pair?host=…&port=…&token=…&fp=…
          --dev <url>          loopback dev instance without TLS: http://127.0.0.1:18090/?token=…
          --loopback           connect to 127.0.0.1:<pairing.loopbackPort> using the pairing token/fp
          --session <id>       session to read history/state for (default: first non-blank)
          --prompt <text>      send one session/prompt to the chosen session
          --steer              send the prompt as mode=steer instead of queue
          --watch <id>         also subscribe to session/observeEvent for <id>
          --follow <id>        open session/follow for <id>: print snapshot, then live events (gap-repaired)
          --watch-limit <n>    stop after n watched events (default 20)
          --watch-timeout <s>  stop watching after s seconds (default 60, 0 = forever)
          --events             subscribe $events and print approval/question waterfall requests
          --approve <id>       answer that approval when it arrives (see --outcome)
          --approve-any        auto-answer every approval in this run (E2E only) with --outcome
          --outcome <value>    allowed-once|rejected|cancelled (default allowed-once)
          --answer <id> <json> answer a question batch: [{"id","selected":[…],"custom"?}] or {"cancelled":true}
          --answer-any <csv>   auto-answer every question with these selected labels (E2E only)
          --workspaces         print workspace/follow baseline (workspaces + archived + pinned)
          --create-session <t> create a session (t = workspaceId, or a cwd path containing / or \\)
          --archive <id>       archive a session; add --unarchive to round-trip it
          --image <path>       attach a local image (.png/.jpg/.jpeg/.webp/.gif) to --prompt
          --images <id>        print image attachment references found in a session history
          --get-image <id>     fetch the newest image attachment bytes via session/attachment
          --page-test <id>     walk latest→older pages: north-star timing, idempotency, duplicates/gaps
          --perf-open <id>     compare old follow-snapshot first screen vs OpenSessionLite/latest-page
          --page-size <n>      history page walk size (default 100)
          --max-pages <n>      stop the walk after n pages (default 60; reports remainingHasMoreOlder)
          --sessions <n>       rows to print (default 5)
          --history <n>        history events to print (default 3)
          --json               dump parsed session summaries
          -v, --verbose        print client diagnostics to stderr
        """);
    }

    private static string SchemeOf(DshPairingInfo pairing) =>
        pairing.SpkiSha256 is { Length: 64 } ? "https"
            : pairing.Host is "127.0.0.1" or "localhost" or "::1" ? "http"
            : "https";

    private static string Mask(string token) =>
        token.Length <= 8 ? "***" : token[..4] + "…" + token[^4..];

    private static string OneLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
