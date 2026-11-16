using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace DshRemote.Core;

/// <summary>
/// M1: one <c>session/follow</c> subscription per session with reconnect + sequence-gap repair.
/// </summary>
/// <remarks>
/// The <c>session/follow</c> request has no caller cursor: every (re)open starts with a fresh
/// <c>snapshot</c> frame. That snapshot is the repair baseline — events after the caller's last seen seq are
/// taken from it when contiguous, otherwise back-filled through backward <c>session/events</c> pages. When
/// even that cannot bridge the hole (history pruned), <see cref="SessionFollowResyncException"/> ends the
/// stream so the consumer can re-open a snapshot.
/// </remarks>
public sealed partial class DshClient
{
    private readonly ConcurrentDictionary<string, FollowBaseline> _followBaselines = new();
    private readonly ConcurrentDictionary<string, FollowSubscription> _followSubscriptions = new();

    private sealed record FollowBaseline(long LastSeq, long Cursor);

    private sealed class FollowSubscription
    {
        public FollowSubscription(string sessionId, long lastSeq, long cursor)
        {
            SessionId = sessionId;
            LastSeq = lastSeq;
            Cursor = cursor;
            Channel = System.Threading.Channels.Channel.CreateUnbounded<SessionEventRecord>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
        }

        public string SessionId { get; }

        public long LastSeq { get; set; }

        public long Cursor { get; set; }

        public Channel<SessionEventRecord> Channel { get; }

        public CancellationTokenSource? Cts { get; set; }

        public Task? Pump { get; set; }
    }

    /// <inheritdoc />
    public async Task<SessionSnapshot> OpenSessionAsync(
        string sessionId,
        int maxMessages = 80,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (mux, generation) = await WaitForMuxAsync(-1, cancellationToken).ConfigureAwait(false);
        var args = DshArgs.Follow(sessionId, maxMessages, assistantStream: true);
        FollowSnapshot? snapshot = null;
        var iterator = mux.OpenAsync(MuxWire.FollowEndpoint, args, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (snapshot is null)
            {
                if (!await iterator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                if (FollowParsing.TryParseSnapshot(iterator.Current, out var parsed))
                {
                    snapshot = parsed;
                    break;
                }

                // A snapshot always opens the stream; tolerate stray frames before it by ignoring them.
            }
        }
        finally
        {
            await iterator.DisposeAsync().ConfigureAwait(false);
        }

        if (snapshot is null)
        {
            throw new DshRemoteException(
                DshRemoteException.Codes.ResultInvalid,
                $"session/follow for '{sessionId}' ended before its opening snapshot (generation {generation})");
        }

        var lastSeq = snapshot.Records.Count > 0 ? Math.Max(snapshot.Records[^1].Seq, snapshot.Cursor) : -1;
        _followBaselines[sessionId] = new FollowBaseline(lastSeq, snapshot.Cursor);

        SessionRuntimeState? state = null;
        try
        {
            state = await GetStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (DshRemoteException ex) when (ex.Code.StartsWith("session/", StringComparison.Ordinal))
        {
            // The snapshot is still a useful baseline even when the passive state read is refused
            // (e.g. a subagent session); surface events without runtime state.
            _options.Log?.Invoke($"OpenSessionAsync: session/state for '{sessionId}' failed: {ex.Code} {ex.Message}");
        }

        stopwatch.Stop();
        DshDiagnostics.Instance.SessionOpen("follow", sessionId, snapshot.Records.Count, stopwatch.ElapsedMilliseconds);
        _options.Log?.Invoke(
            $"open follow: {stopwatch.ElapsedMilliseconds}ms session={sessionId} records={snapshot.Records.Count} "
            + $"cursor={snapshot.Cursor} hasMore={snapshot.HasMore}");
        return new SessionSnapshot(snapshot.Records, snapshot.Cursor, snapshot.HasMore, state, snapshot.ModelSelection);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionEventRecord> FollowEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        }

        // Create the linked token source before registering so a failure here cannot leak the slot.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FollowSubscription subscription;
        try
        {
            // A previous stream for this session may still be draining: the consumer has detached but its
            // pump still has to flush the logical-stream cancel frame before the slot is released. Wait for
            // that instead of throwing immediately — an immediate throw turns every consumer retry into an
            // instantaneous "already active" failure loop that can never recover.
            var acquireStopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                // Read the baseline inside the retry loop: the previous consumer persists its last seq when
                // it releases the slot, so the winning attempt sees the freshest cursor.
                _followBaselines.TryGetValue(sessionId, out var baseline);
                subscription = new FollowSubscription(sessionId, baseline?.LastSeq ?? -1, baseline?.Cursor ?? -1);
                if (_followSubscriptions.TryAdd(sessionId, subscription))
                {
                    break;
                }

                if (acquireStopwatch.Elapsed >= _options.FollowAcquireTimeout)
                {
                    throw new DshRemoteException(
                        DshRemoteException.Codes.FollowBusy,
                        $"a follow subscription for session '{sessionId}' is already active after waiting "
                        + $"{_options.FollowAcquireTimeout.TotalSeconds:0.#}s; one subscription per session is supported");
                }

                await Task.Delay(_options.FollowAcquirePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        subscription.Cts = cts;
        try
        {
            subscription.Pump = Task.Run(() => PumpFollowAsync(subscription, cts.Token), CancellationToken.None);
            await foreach (var record in subscription.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }
        finally
        {
            cts.Cancel();
            if (subscription.Pump is { } pump)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch
                {
                    // The pump reports failures through the channel.
                }
            }

            cts.Dispose();
            // Release the slot only when it is still ours: a newer consumer can take over while this pump
            // is draining, and its registration must survive this cleanup.
            if (_followSubscriptions.TryGetValue(sessionId, out var current) &&
                ReferenceEquals(current, subscription))
            {
                _followSubscriptions.TryRemove(sessionId, out _);
            }

            _followBaselines[sessionId] = new FollowBaseline(subscription.LastSeq, subscription.Cursor);
        }
    }

    /// <inheritdoc />
    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        SendPromptAsync(sessionId, text, steer: true, cancellationToken);

    /// <inheritdoc />
    public async Task UpdateQueueAsync(
        string sessionId,
        string itemId,
        QueueEditAction action,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var kind = action switch
        {
            QueueEditAction.Edit => "edit",
            QueueEditAction.Remove => "remove",
            QueueEditAction.Steer => "steer",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "unknown queue edit action")
        };
        var value = await CallAsync("session/updateQueue", DshArgs.UpdateQueue(sessionId, itemId, kind, text), cancellationToken)
            .ConfigureAwait(false);
        EnsureAccepted(value, "session/updateQueue");
    }

    // ---- follow pump ---------------------------------------------------------------------------------

    private async Task PumpFollowAsync(FollowSubscription subscription, CancellationToken cancellationToken)
    {
        var args = DshArgs.Follow(subscription.SessionId, _options.FollowMaxMessages, assistantStream: true);
        var lastGeneration = -1L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                MuxConnection mux;
                try
                {
                    (mux, lastGeneration) = await WaitForMuxAsync(lastGeneration, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    subscription.Channel.Writer.TryComplete(ex);
                    return;
                }

                var reopenImmediately = false;
                Exception? streamError = null;
                var iterator = mux.OpenAsync(MuxWire.FollowEndpoint, args, cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await iterator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (ex is DshRemoteException remote &&
                                !remote.IsTransportFailure &&
                                mux.Failure is null)
                            {
                                // A per-stream rejection on a live transport is terminal for this attempt
                                // (e.g. the server still holds the previous subscription). Surface it so
                                // the consumer's bounded retry can re-subscribe; waiting for a new
                                // connection generation here would wait forever. A fatal mux-level failure
                                // (mux.Failure set, e.g. bad JSON) is handled as a transport loss below.
                                streamError = remote;
                            }

                            // Transport generation died; the outer loop waits for the next one.
                            break;
                        }

                        if (!moved)
                        {
                            // The server ended the stream without an error: treat it like a lost
                            // subscription so the consumer re-subscribes (bounded by its retry budget).
                            streamError = new DshRemoteException(
                                DshRemoteException.Codes.Transport,
                                $"session/follow stream for '{subscription.SessionId}' ended");
                            break;
                        }

                        var frame = iterator.Current;
                        var frameType = FollowParsing.FrameType(frame);
                        if (frameType == "snapshot")
                        {
                            if (!FollowParsing.TryParseSnapshot(frame, out var snapshot))
                            {
                                continue;
                            }

                            IReadOnlyList<SessionEventRecord> repaired;
                            try
                            {
                                repaired = await RepairFromSnapshotAsync(subscription, snapshot, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (SessionFollowResyncException)
                            {
                                throw;
                            }
                            catch (DshRemoteException ex) when (ex.IsTransportFailure)
                            {
                                // The repair RPCs need the next generation; let the outer loop reconnect.
                                break;
                            }

                            if (!Emit(subscription, repaired))
                            {
                                return;
                            }

                            subscription.Cursor = Math.Max(subscription.Cursor, snapshot.Cursor);
                        }
                        else if (frameType == "event")
                        {
                            if (!FollowParsing.TryParseEventFrame(frame, out var record))
                            {
                                continue;
                            }

                            if (record.Seq <= subscription.LastSeq)
                            {
                                continue;
                            }

                            if (subscription.LastSeq >= 0 && record.Seq > subscription.LastSeq + 1)
                            {
                                // A durable frame skipped a seq: repair from the log, then reopen the stream.
                                try
                                {
                                    var state = await GetStateAsync(subscription.SessionId, cancellationToken).ConfigureAwait(false);
                                    var fetched = await FetchEventsAfterAsync(
                                        subscription.SessionId,
                                        subscription.LastSeq,
                                        state.Cursor,
                                        cancellationToken).ConfigureAwait(false);
                                    if (!FollowParsing.IsContiguous(fetched, subscription.LastSeq) ||
                                        fetched.Count == 0 ||
                                        fetched[^1].Seq < state.Cursor)
                                    {
                                        throw new SessionFollowResyncException(subscription.SessionId, subscription.LastSeq, state.Cursor);
                                    }

                                    if (!Emit(subscription, fetched))
                                    {
                                        return;
                                    }

                                    subscription.Cursor = Math.Max(subscription.Cursor, state.Cursor);
                                }
                                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                {
                                    return;
                                }
                                catch (SessionFollowResyncException)
                                {
                                    throw;
                                }
                                catch (DshRemoteException ex) when (ex.IsTransportFailure)
                                {
                                    reopenImmediately = true;
                                    break;
                                }

                                continue;
                            }

                            if (!Emit(subscription, new[] { record }))
                            {
                                return;
                            }
                        }
                        // `assistant-stream` frames are cursorless process-local deltas: not durable events.
                    }
                }
                finally
                {
                    await iterator.DisposeAsync().ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (streamError is not null)
                {
                    subscription.Channel.Writer.TryComplete(streamError);
                    return;
                }

                if (reopenImmediately)
                {
                    // Same generation is fine for a fresh snapshot; WaitForMuxAsync(-1) returns it at once.
                    lastGeneration = -1;
                    continue;
                }

                // Wait for a new connection generation before re-opening the follow stream.
                try
                {
                    await WaitForMuxAsync(lastGeneration, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    subscription.Channel.Writer.TryComplete(ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            subscription.Channel.Writer.TryComplete(ex);
            return;
        }

        subscription.Channel.Writer.TryComplete();
    }

    /// <summary>Emits events in seq order, skipping duplicates already delivered by this subscription.</summary>
    private static bool Emit(FollowSubscription subscription, IEnumerable<SessionEventRecord> records)
    {
        foreach (var record in records)
        {
            if (record.Seq <= subscription.LastSeq)
            {
                continue;
            }

            subscription.LastSeq = record.Seq;
            if (!subscription.Channel.Writer.TryWrite(record))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes the events missing between the consumer's last seq and the new snapshot cut: snapshot records
    /// when contiguous, otherwise backward <c>session/events</c> back-fill. Throws resync when the hole is
    /// larger than the durable log can still serve.
    /// </summary>
    private async Task<IReadOnlyList<SessionEventRecord>> RepairFromSnapshotAsync(
        FollowSubscription subscription,
        FollowSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var lastSeq = subscription.LastSeq;
        if (lastSeq < 0)
        {
            return snapshot.Records;
        }

        if (snapshot.Cursor <= lastSeq)
        {
            return Array.Empty<SessionEventRecord>();
        }

        var fromSnapshot = snapshot.Records.Where(record => record.Seq > lastSeq).ToList();
        if (fromSnapshot.Count > 0 &&
            FollowParsing.IsContiguous(fromSnapshot, lastSeq) &&
            fromSnapshot[^1].Seq >= snapshot.Cursor)
        {
            return fromSnapshot;
        }

        var fetched = await FetchEventsAfterAsync(subscription.SessionId, lastSeq, snapshot.Cursor, cancellationToken)
            .ConfigureAwait(false);
        var merged = FollowParsing.MergeBySeq(fetched, fromSnapshot);
        if (merged.Count > 0 &&
            FollowParsing.IsContiguous(merged, lastSeq) &&
            merged[^1].Seq >= snapshot.Cursor)
        {
            return merged;
        }

        throw new SessionFollowResyncException(
            subscription.SessionId,
            lastSeq,
            merged.Count > 0 ? merged[^1].Seq : snapshot.Cursor);
    }

    /// <summary>
    /// Reads every durable event after <paramref name="afterSeq"/> up to <paramref name="throughSeq"/> by
    /// paging <c>session/events</c> backwards and de-duplicating by seq.
    /// </summary>
    internal async Task<IReadOnlyList<SessionEventRecord>> FetchEventsAfterAsync(
        string sessionId,
        long afterSeq,
        long throughSeq,
        CancellationToken cancellationToken)
    {
        if (throughSeq <= afterSeq)
        {
            return Array.Empty<SessionEventRecord>();
        }

        var collected = new List<SessionEventRecord>();
        var before = throughSeq + 1;
        for (var pageIndex = 0; pageIndex < 64; pageIndex++)
        {
            var page = await GetHistoryAsync(sessionId, beforeSeq: before, maxMessages: 100, cancellationToken)
                .ConfigureAwait(false);
            if (page.Records.Count == 0)
            {
                break;
            }

            collected.AddRange(page.Records);
            var minimum = page.Records.Min(record => record.Seq);
            if (minimum <= afterSeq + 1 || !page.HasMore)
            {
                break;
            }

            before = minimum;
        }

        return FollowParsing.MergeBySeq(
            Array.Empty<SessionEventRecord>(),
            collected.Where(record => record.Seq > afterSeq));
    }

    /// <summary>Cancels every active follow subscription (called on Disconnect/Dispose).</summary>
    private void CancelFollowSubscriptions()
    {
        foreach (var subscription in _followSubscriptions.Values)
        {
            try
            {
                subscription.Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }
    }

    private static void EnsureAccepted(JsonElement value, string endpoint)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("accepted", out var accepted) &&
            accepted.ValueKind == JsonValueKind.False)
        {
            throw new DshRemoteException(DshRemoteException.Codes.ResultInvalid, $"{endpoint} was not accepted");
        }
    }
}
