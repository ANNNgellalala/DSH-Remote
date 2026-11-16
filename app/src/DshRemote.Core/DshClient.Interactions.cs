using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace DshRemote.Core;

/// <summary>
/// M2: owner-only approvals and structured user questions over the forwarded <c>$events</c> waterfall.
/// </summary>
/// <remarks>
/// One <c>$events</c> logical stream per connection generation. The opening <c>ready</c> frame carries the
/// <c>clientId</c> every answer must repeat; the host replays still-pending waterfall requests to each newly
/// opened answering stream, so reconnects are repaired by request-id de-duplication. Requests settled
/// elsewhere arrive as a <c>cancel</c> frame and are dropped from the surfaced stream.
/// </remarks>
public sealed partial class DshClient
{
    private readonly object _interactionGate = new();
    private readonly Channel<DshApprovalRequest> _approvalChannel =
        Channel.CreateUnbounded<DshApprovalRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly Channel<DshQuestionRequest> _questionChannel =
        Channel.CreateUnbounded<DshQuestionRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly Dictionary<string, PendingInteraction> _pendingInteractions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenInteractionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _answeredInteractionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelledInteractionIds = new(StringComparer.Ordinal);

    private string? _eventsClientId;
    private Task? _interactionPump;
    private CancellationTokenSource? _interactionCts;
    private bool _interactionEverStarted;

    private sealed class PendingInteraction
    {
        public required string EventId { get; init; }

        public required string Kind { get; init; }

        public string ClientId { get; set; } = string.Empty;

        public DshApprovalRequest? Approval { get; init; }

        public DshQuestionRequest? Question { get; init; }

        public bool IsApproval => Kind == "approval";
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DshApprovalRequest> ApprovalsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInteractionPump();
        await foreach (var request in _approvalChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsPending(request.RequestId))
            {
                yield return request;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DshQuestionRequest> QuestionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureInteractionPump();
        await foreach (var request in _questionChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsPending(request.RequestId))
            {
                yield return request;
            }
        }
    }

    /// <inheritdoc />
    public async Task RespondApprovalAsync(
        string requestId,
        DshApprovalOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId is required", nameof(requestId));
        }

        PendingInteraction? pending;
        string clientId;
        lock (_interactionGate)
        {
            if (_answeredInteractionIds.Contains(requestId) || _cancelledInteractionIds.Contains(requestId))
            {
                return;
            }

            if (!_pendingInteractions.TryGetValue(requestId, out pending) || !pending.IsApproval)
            {
                _options.Log?.Invoke($"RespondApprovalAsync: request '{requestId}' is not pending; ignored");
                return;
            }

            clientId = pending.ClientId;
            _answeredInteractionIds.Add(requestId);
            _pendingInteractions.Remove(requestId);
        }

        var value = DshArgs.ApprovalOutcomeValue(outcome switch
        {
            DshApprovalOutcome.AllowedOnce => "allowed-once",
            DshApprovalOutcome.Rejected => "rejected",
            DshApprovalOutcome.Cancelled => "cancelled",
            _ => "rejected"
        });
        try
        {
            await CallAsync(MuxWire.EventsResultMethod, DshArgs.EventsResult(clientId, requestId, value), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_interactionGate)
            {
                _answeredInteractionIds.Remove(requestId);
                if (pending is not null)
                {
                    _pendingInteractions[requestId] = pending;
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task RespondQuestionAsync(
        string requestId,
        IReadOnlyList<DshQuestionAnswerItem> answers,
        bool cancelled = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId is required", nameof(requestId));
        }

        PendingInteraction? pending;
        string clientId;
        lock (_interactionGate)
        {
            if (_answeredInteractionIds.Contains(requestId) || _cancelledInteractionIds.Contains(requestId))
            {
                return;
            }

            if (!_pendingInteractions.TryGetValue(requestId, out pending) || pending.IsApproval)
            {
                _options.Log?.Invoke($"RespondQuestionAsync: request '{requestId}' is not pending; ignored");
                return;
            }

            clientId = pending.ClientId;
            _answeredInteractionIds.Add(requestId);
            _pendingInteractions.Remove(requestId);
        }

        var value = cancelled
            ? DshArgs.QuestionCancelledValue()
            : DshArgs.QuestionAnswersValue(answers ?? Array.Empty<DshQuestionAnswerItem>());
        try
        {
            await CallAsync(MuxWire.EventsResultMethod, DshArgs.EventsResult(clientId, requestId, value), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_interactionGate)
            {
                _answeredInteractionIds.Remove(requestId);
                if (pending is not null)
                {
                    _pendingInteractions[requestId] = pending;
                }
            }

            throw;
        }
    }

    // ---- $events pump --------------------------------------------------------------------------------

    private bool IsPending(string requestId)
    {
        lock (_interactionGate)
        {
            return _pendingInteractions.ContainsKey(requestId) &&
                   !_answeredInteractionIds.Contains(requestId) &&
                   !_cancelledInteractionIds.Contains(requestId);
        }
    }

    private void EnsureInteractionPump()
    {
        lock (_interactionGate)
        {
            _interactionEverStarted = true;
            if (_interactionPump is { IsCompleted: false })
            {
                return;
            }

            _interactionCts?.Dispose();
            var cts = new CancellationTokenSource();
            _interactionCts = cts;
            _interactionPump = Task.Run(() => PumpInteractionsAsync(cts.Token), CancellationToken.None);
        }
    }

    /// <summary>Restarts the pump after a fresh connection when the UI had already subscribed.</summary>
    private void RestartInteractionPumpIfNeeded()
    {
        bool started;
        lock (_interactionGate)
        {
            started = _interactionEverStarted;
        }

        if (started)
        {
            EnsureInteractionPump();
        }
    }

    private async Task PumpInteractionsAsync(CancellationToken cancellationToken)
    {
        var lastGeneration = -1L;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                MuxConnection mux;
                (mux, lastGeneration) = await WaitForMuxAsync(lastGeneration, cancellationToken).ConfigureAwait(false);
                var iterator = mux.OpenAsync(MuxWire.EventsEndpoint, DshArgs.EventsStream(), cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
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
                        catch (Exception)
                        {
                            // Transport generation died; wait for the next one below.
                            break;
                        }

                        if (!moved)
                        {
                            break;
                        }

                        DispatchEventsValue(iterator.Current);
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

                try
                {
                    await WaitForMuxAsync(lastGeneration, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    await DelayAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"$events pump: {ex.Message}");
                try
                {
                    await DelayAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void DispatchEventsValue(JsonElement value)
    {
        switch (InteractionParsing.FrameType(value))
        {
            case "ready":
                if (InteractionParsing.TryParseReady(value, out var clientId, out var home))
                {
                    lock (_interactionGate)
                    {
                        _eventsClientId = clientId;
                    }

                    _options.Log?.Invoke($"$events ready: clientId={clientId} home={home}");
                }

                break;
            case "waterfall":
                DispatchWaterfall(value);
                break;
            case "cancel":
                if (InteractionParsing.TryParseCancel(value, out var cancelledId))
                {
                    lock (_interactionGate)
                    {
                        _pendingInteractions.Remove(cancelledId);
                        _cancelledInteractionIds.Add(cancelledId);
                    }
                }

                break;
            case "emit":
                // Process-wide notifications are not part of the M2 interaction contract; ignored.
                break;
            default:
                _options.Log?.Invoke($"$events: ignored frame type '{InteractionParsing.FrameType(value)}'");
                break;
        }
    }

    private void DispatchWaterfall(JsonElement value)
    {
        string eventName = DshParsing.GetString(value, "event") ?? string.Empty;
        if (eventName == InteractionParsing.ApprovalEvent)
        {
            if (InteractionParsing.TryParseApproval(value, out var approval))
            {
                AddPending(new PendingInteraction
                {
                    EventId = approval.RequestId,
                    Kind = "approval",
                    Approval = approval
                }, approval.RequestId);
            }
        }
        else if (eventName == InteractionParsing.QuestionEvent)
        {
            if (InteractionParsing.TryParseQuestion(value, out var question))
            {
                AddPending(new PendingInteraction
                {
                    EventId = question.RequestId,
                    Kind = "question",
                    Question = question
                }, question.RequestId);
            }
        }
        else
        {
            _options.Log?.Invoke($"$events: ignored waterfall event '{eventName}'");
        }
    }

    private void AddPending(PendingInteraction pending, string eventId)
    {
        ChannelWriter<DshApprovalRequest>? approvalsWriter = null;
        ChannelWriter<DshQuestionRequest>? questionsWriter = null;
        DshApprovalRequest? approval = null;
        DshQuestionRequest? question = null;
        lock (_interactionGate)
        {
            if (_cancelledInteractionIds.Contains(eventId) || _answeredInteractionIds.Contains(eventId))
            {
                return;
            }

            if (_seenInteractionIds.Contains(eventId))
            {
                // Host replay after reconnect: refresh the answering credential used by a later response.
                if (_pendingInteractions.TryGetValue(eventId, out var existing))
                {
                    existing.ClientId = _eventsClientId ?? existing.ClientId;
                }

                return;
            }

            _seenInteractionIds.Add(eventId);
            pending.ClientId = _eventsClientId ?? string.Empty;
            _pendingInteractions[eventId] = pending;
            if (pending.IsApproval)
            {
                approval = pending.Approval;
                approvalsWriter = _approvalChannel.Writer;
            }
            else
            {
                question = pending.Question;
                questionsWriter = _questionChannel.Writer;
            }
        }

        if (approval is not null)
        {
            approvalsWriter?.TryWrite(approval);
        }
        else if (question is not null)
        {
            questionsWriter?.TryWrite(question);
        }
    }

    /// <summary>Stops the <c>$events</c> pump (called on Disconnect); subscriptions stay alive for the next connect.</summary>
    private void CancelInteractions()
    {
        CancellationTokenSource? cts;
        lock (_interactionGate)
        {
            cts = _interactionCts;
            _interactionCts = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    /// <summary>Ends the application-level interaction streams on final dispose.</summary>
    private void CompleteInteractionStreams()
    {
        CancelInteractions();
        lock (_interactionGate)
        {
            _approvalChannel.Writer.TryComplete();
            _questionChannel.Writer.TryComplete();
            _interactionCts?.Dispose();
            _interactionCts = null;
        }
    }
}
