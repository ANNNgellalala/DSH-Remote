using System.Collections.ObjectModel;
using System.Windows.Input;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>One queued prompt shown under the chat header (editable while it is still queued).</summary>
public sealed class QueueItemViewModel : ObservableObject
{
    private readonly SessionViewModel _owner;
    private string _text;
    private bool _isEditing;

    public QueueItemViewModel(QueuedPrompt prompt, SessionViewModel owner)
    {
        _owner = owner;
        Id = prompt.Id;
        Placement = prompt.Placement;
        _text = prompt.Text ?? string.Empty;
        EditCommand = new RelayCommand(() => IsEditing = true);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelEditCommand = new RelayCommand(() =>
        {
            _text = OriginalText;
            IsEditing = false;
            OnPropertyChanged(nameof(Text));
        });
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        SteerNowCommand = new AsyncRelayCommand(SteerNowAsync);
    }

    public string Id { get; }

    public string Placement { get; }

    public string PlacementText => string.IsNullOrWhiteSpace(Placement) || Placement == "queue" ? "排队" : Placement;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string OriginalText { get; internal set; } = string.Empty;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsReadOnly));
            }
        }
    }

    public bool IsReadOnly => !IsEditing;

    public ICommand EditCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelEditCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand SteerNowCommand { get; }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        OriginalText = Text;
        IsEditing = false;
        await _owner.UpdateQueueItemAsync(this, QueueEditAction.Edit, Text).ConfigureAwait(true);
    }

    private async Task RemoveAsync()
    {
        await _owner.UpdateQueueItemAsync(this, QueueEditAction.Remove).ConfigureAwait(true);
    }

    private async Task SteerNowAsync()
    {
        await _owner.UpdateQueueItemAsync(this, QueueEditAction.Steer).ConfigureAwait(true);
    }
}

/// <summary>One approval card in the tray.</summary>
public sealed class ApprovalViewModel : ObservableObject
{
    private readonly InteractionHub _hub;
    private readonly DshApprovalRequest _request;
    private bool _isSubmitting;
    private bool _isSubmitted;
    private bool _isExpired;
    private string _statusText = "等待你的决定";

    public ApprovalViewModel(DshApprovalRequest request, InteractionHub hub)
    {
        _hub = hub;
        _request = request;
        ReceivedAt = DateTimeOffset.Now;
        AllowCommand = new AsyncRelayCommand(() => RespondAsync(DshApprovalOutcome.AllowedOnce));
        DenyCommand = new AsyncRelayCommand(() => RespondAsync(DshApprovalOutcome.Rejected));
    }

    public string RequestId => _request.RequestId;

    public string SessionId => _request.SessionId;

    public string ToolName => string.IsNullOrWhiteSpace(_request.ToolName) ? "工具" : _request.ToolName;

    public string? CallId => _request.CallId;

    public DateTimeOffset ReceivedAt { get; }

    /// <summary>Local timeout guard; host requests carry no reliable expiry in M0 data.</summary>
    public DateTimeOffset ExpiresAt => ReceivedAt.AddMinutes(2);

    public string ReasonText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_request.DisplayReason))
            {
                return _request.DisplayReason!;
            }

            if (!string.IsNullOrWhiteSpace(_request.Reason))
            {
                return _request.Reason!;
            }

            return "该工具调用需要你的授权才能继续。";
        }
    }

    public bool HasArguments => !string.IsNullOrWhiteSpace(_request.ArgumentsJson);

    public string ArgumentsJson => _request.ArgumentsJson ?? string.Empty;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (SetProperty(ref _isSubmitting, value))
            {
                Raise(nameof(CanRespond), nameof(IsBusy));
            }
        }
    }

    public bool IsSubmitted
    {
        get => _isSubmitted;
        private set
        {
            if (SetProperty(ref _isSubmitted, value))
            {
                Raise(nameof(CanRespond), nameof(IsBusy), nameof(ShowButtons));
            }
        }
    }

    public bool IsExpired
    {
        get => _isExpired;
        internal set
        {
            if (SetProperty(ref _isExpired, value))
            {
                Raise(nameof(CanRespond), nameof(ShowButtons));
            }
        }
    }

    public bool CanRespond => !IsSubmitting && !IsSubmitted && !IsExpired;

    public bool IsBusy => IsSubmitting || IsSubmitted;

    public bool ShowButtons => CanRespond;

    public string CountdownText
    {
        get
        {
            if (IsSubmitted || IsExpired)
            {
                return string.Empty;
            }

            var remaining = ExpiresAt - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
            {
                return "即将失效";
            }

            return remaining.TotalSeconds < 60
                ? $"{(int)remaining.TotalSeconds}s 后失效"
                : $"{(int)remaining.TotalMinutes} 分钟后失效";
        }
    }

    public ICommand AllowCommand { get; }

    public ICommand DenyCommand { get; }

    internal void MarkExpired()
    {
        IsExpired = true;
        StatusText = "已失效";
    }

    internal void SetStatus(string status) => StatusText = status;

    /// <summary>Lets the hub refresh the locally computed countdown once per second.</summary>
    internal void RefreshCountdown() => OnPropertyChanged(nameof(CountdownText));

    private async Task RespondAsync(DshApprovalOutcome outcome)
    {
        if (!CanRespond)
        {
            return;
        }

        IsSubmitting = true;
        StatusText = "提交中…";
        var ok = await _hub.RespondApprovalAsync(this, outcome).ConfigureAwait(true);
        IsSubmitting = false;
        IsSubmitted = ok;
        StatusText = ok
            ? "已提交，等待桌面/Agent 继续"
            : "提交失败，可能已在桌面处理";
    }
}

/// <summary>One selectable option of a question.</summary>
public sealed class QuestionOptionViewModel : ObservableObject
{
    private readonly QuestionItemViewModel _owner;
    private bool _isSelected;

    public QuestionOptionViewModel(QuestionItemViewModel owner, string label)
    {
        _owner = owner;
        Label = label;
    }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
            {
                return;
            }

            if (value)
            {
                _owner.OnOptionSelected(this);
            }
        }
    }
}

/// <summary>One question inside a batch.</summary>
public sealed class QuestionItemViewModel : ObservableObject
{
    private string _freeText = string.Empty;

    public QuestionItemViewModel(DshQuestionItem item)
    {
        Id = item.Id;
        Text = item.Text;
        Header = item.Detail ?? string.Empty;
        Options = new ObservableCollection<QuestionOptionViewModel>(
            (item.Options ?? Array.Empty<string>())
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Select(option => new QuestionOptionViewModel(this, option)));
    }

    public string Id { get; }

    public string Text { get; }

    public string Header { get; }

    public bool HasHeader => !string.IsNullOrWhiteSpace(Header);

    public ObservableCollection<QuestionOptionViewModel> Options { get; }

    public bool HasOptions => Options.Count > 0;

    public string FreeText
    {
        get => _freeText;
        set => SetProperty(ref _freeText, value);
    }

    public bool AllowFreeText => true;

    public string? SelectedLabel => Options.FirstOrDefault(option => option.IsSelected)?.Label;

    public string AnswerText => SelectedLabel ?? FreeText.Trim();

    public bool IsAnswered => !string.IsNullOrWhiteSpace(AnswerText);

    internal void OnOptionSelected(QuestionOptionViewModel selected)
    {
        foreach (var option in Options)
        {
            if (!ReferenceEquals(option, selected))
            {
                option.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(AnswerText));
        OnPropertyChanged(nameof(IsAnswered));
        Raise(nameof(HasSelection));
    }

    public bool HasSelection => SelectedLabel is not null;

    // Called by radio-style UI taps from XAML command if needed.
    public void RefreshAnswerState()
    {
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(AnswerText));
        OnPropertyChanged(nameof(IsAnswered));
    }
}

/// <summary>A batched question request rendered as an interactive card.</summary>
public sealed class QuestionViewModel : ObservableObject
{
    private readonly InteractionHub _hub;
    private bool _isSubmitting;
    private bool _isSubmitted;
    private string _statusText = "等待你的回答";

    public QuestionViewModel(DshQuestionRequest request, InteractionHub hub)
    {
        _hub = hub;
        RequestId = request.RequestId;
        SessionId = request.SessionId;
        ReceivedAt = DateTimeOffset.Now;
        Items = new ObservableCollection<QuestionItemViewModel>(
            request.Questions.Select(question => new QuestionItemViewModel(question)));
        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
        CancelCommand = new AsyncRelayCommand(CancelAsync);
    }

    public string RequestId { get; }

    public string SessionId { get; }

    public DateTimeOffset ReceivedAt { get; }

    public DateTimeOffset ExpiresAt => ReceivedAt.AddMinutes(5);

    public ObservableCollection<QuestionItemViewModel> Items { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (SetProperty(ref _isSubmitting, value))
            {
                Raise(nameof(CanSubmit), nameof(IsBusy), nameof(ShowButtons));
            }
        }
    }

    public bool IsSubmitted
    {
        get => _isSubmitted;
        private set
        {
            if (SetProperty(ref _isSubmitted, value))
            {
                Raise(nameof(CanSubmit), nameof(IsBusy), nameof(ShowButtons));
            }
        }
    }

    public bool IsBusy => IsSubmitting || IsSubmitted;

    public bool CanSubmit => !IsBusy;

    public bool ShowButtons => !IsBusy;

    public ICommand SubmitCommand { get; }

    public ICommand CancelCommand { get; }

    internal void MarkExpired()
    {
        IsSubmitted = true;
        StatusText = "已失效";
    }

    internal void SetStatus(string status) => StatusText = status;

    private async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        for (var index = 0; index < Items.Count; index++)
        {
            if (!Items[index].IsAnswered)
            {
                StatusText = $"请先回答第 {index + 1} 个问题。";
                return;
            }
        }

        IsSubmitting = true;
        StatusText = "提交中…";
        var answers = Items
            .Select(item => new DshQuestionAnswerItem(
                item.Id,
                item.SelectedLabel is null ? Array.Empty<string>() : new[] { item.SelectedLabel! },
                string.IsNullOrWhiteSpace(item.FreeText) ? null : item.FreeText.Trim()))
            .ToList();
        var ok = await _hub.RespondQuestionAsync(this, answers, cancelled: false).ConfigureAwait(true);
        IsSubmitting = false;
        IsSubmitted = ok;
        StatusText = ok ? "已提交答案" : "提交失败，可能已在桌面处理";
    }

    private async Task CancelAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsSubmitting = true;
        StatusText = "正在关闭…";
        var ok = await _hub.RespondQuestionAsync(this, Array.Empty<DshQuestionAnswerItem>(), cancelled: true).ConfigureAwait(true);
        IsSubmitting = false;
        IsSubmitted = true;
        StatusText = ok ? "已关闭提问" : "关闭失败，可能已在桌面处理";
    }
}
