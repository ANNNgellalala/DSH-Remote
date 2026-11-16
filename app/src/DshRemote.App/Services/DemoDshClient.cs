using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DshRemote.Core;

namespace DshRemote.App.Services;

/// <summary>
/// TODO(integration): temporary in-app fake used until <c>DshRemote.Core.DshClient</c> is linked.
/// It speaks the same contract and emits events with the exact shape of real DSH session logs
/// (<c>user/message</c>, <c>assistant/message</c>, <c>tool/call</c>, <c>tool/result</c>, <c>turn/*</c>,
/// <c>step/*</c>) so the UI can be built, screenshotted and regression-tested without a live relay.
/// Delete it once the real client is wired (or keep it behind the "demo" pairing host).
/// </summary>
public sealed class DemoDshClient : IDshClient, ISessionFollowClient, IInteractionClient, IWorkspaceClient, IImageClient, IHistoryPagingClient, IModelClient
{
    private readonly object _gate = new();
    private readonly List<SessionSummary> _sessions = new();
    private readonly Dictionary<string, List<SessionEventRecord>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _seq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _turn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Channel<SessionEventRecord>> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<QueuedPrompt>> _queues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _demoLiveScheduled = new(StringComparer.Ordinal);
    private readonly Channel<DshApprovalRequest> _approvalStream = Channel.CreateUnbounded<DshApprovalRequest>();
    private readonly Channel<DshQuestionRequest> _questionStream = Channel.CreateUnbounded<DshQuestionRequest>();
    private DshConnectionState _state = DshConnectionState.Disconnected;
    private bool _disposed;

    public DemoDshClient()
    {
        Seed();
    }

    public DshConnectionState State => _state;

    public DshPairingInfo? Pairing { get; private set; }

    public event EventHandler<DshConnectionState>? StateChanged;

    public async Task ConnectAsync(DshPairingInfo pairing, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SetState(DshConnectionState.Connecting);
        try
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(DshConnectionState.Disconnected);
            throw;
        }

        Pairing = pairing;
        lock (_gate)
        {
            _streams.Clear();
            _demoLiveScheduled.Clear();
        }

        SetState(DshConnectionState.Connected);
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            foreach (var request in _runs.Values)
            {
                request.Cancel();
            }

            _runs.Clear();
            foreach (var channel in _streams.Values)
            {
                channel.Writer.TryComplete();
            }

            _streams.Clear();
            _demoLiveScheduled.Clear();
        }

        Pairing = null;
        SetState(DshConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<SessionSummary> snapshot = _sessions
                .OrderByDescending(session => session.UpdatedAt)
                .ToList();
            return Task.FromResult(snapshot);
        }
    }

    public Task<SessionHistoryPage> GetHistoryAsync(
        string sessionId,
        long? beforeSeq = null,
        int maxMessages = 80,
        CancellationToken cancellationToken = default)
    {
        maxMessages = maxMessages <= 0 ? 80 : maxMessages;
        lock (_gate)
        {
            var all = _history.TryGetValue(sessionId, out var records)
                ? records
                : new List<SessionEventRecord>();
            var filtered = (beforeSeq is null ? all : all.Where(record => record.Seq < beforeSeq.Value)).ToList();
            var page = filtered.TakeLast(maxMessages).ToList();
            var cursor = page.Count > 0 ? page[^1].Seq : beforeSeq ?? 0;
            return Task.FromResult(new SessionHistoryPage(page, cursor, filtered.Count > page.Count));
        }
    }

    public Task<SessionRuntimeState> GetStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var cursor = _seq.TryGetValue(sessionId, out var seq) ? seq : 0;
            var running = _running.TryGetValue(sessionId, out var value) && value;
            var queue = _queues.TryGetValue(sessionId, out var items)
                ? (IReadOnlyList<QueuedPrompt>)items.ToList()
                : Array.Empty<QueuedPrompt>();
            var state = new SessionRuntimeState(cursor, Active: true, Running: running, queue, Array.Empty<string>());
            return Task.FromResult(state);
        }
    }

    // ---- M6: IHistoryPagingClient (lazy history paging) -----------------------------------------

    public Task<SessionHistoryPage> GetLatestHistoryAsync(
        string sessionId,
        int maxMessages = 30,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            maxMessages = maxMessages <= 0 ? 30 : maxMessages;
            _history.TryGetValue(sessionId, out var all);
            all ??= new List<SessionEventRecord>();
            var ordered = all.OrderBy(record => record.Seq).ToList();
            var pageRecords = ordered.Skip(Math.Max(0, ordered.Count - maxMessages)).ToList();
            var hasOlder = pageRecords.Count > 0 && pageRecords[0].Seq > ordered[0].Seq;
            var cursor = _seq.TryGetValue(sessionId, out var seq) ? seq : 0;
            return Task.FromResult(new SessionHistoryPage(
                pageRecords,
                cursor,
                HasMore: hasOlder,
                HasMoreOlder: hasOlder,
                BeforeSeq: hasOlder ? pageRecords[0].Seq : null));
        }
    }

    public Task<SessionHistoryPage> GetOlderHistoryAsync(
        string sessionId,
        long beforeSeq,
        int maxMessages = 30,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            maxMessages = maxMessages <= 0 ? 30 : maxMessages;
            _history.TryGetValue(sessionId, out var all);
            all ??= new List<SessionEventRecord>();
            var older = all
                .Where(record => record.Seq > 0 && record.Seq < beforeSeq)
                .OrderBy(record => record.Seq)
                .ToList();
            var pageRecords = older.Skip(Math.Max(0, older.Count - maxMessages)).ToList();
            var hasOlder = pageRecords.Count > 0 && pageRecords[0].Seq > older[0].Seq;
            var cursor = _seq.TryGetValue(sessionId, out var seq) ? seq : 0;
            return Task.FromResult(new SessionHistoryPage(
                pageRecords,
                cursor,
                HasMore: hasOlder,
                HasMoreOlder: hasOlder,
                BeforeSeq: hasOlder ? pageRecords[0].Seq : null));
        }
    }

    public async Task<SessionLiteSnapshot> OpenSessionLiteAsync(
        string sessionId,
        int maxMessages = 30,
        CancellationToken cancellationToken = default)
    {
        var page = await GetLatestHistoryAsync(sessionId, maxMessages, cancellationToken).ConfigureAwait(false);
        var state = await GetStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionLiteSnapshot(page, state);
    }

    public Task<string> SendPromptAsync(
        string sessionId,
        string text,
        bool steer = false,
        CancellationToken cancellationToken = default)
        => SendPromptAsync(sessionId, text, images: null, steer, cancellationToken);

    public Task<string> SendPromptAsync(
        string sessionId,
        string text,
        IReadOnlyList<DshImageAttachment>? images,
        bool steer = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text) && (images is null || images.Count == 0))
        {
            throw new ArgumentException("消息不能为空。", nameof(text));
        }

        var requestId = "req-" + Guid.NewGuid().ToString("N")[..10];
        var imageRefs = new List<DemoImage>();
        if (images is { Count: > 0 })
        {
            lock (_gate)
            {
                var index = 0;
                foreach (var image in images)
                {
                    var id = $"att-upload-{Guid.NewGuid().ToString("N")[..8]}-{index++}";
                    _uploadedImages[id] = image.Data;
                    _uploadedImageMedia[id] = image.MediaType;
                    imageRefs.Add(new DemoImage(id, image.MediaType, image.Data.Length, image.Name));
                }
            }
        }

        StartDemoRun(sessionId, (text ?? string.Empty).Trim(), steer, requestId, imageRefs);
        return Task.FromResult(requestId);
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(sessionId, out var request))
            {
                request.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SessionEventRecord> ObserveEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = GetOrCreateStream(sessionId);
        ScheduleDemoLiveEvents(sessionId);

        while (!cancellationToken.IsCancellationRequested)
        {
            SessionEventRecord? record = null;
            try
            {
                if (!await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                channel.Reader.TryRead(out record);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (record is not null)
            {
                yield return record;
            }
        }
    }

    // ---- ISessionFollowClient (demo: snapshot then the same live stream) ------------------------

    public Task<SessionSnapshot> OpenSessionAsync(
        string sessionId,
        int maxMessages = 80,
        CancellationToken cancellationToken = default)
    {
        var page = GetHistoryAsync(sessionId, null, maxMessages, cancellationToken).GetAwaiter().GetResult();
        var state = GetStateAsync(sessionId, cancellationToken).GetAwaiter().GetResult();
        return Task.FromResult(new SessionSnapshot(page.Records, page.Cursor, page.HasMore, state));
    }

    public IAsyncEnumerable<SessionEventRecord> FollowEventsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => ObserveEventsAsync(sessionId, cancellationToken);

    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        StartDemoRun(sessionId, text, steer: true, requestId: null);
        return Task.CompletedTask;
    }

    public Task UpdateQueueAsync(
        string sessionId,
        string itemId,
        QueueEditAction action,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(sessionId, out var queue))
            {
                return Task.CompletedTask;
            }

            var index = queue.FindIndex(item => item.Id == itemId);
            switch (action)
            {
                case QueueEditAction.Edit when index >= 0:
                    queue[index] = new QueuedPrompt(itemId, queue[index].Placement, text ?? queue[index].Text);
                    break;
                case QueueEditAction.Remove when index >= 0:
                    queue.RemoveAt(index);
                    break;
                case QueueEditAction.Steer when index >= 0:
                    var steered = queue[index];
                    queue.RemoveAt(index);
                    StartDemoRun(sessionId, steered.Text ?? "(队列条目)", steer: true, requestId: null);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    // ---- IInteractionClient (demo: scripted requests) -----------------------------------------

    public async IAsyncEnumerable<DshApprovalRequest> ApprovalsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DshApprovalRequest? request = null;
            try
            {
                if (!await _approvalStream.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                _approvalStream.Reader.TryRead(out request);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (request is not null)
            {
                yield return request;
            }
        }
    }

    public async IAsyncEnumerable<DshQuestionRequest> QuestionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DshQuestionRequest? request = null;
            try
            {
                if (!await _questionStream.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                _questionStream.Reader.TryRead(out request);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (request is not null)
            {
                yield return request;
            }
        }
    }

    public Task RespondApprovalAsync(
        string requestId,
        DshApprovalOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var text = outcome switch
        {
            DshApprovalOutcome.AllowedOnce => "审批已允许（演示）：继续执行工具调用。",
            DshApprovalOutcome.Rejected => "审批已拒绝（演示）：本轮将不再执行该工具。",
            _ => "审批请求已取消。"
        };
        var sessionId = _pendingApprovalSessions.GetValueOrDefault(requestId) ?? "sess-demo-a1b2c3";
        _pendingApprovalSessions.Remove(requestId);
        Emit(sessionId, "assistant/message", AssistantMessageJson(0, 0, text, null));
        return Task.CompletedTask;
    }

    public Task RespondQuestionAsync(
        string requestId,
        IReadOnlyList<DshQuestionAnswerItem> answers,
        bool cancelled = false,
        CancellationToken cancellationToken = default)
    {
        var sessionId = _pendingQuestionSessions.GetValueOrDefault(requestId) ?? "sess-demo-a1b2c3";
        _pendingQuestionSessions.Remove(requestId);
        var summary = cancelled
            ? "问题已关闭（演示）。"
            : "已收到回答（演示）：" + string.Join("；", answers.Select(answer =>
                $"{answer.Id}={(answer.Selected.Count > 0 ? string.Join("/", answer.Selected) : answer.Custom ?? "（空）")}"));
        Emit(sessionId, "assistant/message", AssistantMessageJson(0, 0, summary, null));
        return Task.CompletedTask;
    }

    private readonly Dictionary<string, string> _pendingApprovalSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingQuestionSessions = new(StringComparer.Ordinal);

    // ---- M5: workspaces / images -------------------------------------------------------------
    private readonly List<string> _archivedSessionIds = new();
    private readonly List<DshWorkspace> _workspaces = new();
    private readonly Dictionary<string, byte[]> _uploadedImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uploadedImageMedia = new(StringComparer.Ordinal);

    private sealed record DemoImage(string Id, string MediaType, int Bytes, string? Name);


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _approvalStream.Writer.TryComplete();
        _questionStream.Writer.TryComplete();
        await DisconnectAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------
    // Demo run simulation
    // ------------------------------------------------------------------------------------------------

    private void StartDemoRun(
        string sessionId,
        string text,
        bool steer,
        string? requestId,
        IReadOnlyList<DemoImage>? images = null)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_runs.TryGetValue(sessionId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            cts = new CancellationTokenSource();
            _runs[sessionId] = cts;
        }

        _ = Task.Run(async () =>
        {
            var turn = NextTurn(sessionId);
            const int step = 1;
            try
            {
                SetRunning(sessionId, true);
                await Task.Delay(120, cts.Token).ConfigureAwait(false);
                Emit(sessionId, "turn/start", $"{{\"turn\":{turn}}}");
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                Emit(
                    sessionId,
                    "user/message",
                    images is { Count: > 0 }
                        ? UserMessageJsonWithImages(text, requestId, images)
                        : UserMessageJson(text, requestId));
                await Task.Delay(280, cts.Token).ConfigureAwait(false);
                Emit(sessionId, "step/start", StepJson(turn, step));
                await Task.Delay(500, cts.Token).ConfigureAwait(false);

                var reply = images is { Count: > 0 }
                    ? $"收到 {images.Count} 张图片（演示）：我已看过附件，继续处理你的请求。"
                    : steer
                        ? $"收到追加指令: {Shorten(text, 60)}。我把它插到当前步骤里继续。"
                        : $"收到: {Shorten(text, 60)}。我先看一下相关代码，然后把结论同步给你。";
                await EmitStreamingAssistantAsync(
                    sessionId,
                    turn,
                    step,
                    "先确认用户意图，再决定是否要调用工具。",
                    reply,
                    cts.Token).ConfigureAwait(false);
                await Task.Delay(500, cts.Token).ConfigureAwait(false);

                var callId = "call_demo_" + Guid.NewGuid().ToString("N")[..8];
                Emit(
                    sessionId,
                    "tool/call",
                    $"{{\"turn\":{turn},\"step\":{step},\"callId\":{Quote(callId)},\"name\":\"pwsh\",\"arguments\":{Quote("{\"command\":\"Get-ChildItem app/src -Directory | Select-Object Name\",\"description\":\"Inspect app projects\"}")}}}");
                await Task.Delay(800, cts.Token).ConfigureAwait(false);
                Emit(
                    sessionId,
                    "tool/result",
                    ToolResultJson(
                        turn,
                        step,
                        callId,
                        "Name\r\n----\r\nDshRemote.App\r\nDshRemote.App.Android\r\nDshRemote.App.Desktop\r\nDshRemote.Core"));
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
                await EmitStreamingAssistantAsync(
                    sessionId,
                    turn,
                    step,
                    null,
                    "我已经核对完了: 共享 UI 在 `app/src/DshRemote.App`，桌面壳与 Android 头共用同一份 XAML。没有发现问题，继续下一步。",
                    cts.Token).ConfigureAwait(false);
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                Emit(sessionId, "step/end", StepJson(turn, step));
                Emit(sessionId, "turn/end", $"{{\"turn\":{turn},\"reason\":{{\"kind\":\"completed\"}}}}");
                SetRunning(sessionId, false);

                await MaybeEmitInteractionAsync(sessionId, text, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Emit(sessionId, "turn/end", $"{{\"turn\":{turn},\"reason\":{{\"kind\":\"cancelled\"}}}}");
                SetRunning(sessionId, false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_runs.TryGetValue(sessionId, out var current) && ReferenceEquals(current, cts))
                    {
                        _runs.Remove(sessionId);
                    }
                }

                cts.Dispose();
            }
        });
    }

    /// <summary>Fake attempt stream: reasoning deltas, text deltas, then the durable final message.</summary>
    private async Task EmitStreamingAssistantAsync(
        string sessionId,
        int turn,
        int step,
        string? reasoning,
        string text,
        CancellationToken cancellationToken)
    {
        var attemptId = "att_" + Guid.NewGuid().ToString("N")[..8];
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            foreach (var part in SplitChunks(reasoning!, 16))
            {
                Emit(sessionId, "assistant/live-chunk", LiveChunkJson(attemptId, turn, step, "reasoning-delta", 0, part));
                await Task.Delay(35, cancellationToken).ConfigureAwait(false);
            }
        }

        var index = 0;
        foreach (var part in SplitChunks(text, 12))
        {
            Emit(sessionId, "assistant/live-chunk", LiveChunkJson(attemptId, turn, step, "text-delta", index++, part));
            await Task.Delay(45, cancellationToken).ConfigureAwait(false);
        }

        Emit(sessionId, "assistant/message", AssistantMessageJson(turn, step, text, reasoning));
    }

    private async Task MaybeEmitInteractionAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        await Task.Delay(600, cancellationToken).ConfigureAwait(false);

        if (text.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("审批", StringComparison.Ordinal))
        {
            var requestId = "appr_" + Guid.NewGuid().ToString("N")[..8];
            var request = new DshApprovalRequest(
                requestId,
                sessionId,
                "pwsh",
                "call_demo_approval",
                "The agent wants to run a command that would write outside the session workspace.",
                "Agent 想执行一条会修改工作区外文件的命令，需要你的批准。",
                "{\"command\":\"Remove-Item D:\\\\tmp\\\\old.log -Recurse\",\"description\":\"cleanup\"}");
            _pendingApprovalSessions[requestId] = sessionId;
            _approvalStream.Writer.TryWrite(request);
        }

        if (text.Contains("question", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ask", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("提问", StringComparison.Ordinal))
        {
            var requestId = "qs_" + Guid.NewGuid().ToString("N")[..8];
            IReadOnlyList<DshQuestionItem> questions = new[]
            {
                new DshQuestionItem(
                    "q1",
                    "接下来用哪个构建配置继续？",
                    new[] { "Debug（快，用于真机联调）", "Release（慢，需要单 ABI 裁剪）" },
                    Detail: "Android 构建")
            };
            _pendingQuestionSessions[requestId] = sessionId;
            _questionStream.Writer.TryWrite(new DshQuestionRequest(requestId, sessionId, questions));
        }
    }

    private static IEnumerable<string> SplitChunks(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            yield return text.Substring(offset, Math.Min(size, text.Length - offset));
        }
    }


    /// <summary>When a running demo session is opened, finish its in-flight turn after a beat.</summary>
    private void ScheduleDemoLiveEvents(string sessionId)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(sessionId, out var running) || !running ||
                !_demoLiveScheduled.Add(sessionId))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2600).ConfigureAwait(false);
                int turn;
                lock (_gate)
                {
                    // A real prompt run may have started while we waited; don't inject a bogus turn/end.
                    if (_runs.ContainsKey(sessionId) ||
                        !_running.TryGetValue(sessionId, out var stillRunning) || !stillRunning)
                    {
                        return;
                    }

                    turn = _turn.TryGetValue(sessionId, out var value) ? value : 1;
                }

                Emit(sessionId, "step/end", StepJson(turn, 1));
                Emit(sessionId, "turn/end", $"{{\"turn\":{turn},\"reason\":{{\"kind\":\"completed\"}}}}");
                SetRunning(sessionId, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DemoDshClient] live demo failed: {ex}");
            }
        });
    }

    private int NextTurn(string sessionId)
    {
        lock (_gate)
        {
            var turn = _turn.TryGetValue(sessionId, out var value) ? value + 1 : 1;
            _turn[sessionId] = turn;
            return turn;
        }
    }

    private void SetRunning(string sessionId, bool running)
    {
        lock (_gate)
        {
            _running[sessionId] = running;
            var index = _sessions.FindIndex(session => session.SessionId == sessionId);
            if (index >= 0)
            {
                _sessions[index] = _sessions[index] with
                {
                    Running = running,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
        }
    }

    private Channel<SessionEventRecord> GetOrCreateStream(string sessionId)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(sessionId, out var channel))
            {
                channel = Channel.CreateUnbounded<SessionEventRecord>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                _streams[sessionId] = channel;
            }

            return channel;
        }
    }

    private void Emit(string sessionId, string type, string dataJson)
    {
        SessionEventRecord record;
        lock (_gate)
        {
            var seq = _seq.TryGetValue(sessionId, out var current) ? current + 1 : 1;
            _seq[sessionId] = seq;
            var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var raw = $"{{\"type\":{Quote(type)},\"seq\":{seq},\"time\":{time},\"data\":{dataJson}}}";
            record = SessionEventRecord.FromRawJson(raw)
                     ?? throw new InvalidOperationException("demo session event envelope failed to parse");

            if (!_history.TryGetValue(sessionId, out var history))
            {
                history = new List<SessionEventRecord>();
                _history[sessionId] = history;
            }

            history.Add(record);
            if (history.Count > 600)
            {
                history.RemoveRange(0, history.Count - 600);
            }

            var index = _sessions.FindIndex(session => session.SessionId == sessionId);
            if (index >= 0)
            {
                _sessions[index] = _sessions[index] with { UpdatedAt = record.Time };
            }

            if (_streams.TryGetValue(sessionId, out var channel))
            {
                channel.Writer.TryWrite(record);
            }
        }
    }

    private void SetState(DshConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // ------------------------------------------------------------------------------------------------
    // Seed data (shapes copied from real DSH session logs)
    // ------------------------------------------------------------------------------------------------

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var minute = 60_000L;

        AddSession("sess-demo-a1b2c3", "DSH Remote 手机端联调", @"D:\Code\VibeCoding\DSHRemote", "deepseek-v4.1-flash", running: true, updatedAt: now - 45 * 1000, modelSelection: new DshModelSelection("deepseek-official", "deepseek-v4.1-flash", "high"));
        AddSession("sess-demo-d4e5f6", "Arc 主题在 Android 上的探针复现", @"D:\Code\VibeCoding\DSHRemote\_scratch\android-arc", "deepseek-v4.1-pro", running: false, updatedAt: now - 26 * minute, modelSelection: new DshModelSelection("deepseek-official", "deepseek-v4.1-pro", null));
        AddSession("sess-demo-7890ab", "账单 App 构建体积优化", @"D:\Code\VibeCoding\我的账单APP", "deepseek-v4.1-flash", running: false, updatedAt: now - 3 * 60 * minute, modelSelection: new DshModelSelection("deepseek-official", "deepseek-v4.1-flash", "high"));
        AddSession("sess-demo-0f1e2d", null, @"C:\Users\dev\scratch", null, running: false, updatedAt: now - 2 * 24 * 60 * minute, blank: true);
        AddSession(
            "sess-demo-sub01",
            "子代理 · 构建日志分析",
            @"D:\Code\VibeCoding\DSHRemote",
            "deepseek-v4.1-flash",
            running: true,
            updatedAt: now - 60 * 1000,
            parentSessionId: "sess-demo-a1b2c3",
            origin: "subagent",
            modelSelection: new DshModelSelection("deepseek-official", "deepseek-v4.1-flash", "high"));

        var t = now - 8 * minute;
        SeedEvent("sess-demo-a1b2c3", "turn/start", "{\"turn\":1}", ref t);
        SeedEvent("sess-demo-a1b2c3", "user/message", UserMessageJson("先确认一下 Android 构建脚本里的 SDK 路径是怎么传的。"), ref t);
        SeedEvent("sess-demo-a1b2c3", "step/start", StepJson(1, 1), ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "assistant/message",
            AssistantMessageJson(1, 1, "我先读一下 tools/android/build-apk.ps1，看看 SDK/JDK 路径是不是写死的。"),
            ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "tool/call",
            $"{{\"turn\":1,\"step\":1,\"callId\":\"call_seed_1\",\"name\":\"pwsh\",\"arguments\":{Quote("{\"command\":\"Get-Content tools/android/build-apk.ps1\"}")}}}",
            ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "tool/result",
            ToolResultJson(
                1,
                1,
                "call_seed_1",
                "param([string]$Configuration = 'Debug')\r\n$sdk = 'D:\\SmartBillAndroidSdk'\r\n$java = 'C:\\Program Files\\Java\\jdk-21'\r\ndotnet build app/src/DshRemote.App.Android/DshRemote.App.Android.csproj -c $Configuration `\r\n  -p:AndroidSdkDirectory=$sdk -p:JavaSdkDirectory=$java"),
            ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "tool/call",
            $"{{\"turn\":1,\"step\":1,\"callId\":\"call_seed_edit\",\"name\":\"edit\",\"arguments\":{Quote("{\"file_path\":\"app/src/DshRemote.App.Android/DshRemote.App.Android.csproj\",\"old_string\":\"Avalonia.Android 12.1.4\",\"new_string\":\"Avalonia.Android 12.1.5\"}")}}}",
            ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "tool/result",
            ToolResultJson(1, 1, "call_seed_edit", "已更新 1 处（csproj）。"),
            ref t);
        var demoPresentArgs = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["files"] = new object[]
            {
                new Dictionary<string, object?> { ["path"] = "dist/apk/dshremote-1.3.0-23-arm64-release.apk", ["description"] = "修复版 Release APK" },
                new Dictionary<string, object?> { ["path"] = "README.md", ["description"] = "v1.3.0 变更说明" }
            }
        });
        SeedEvent(
            "sess-demo-a1b2c3",
            "tool/call",
            $"{{\"turn\":1,\"step\":1,\"callId\":\"call_seed_present\",\"name\":\"present\",\"arguments\":{Quote(demoPresentArgs)}}}",
            ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "deliverables/presented",
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["turn"] = 1,
                ["callId"] = "call_seed_present",
                ["files"] = new object[]
                {
                    new Dictionary<string, object?> { ["path"] = @"D:\Code\VibeCoding\DSHRemote\dist\apk\dshremote-1.3.0-23-arm64-release.apk", ["description"] = "修复版 Release APK" },
                    new Dictionary<string, object?> { ["path"] = @"D:\Code\VibeCoding\DSHRemote\README.md", ["description"] = "v1.3.0 变更说明" }
                }
            }),
            ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "assistant/message",
            AssistantMessageJson(1, 1, "脚本已经显式传了 `AndroidSdkDirectory`，但默认值写死了。建议改成可覆盖参数，CI 换机时不用改脚本。"),
            ref t);
        SeedEvent("sess-demo-a1b2c3", "step/end", StepJson(1, 1), ref t);
        SeedEvent("sess-demo-a1b2c3", "turn/end", "{\"turn\":1,\"reason\":{\"kind\":\"completed\"}}", ref t);
        SeedEvent("sess-demo-a1b2c3", "turn/start", "{\"turn\":2}", ref t);
        SeedEvent("sess-demo-a1b2c3", "user/message", UserMessageJson("好，改吧；顺便确认一下配对 URI 的格式。"), ref t);
        SeedEvent("sess-demo-a1b2c3", "step/start", StepJson(2, 1), ref t);
        SeedEvent(
            "sess-demo-a1b2c3",
            "assistant/message",
            AssistantMessageJson(
                2,
                1,
                "配对 URI 形如 `dshremote://pair?v=1&host=192.0.2.20&port=8443&token=…&fp=…`，在 App 里整段粘贴即可；也可以手填 host / port / token。\n\n下载页：<https://203.0.113.10:9443/__gateway/download>\n最新 APK：https://203.0.113.10:9443/__gateway/download/dshremote-1.4.2-27-arm64-release.apk\n安装文档见 [install.md](https://example.com/docs/install.md)，链接可以直接点开复制或跳浏览器。"),
            ref t);

        var t2 = now - 40 * minute;
        SeedEvent("sess-demo-d4e5f6", "turn/start", "{\"turn\":1}", ref t2);
        SeedEvent("sess-demo-d4e5f6", "user/message", UserMessageJson("把 Arc 探针改成正式模板，先跑通真机。"), ref t2);
        SeedEvent("sess-demo-d4e5f6", "step/start", StepJson(1, 1), ref t2);
        SeedEvent(
            "sess-demo-d4e5f6",
            "assistant/message",
            AssistantMessageJson(1, 1, "可以，模板已经验证过: AppCompat 主题 + EmbedAssembliesIntoApk=true 是硬性条件。"),
            ref t2);
        SeedEvent("sess-demo-d4e5f6", "step/end", StepJson(1, 1), ref t2);
        SeedEvent("sess-demo-d4e5f6", "turn/end", "{\"turn\":1,\"reason\":{\"kind\":\"completed\"}}", ref t2);

        var t3 = now - 3 * 60 * minute;
        SeedEvent("sess-demo-7890ab", "user/message", UserMessageJson("Debug APK 78 MB，Release 能压到多少？"), ref t3);
        SeedEvent(
            "sess-demo-7890ab",
            "assistant/message",
            AssistantMessageJson(1, 1, "单 ABI + Release + 裁剪，通常能到 20 MB 级；这次先不动架构。"),
            ref t3);

        // The running demo session is mid-turn-2 (its history ends before turn/end).
        _turn["sess-demo-a1b2c3"] = 2;

        // One queued prompt so the M2 queue editor has something to work with.
        _queues["sess-demo-a1b2c3"].Add(new QueuedPrompt(
            "q-demo-1",
            "queue",
            "演示：确认 APK 输出路径，并把 build 日志最后 20 行贴出来。"));

        // M5 demo workspaces: two explicit projects, the rest falls into the UI's "其他" group.
        _workspaces.AddRange(new[]
        {
            new DshWorkspace(
                "ws-demo-dshremote",
                @"D:\Code\VibeCoding\DSHRemote",
                "DSH Remote",
                new[] { "sess-demo-a1b2c3", "sess-demo-sub01" },
                now - 30 * minute,
                now),
            new DshWorkspace(
                "ws-demo-arc",
                @"D:\Code\VibeCoding\Arc.Avalonia",
                "Arc.Avalonia",
                new[] { "sess-demo-d4e5f6" },
                now - 60 * minute,
                now - 26 * minute),
            new DshWorkspace(
                "ws-demo-bills",
                @"D:\Code\VibeCoding\我的账单APP",
                "我的账单 APP",
                new[] { "sess-demo-7890ab" },
                now - 3 * 60 * minute,
                now - 3 * 60 * minute)
        });

        // M5 demo images: a user prompt with an attached screenshot and a tool result that carries one.
        SeedEvent("sess-demo-d4e5f6", "turn/start", "{\"turn\":2}", ref t2);
        SeedEvent(
            "sess-demo-d4e5f6",
            "user/message",
            UserMessageJsonWithImages(
                "看一下这张探针截图，重点是 Arc 主题是否生效。",
                null,
                new[] { new DemoImage("att-demo-1", "image/png", DemoImages.VioletBytes().Length, "arc-probe.png") }),
            ref t2);
        SeedEvent(
            "sess-demo-d4e5f6",
            "assistant/message",
            AssistantMessageJson(2, 1, "截图已收到：暗色主题和 Violet 强调色都生效了。"),
            ref t2);
        SeedEvent(
            "sess-demo-d4e5f6",
            "tool/call",
            $"{{\"turn\":2,\"step\":1,\"callId\":\"call_img_1\",\"name\":\"read_image\",\"arguments\":{Quote("{\"path\":\"_scratch/arc-probe-device.png\"}")}}}",
            ref t2);
        SeedEvent(
            "sess-demo-d4e5f6",
            "tool/result",
            ToolResultJsonWithImage(
                2,
                1,
                "call_img_1",
                "已读取图片，尺寸 1272×2800。",
                new[] { new DemoImage("att-demo-2", "image/png", DemoImages.MintBytes().Length, "tool-shot.png") }),
            ref t2);
        SeedEvent("sess-demo-d4e5f6", "turn/end", "{\"turn\":2,\"reason\":{\"kind\":\"completed\"}}", ref t2);
    }

    private void AddSession(
        string id,
        string? title,
        string cwd,
        string? model,
        bool running,
        long updatedAt,
        bool blank = false,
        string? parentSessionId = null,
        string? origin = null,
        DshModelSelection? modelSelection = null)
    {
        _sessions.Add(new SessionSummary(
            SessionId: id,
            Title: title,
            Cwd: cwd,
            Model: model,
            Running: running,
            AgentAvailable: true,
            Blank: blank,
            UpdatedAt: updatedAt,
            LastPromptAt: updatedAt - 5_000,
            ParentSessionId: parentSessionId,
            Origin: origin,
            ModelSelection: modelSelection));
        _history[id] = new List<SessionEventRecord>();
        _queues[id] = new List<QueuedPrompt>();
        _seq[id] = 0;
        _running[id] = running;
        _turn[id] = 0;
    }

    private void SeedEvent(string sessionId, string type, string dataJson, ref long time)
    {
        time += 9_000;
        var seq = _seq[sessionId] + 1;
        _seq[sessionId] = seq;
        var raw = $"{{\"type\":{Quote(type)},\"seq\":{seq},\"time\":{time},\"data\":{dataJson}}}";
        _history[sessionId].Add(
            SessionEventRecord.FromRawJson(raw)
            ?? throw new InvalidOperationException("demo seed event envelope failed to parse"));
    }

    // ------------------------------------------------------------------------------------------------
    // M5: IWorkspaceClient / IImageClient demo
    // ------------------------------------------------------------------------------------------------

    public Task<DshWorkspaceState> GetWorkspaceStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var state = new DshWorkspaceState(
                _workspaces.ToList(),
                _archivedSessionIds.ToList(),
                Array.Empty<string>());
            return Task.FromResult(state);
        }
    }

    public Task<DshWorkspaceState> ArchiveSessionAsync(
        string sessionId,
        bool stopActivity = false,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_archivedSessionIds.Contains(sessionId))
            {
                _archivedSessionIds.Add(sessionId);
            }

            if (stopActivity && _runs.TryGetValue(sessionId, out var run))
            {
                run.Cancel();
            }

            var index = _sessions.FindIndex(session => session.SessionId == sessionId);
            if (index >= 0)
            {
                _sessions[index] = _sessions[index] with
                {
                    Running = false,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _running[sessionId] = false;
            }
        }

        return GetWorkspaceStateAsync(cancellationToken);
    }

    public Task<DshWorkspaceState> UnarchiveSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _archivedSessionIds.Remove(sessionId);
        }

        return GetWorkspaceStateAsync(cancellationToken);
    }

    public Task<string> CreateSessionAsync(
        string? workspaceId = null,
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = "sess-demo-" + Guid.NewGuid().ToString("N")[..6];
            var workspaceIndex = string.IsNullOrWhiteSpace(workspaceId)
                ? -1
                : _workspaces.FindIndex(workspace => workspace.WorkspaceId == workspaceId);
            var path = cwd
                       ?? (workspaceIndex >= 0 ? _workspaces[workspaceIndex].Path : null)
                       ?? @"D:\Code\VibeCoding\DSHRemote";

            _sessions.Add(new SessionSummary(
                SessionId: id,
                Title: "新会话",
                Cwd: path,
                Model: "deepseek-v4.1-flash",
                Running: false,
                AgentAvailable: true,
                Blank: true,
                UpdatedAt: now,
                LastPromptAt: now,
                ParentSessionId: null,
                Origin: null));
            _history[id] = new List<SessionEventRecord>();
            _queues[id] = new List<QueuedPrompt>();
            _seq[id] = 0;
            _running[id] = false;
            _turn[id] = 0;

            if (workspaceIndex >= 0)
            {
                var workspace = _workspaces[workspaceIndex];
                var ids = workspace.SessionIds.ToList();
                ids.Insert(0, id);
                _workspaces[workspaceIndex] = workspace with { SessionIds = ids, UpdatedAt = now };
            }

            return Task.FromResult(id);
        }
    }

    public Task<DshImageData> GetImageAsync(
        string sessionId,
        DshImageRef image,
        CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        lock (_gate)
        {
            if (_uploadedImages.TryGetValue(image.AttachmentId, out var uploaded))
            {
                bytes = uploaded;
            }
        }

        bytes ??= image.AttachmentId switch
        {
            "att-demo-1" => DemoImages.VioletBytes(),
            "att-demo-2" => DemoImages.MintBytes(),
            _ => null
        };

        if (bytes is null)
        {
            throw new DshRemoteException("image/not-found", "attachment not found: " + image.AttachmentId);
        }

        return Task.FromResult(new DshImageData(image, bytes));
    }

    // ------------------------------------------------------------------------------------------------
    // IModelClient: fake catalog + per-session selection so --demo can exercise the model picker
    // ------------------------------------------------------------------------------------------------

    private DshModelCatalogResult? _demoModelCatalog;

    public Task<DshModelCatalogResult> GetModelCatalogAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (forceRefresh)
        {
            _demoModelCatalog = null;
        }

        _demoModelCatalog ??= new DshModelCatalogResult(
            BuildDemoModelCatalog(),
            DshModelCatalogSupport.Supported,
            Message: null);
        return Task.FromResult(_demoModelCatalog);
    }

    public Task<DshModelSelection> SelectModelAsync(
        string sessionId,
        string provider,
        string model,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DshModelSelection selection;
        lock (_gate)
        {
            var index = _sessions.FindIndex(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new DshRemoteException("session/not-found", "session not found: " + sessionId);
            }

            var descriptor = BuildDemoModelCatalog().Groups
                .FirstOrDefault(group => string.Equals(group.Id, provider, StringComparison.Ordinal))
                ?.Models.FirstOrDefault(item => string.Equals(item.Id, model, StringComparison.Ordinal));
            if (descriptor is null)
            {
                throw new DshRemoteException(
                    "session/model-unavailable",
                    "Select an available model before sending a message.");
            }

            var reasoning = descriptor.Reasoning;
            string? materialized;
            if (reasoning is null)
            {
                if (!string.IsNullOrWhiteSpace(reasoningEffort))
                {
                    throw new DshRemoteException(
                        "session/model-unavailable",
                        $"provider \"{provider}\" model \"{model}\" does not support reasoning effort \"{reasoningEffort}\"");
                }

                materialized = null;
            }
            else if (string.IsNullOrWhiteSpace(reasoningEffort))
            {
                materialized = string.IsNullOrWhiteSpace(reasoning.DefaultEffort) ? null : reasoning.DefaultEffort;
            }
            else if (reasoning.Efforts is not null &&
                     reasoning.Efforts.Any(effort => string.Equals(effort.Id, reasoningEffort, StringComparison.Ordinal)))
            {
                materialized = reasoningEffort;
            }
            else
            {
                throw new DshRemoteException(
                    "session/model-unavailable",
                    $"provider \"{provider}\" model \"{model}\" does not support reasoning effort \"{reasoningEffort}\"");
            }

            selection = new DshModelSelection(provider, model, materialized);
            _sessions[index] = _sessions[index] with
            {
                Model = model,
                ModelSelection = selection,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        // Durably announce the change like the real host does, so an open picker/chip syncs through the
        // same follow-event path as production.
        Emit(sessionId, "model/selection", ModelSelectionJson(selection));
        return Task.FromResult(selection);
    }

    private static DshModelCatalog BuildDemoModelCatalog()
        => new(
            new DshModelSelection("deepseek-official", "deepseek-v4.1-flash", "high"),
            new[] { "deepseek-official", "opencode-go" },
            new[]
            {
                new DshModelGroup(
                    "deepseek-official",
                    "DeepSeek 官方",
                    new[]
                    {
                        new DshModelDescriptor(
                            "deepseek-v4.1-flash",
                            "V4.1 Flash",
                            "快速模型，适合日常改码与问答。",
                            new DshModelReasoning(
                                new[]
                                {
                                    new DshModelEffort("low", "低", "更快，适合简单改动。"),
                                    new DshModelEffort("medium", "中", "速度与质量平衡。"),
                                    new DshModelEffort("high", "高", "更充分的推理，适合复杂任务。")
                                },
                                "high"),
                            new DshModelBadge("推荐", "info")),
                        new DshModelDescriptor(
                            "deepseek-v4.1-pro",
                            "V4.1 Pro",
                            "深度模型，不支持单独设置推理等级。",
                            Reasoning: null,
                            Badge: null)
                    }),
                new DshModelGroup(
                    "opencode-go",
                    "OpenCode Go",
                    new[]
                    {
                        new DshModelDescriptor(
                            "qwen3-coder",
                            "Qwen3 Coder",
                            "第三方可路由模型（演示）。",
                            Reasoning: null,
                            Badge: new DshModelBadge("第三方", "neutral"))
                    })
            },
            new[]
            {
                new DshModelFailure("deepseek-account", "DeepSeek 账号", "未配置 API key，已跳过该分组。")
            });

    private static string ModelSelectionJson(DshModelSelection selection)
    {
        var fields = new List<string>
        {
            "\"provider\":" + Quote(selection.Provider),
            "\"model\":" + Quote(selection.Model)
        };
        if (!string.IsNullOrWhiteSpace(selection.ReasoningEffort))
        {
            fields.Add("\"reasoningEffort\":" + Quote(selection.ReasoningEffort));
        }

        return "{" + string.Join(",", fields) + "}";
    }

    // ------------------------------------------------------------------------------------------------
    // JSON helpers (single-quoted C# strings keep the demo payload readable)
    // ------------------------------------------------------------------------------------------------

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string UserMessageJsonWithImages(
        string text,
        string? rpcId,
        IReadOnlyList<DemoImage> images)
    {
        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add($"{{\"type\":\"text\",\"text\":{Quote(text)}}}");
        }

        foreach (var image in images)
        {
            blocks.Add(ImageBlockJson(image));
        }

        var source = rpcId is null
            ? "{\"kind\":\"user\"}"
            : $"{{\"kind\":\"user\",\"rpcId\":{Quote(rpcId)}}}";
        return $"{{\"content\":[{string.Join(",", blocks)}],\"source\":{source},\"role\":\"user\",\"id\":{Quote(Guid.NewGuid().ToString())}}}";
    }

    private static string ImageBlockJson(DemoImage image)
        => $"{{\"type\":\"image\",\"attachment\":{{\"attachmentId\":{Quote(image.Id)},\"mediaType\":{Quote(image.MediaType)},\"bytes\":{image.Bytes},\"width\":0,\"height\":0,\"name\":{(image.Name is null ? "null" : Quote(image.Name))}}}}}";

    private static string ToolResultJsonWithImage(
        int turn,
        int step,
        string callId,
        string text,
        IReadOnlyList<DemoImage> images)
    {
        var blocks = new List<string> { $"{{\"type\":\"text\",\"text\":{Quote(text)}}}" };
        foreach (var image in images)
        {
            blocks.Add(ImageBlockJson(image));
        }

        return $"{{\"turn\":{turn},\"step\":{step},\"toolCallId\":{Quote(callId)},\"message\":{{\"role\":\"tool\",\"source\":{{\"kind\":\"tool\",\"callId\":{Quote(callId)}}},\"content\":[{string.Join(",", blocks)}]}}}}";
    }


    private static string UserMessageJson(string text, string? rpcId = null)
    {
        var source = rpcId is null
            ? "{\"kind\":\"user\"}"
            : $"{{\"kind\":\"user\",\"rpcId\":{Quote(rpcId)}}}";
        return $"{{\"content\":[{{\"type\":\"text\",\"text\":{Quote(text)}}}],\"source\":{source},\"role\":\"user\",\"id\":{Quote(Guid.NewGuid().ToString())}}}";
    }

    private static string AssistantMessageJson(int turn, int step, string text, string? reasoning = null)
        => $"{{\"turn\":{turn},\"step\":{step},\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"reasoning\",\"text\":{Quote(reasoning ?? "先梳理一下上下文，再给出结论。")}}},{{\"type\":\"text\",\"text\":{Quote(text)}}}]}}}}";

    private static string LiveChunkJson(string attemptId, int turn, int step, string chunkType, int index, string text)
        => $"{{\"attemptId\":{Quote(attemptId)},\"turn\":{turn},\"step\":{step},\"chunk\":{{\"type\":{Quote(chunkType)},\"index\":{index},\"text\":{Quote(text)}}}}}";

    private static string ToolResultJson(int turn, int step, string callId, string output)
        => $"{{\"turn\":{turn},\"step\":{step},\"toolCallId\":{Quote(callId)},\"message\":{{\"role\":\"tool\",\"source\":{{\"kind\":\"tool\",\"callId\":{Quote(callId)}}},\"content\":[{{\"type\":\"text\",\"text\":{Quote(output)}}}]}}}}";

    private static string StepJson(int turn, int step) => $"{{\"turn\":{turn},\"step\":{step}}}";

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
