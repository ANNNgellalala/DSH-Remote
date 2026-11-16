using System.Collections.ObjectModel;
using Avalonia.Threading;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>
/// App-wide interaction hub: one subscription to <see cref="IInteractionClient"/> for approvals and
/// questions, deduplicated by request id, plus a per-tick expiry guard (host requests carry no reliable
/// expiry, and a request handled on the desktop simply stops appearing).
/// </summary>
public sealed class InteractionHub : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QuestionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResolvedLinger = TimeSpan.FromSeconds(8);

    private readonly ObservableCollection<ApprovalViewModel> _approvals = new();
    private readonly ObservableCollection<QuestionViewModel> _questions = new();
    private readonly Dictionary<string, ApprovalViewModel> _approvalsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QuestionViewModel> _questionsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvedApprovalIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolvedQuestionIds = new(StringComparer.Ordinal);
    private IInteractionClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _approvalLoop;
    private Task? _questionLoop;

    private string? _activeSessionId;
    private string _notice = string.Empty;
    private int _noticeVersion;

    public InteractionHub()
    {
        Ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        Ticker.Tick += (_, _) => OnTick();
    }

    public ObservableCollection<ApprovalViewModel> Approvals => _approvals;

    public ObservableCollection<QuestionViewModel> Questions => _questions;

    public bool IsAttached => _client is not null;

    public bool HasPendingInteractions => ActiveApproval is not null || ActiveQuestion is not null;

    /// <summary>The session currently open in the chat page; interaction cards are scoped to it.</summary>
    public string? ActiveSessionId
    {
        get => _activeSessionId;
        set
        {
            if (SetProperty(ref _activeSessionId, value))
            {
                RaiseInteractionStateChanged();
            }
        }
    }

    public ApprovalViewModel? ActiveApproval
    {
        get
        {
            var matches = _approvals.Where(approval => approval.CanRespond).ToList();
            return matches.FirstOrDefault(approval => MatchesActiveSession(approval.SessionId)) ?? matches.FirstOrDefault();
        }
    }

    public QuestionViewModel? ActiveQuestion
    {
        get
        {
            var matches = _questions.Where(question => !question.IsBusy).ToList();
            return matches.FirstOrDefault(question => MatchesActiveSession(question.SessionId)) ?? matches.FirstOrDefault();
        }
    }

    public bool HasActiveApproval => ActiveApproval is not null;

    public bool HasActiveQuestion => ActiveQuestion is not null;

    public int PendingApprovalCount => _approvals.Count(approval => approval.CanRespond);

    public int PendingQuestionCount => _questions.Count(question => !question.IsBusy);

    public string PendingBadgeText
    {
        get
        {
            var parts = new List<string>();
            if (HasActiveApproval)
            {
                parts.Add($"待审批 {PendingApprovalCount}");
            }

            if (HasActiveQuestion)
            {
                parts.Add($"待回答 {PendingQuestionCount}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Transient in-app notice (request handled on the desktop, submit failed, …).</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public int NoticeVersion => _noticeVersion;

    internal DispatcherTimer Ticker { get; }

    public event EventHandler? InteractionStateChanged;

    /// <summary>Raised when a new approval request arrives (used for local notifications).</summary>
    public event EventHandler<ApprovalViewModel>? ApprovalReceived;

    /// <summary>Raised when a new question batch arrives (used for local notifications).</summary>
    public event EventHandler<QuestionViewModel>? QuestionReceived;

    public void Attach(IInteractionClient client)
    {
        if (ReferenceEquals(_client, client) && _cts is { IsCancellationRequested: false })
        {
            return;
        }

        Detach();
        _client = client;
        _cts = new CancellationTokenSource();
        _approvalLoop = Task.Run(() => ApprovalLoopAsync(client, _cts.Token));
        _questionLoop = Task.Run(() => QuestionLoopAsync(client, _cts.Token));
        Ticker.Start();
        RaiseInteractionStateChanged();
    }

    public void Detach()
    {
        Ticker.Stop();
        _client = null;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        _cts?.Dispose();
        _cts = null;
        _approvalLoop = null;
        _questionLoop = null;
        _approvals.Clear();
        _questions.Clear();
        _approvalsById.Clear();
        _questionsById.Clear();
        _resolvedApprovalIds.Clear();
        _resolvedQuestionIds.Clear();
        RaiseInteractionStateChanged();
    }

    /// <summary>Called by a session when its turn ends: any pending request for it was handled elsewhere.</summary>
    public void DismissForSession(string sessionId, string notice)
    {
        var removed = false;
        foreach (var approval in _approvals.Where(item => item.SessionId == sessionId).ToList())
        {
            if (approval.IsSubmitted)
            {
                continue;
            }

            approval.MarkExpired();
            RemoveApproval(approval);
            removed = true;
        }

        foreach (var question in _questions.Where(item => item.SessionId == sessionId).ToList())
        {
            if (question.IsSubmitted)
            {
                continue;
            }

            question.MarkExpired();
            RemoveQuestion(question);
            removed = true;
        }

        if (removed)
        {
            ShowNotice(notice);
        }
    }

    internal async Task<bool> RespondApprovalAsync(ApprovalViewModel approval, DshApprovalOutcome outcome)
    {
        var client = _client;
        if (client is null)
        {
            return false;
        }

        try
        {
            await client.RespondApprovalAsync(approval.RequestId, outcome).ConfigureAwait(true);
            _resolvedApprovalIds.Add(approval.RequestId);
            approval.SetStatus(outcome == DshApprovalOutcome.Rejected ? "已拒绝" : "已允许一次");
            _ = RemoveApprovalAfterAsync(approval, ResolvedLinger);
            RaiseInteractionStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            approval.SetStatus("提交失败: " + ex.Message);
            ShowNotice("审批提交失败: " + ex.Message);
            return false;
        }
    }

    internal async Task<bool> RespondQuestionAsync(
        QuestionViewModel question,
        IReadOnlyList<DshQuestionAnswerItem> answers,
        bool cancelled)
    {
        var client = _client;
        if (client is null)
        {
            return false;
        }

        try
        {
            await client.RespondQuestionAsync(question.RequestId, answers, cancelled).ConfigureAwait(true);
            _resolvedQuestionIds.Add(question.RequestId);
            _ = RemoveQuestionAfterAsync(question, ResolvedLinger);
            RaiseInteractionStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            question.SetStatus("提交失败: " + ex.Message);
            ShowNotice("回答提交失败: " + ex.Message);
            return false;
        }
    }

    private async Task ApprovalLoopAsync(IInteractionClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in client.ApprovalsAsync(cancellationToken).ConfigureAwait(false))
            {
                Dispatcher.UIThread.Post(() => OnApproval(request));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowNotice("审批流已断开: " + ex.Message));
        }
    }

    private async Task QuestionLoopAsync(IInteractionClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in client.QuestionsAsync(cancellationToken).ConfigureAwait(false))
            {
                Dispatcher.UIThread.Post(() => OnQuestion(request));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ShowNotice("问答流已断开: " + ex.Message));
        }
    }

    private void OnApproval(DshApprovalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            _approvalsById.ContainsKey(request.RequestId) ||
            _resolvedApprovalIds.Contains(request.RequestId))
        {
            return;
        }

        var approval = new ApprovalViewModel(request, this);
        _approvalsById[request.RequestId] = approval;
        _approvals.Add(approval);
        RaiseInteractionStateChanged();
        ApprovalReceived?.Invoke(this, approval);
        ShowNotice($"「{approval.ToolName}」需要审批");
    }

    private void OnQuestion(DshQuestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            _questionsById.ContainsKey(request.RequestId) ||
            _resolvedQuestionIds.Contains(request.RequestId))
        {
            return;
        }

        var question = new QuestionViewModel(request, this);
        _questionsById[request.RequestId] = question;
        _questions.Add(question);
        ShowNotice("Agent 有问题需要你回答");
        RaiseInteractionStateChanged();
        QuestionReceived?.Invoke(this, question);
    }

    private void OnTick()
    {
        var now = DateTimeOffset.Now;
        var dirty = false;
        foreach (var approval in _approvals.ToList())
        {
            if (approval.IsSubmitted || approval.IsExpired)
            {
                continue;
            }

            approval.RefreshCountdown();
            if (now - approval.ReceivedAt >= ApprovalTimeout)
            {
                approval.MarkExpired();
                _ = RemoveApprovalAfterAsync(approval, TimeSpan.FromSeconds(4));
                dirty = true;
            }
        }

        foreach (var question in _questions.ToList())
        {
            if (question.IsSubmitted)
            {
                continue;
            }

            if (now - question.ReceivedAt >= QuestionTimeout)
            {
                question.MarkExpired();
                _ = RemoveQuestionAfterAsync(question, TimeSpan.FromSeconds(4));
                dirty = true;
            }
        }

        if (dirty)
        {
            RaiseInteractionStateChanged();
        }
    }

    private async Task RemoveApprovalAfterAsync(ApprovalViewModel approval, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(true);
        Dispatcher.UIThread.Post(() => RemoveApproval(approval));
    }

    private async Task RemoveQuestionAfterAsync(QuestionViewModel question, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(true);
        Dispatcher.UIThread.Post(() => RemoveQuestion(question));
    }

    private void RemoveApproval(ApprovalViewModel approval)
    {
        if (_approvals.Remove(approval))
        {
            _approvalsById.Remove(approval.RequestId);
        }

        RaiseInteractionStateChanged();
    }

    private void RemoveQuestion(QuestionViewModel question)
    {
        if (_questions.Remove(question))
        {
            _questionsById.Remove(question.RequestId);
        }

        RaiseInteractionStateChanged();
    }

    private bool MatchesActiveSession(string sessionId)
        => string.IsNullOrWhiteSpace(ActiveSessionId) ||
           string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal);

    private void ShowNotice(string message)
    {
        Notice = message;
        _noticeVersion++;
        OnPropertyChanged(nameof(NoticeVersion));
        var version = _noticeVersion;
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (version == _noticeVersion)
            {
                Notice = string.Empty;
            }
        }), TaskScheduler.Default);
    }

    private void RaiseInteractionStateChanged()
    {
        Raise(
            nameof(ActiveApproval),
            nameof(ActiveQuestion),
            nameof(HasActiveApproval),
            nameof(HasActiveQuestion),
            nameof(HasPendingInteractions),
            nameof(PendingApprovalCount),
            nameof(PendingQuestionCount),
            nameof(PendingBadgeText),
            nameof(IsAttached));
        InteractionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        Detach();
        Ticker.Tick -= (_, _) => OnTick();
        return ValueTask.CompletedTask;
    }
}
