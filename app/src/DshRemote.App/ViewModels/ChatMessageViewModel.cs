using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Layout;

namespace DshRemote.App.ViewModels;

public enum ChatMessageKind
{
    User,
    Assistant,
    Tool,
    System,

    /// <summary>
    /// A durable <c>deliverables/presented</c> declaration. It is consumed by the turn projection (the files
    /// render as the turn's file card) and never reaches the timeline as a row of its own.
    /// </summary>
    Artifact,

    /// <summary>
    /// A context-compaction checkpoint (DSH summary compaction). Rendered as its own top-level card under the
    /// checkpoint event's position; the checkpoint replacement user message itself is never a transcript row.
    /// </summary>
    Compaction
}

/// <summary>
/// One row in the transcript.
/// <para>
/// Assistant rows can receive streamed increments (<see cref="AppendTextDelta"/>) and are revealed with a
/// character pump so bursts of deltas still type out smoothly; once finalised the visible text switches to
/// Markdown rendering. Tool rows start on <c>tool/call</c> and are filled in by <c>tool/result</c>.
/// </para>
/// </summary>
public sealed class ChatMessageViewModel : ObservableObject
{
    private const int MaxToolPayloadLength = 6000;
    private const int MaxReasoningLength = 20000;
    private const int ToolPreviewLength = 260;

    private string _text = string.Empty;
    private string _targetText = string.Empty;
    private int _revealedChars;
    private string _reasoningText = string.Empty;
    private bool _isReasoningExpanded;
    private bool _isStreaming;
    private bool _isFinal = true;
    private string _toolName = string.Empty;
    private string _argumentsJson = string.Empty;
    private bool _isArgumentsExpanded;
    private string _resultText = string.Empty;
    private bool _isResultExpanded;
    private string _statusText = string.Empty;
    private bool _isError;
    private long _startedAt;
    private long _finishedAt;
    private string? _headerOverride;
    private bool _hasResultApplied;

    // ---- Context compaction card state ---------------------------------------------------------
    private string _compactionSummary = string.Empty;
    private string _compactionMetaText = string.Empty;
    private string _compactionHintText = string.Empty;
    private bool _hasCompactionSummary;
    private bool _hasCompactionHint;
    private bool _isCompactionExpanded;
    private bool _isCompactionFailed;
    private bool _isCompactionManual;

    private ChatMessageViewModel(ChatMessageKind kind)
    {
        Kind = kind;
        ToggleReasoningCommand = new RelayCommand(() =>
        {
            IsReasoningExpanded = !IsReasoningExpanded;
            OnPropertyChanged(nameof(IsReasoningExpanded));
            OnPropertyChanged(nameof(ReasoningChevron));
        });
        ToggleArgumentsCommand = new RelayCommand(() => IsArgumentsExpanded = !IsArgumentsExpanded);
        ToggleResultCommand = new RelayCommand(() =>
        {
            IsResultExpanded = !IsResultExpanded;
            OnPropertyChanged(nameof(ResultToggleText));
        });
        ToggleCompactionCommand = new RelayCommand(() =>
        {
            IsCompactionExpanded = !IsCompactionExpanded;
            OnPropertyChanged(nameof(CompactionChevron));
        });
    }

    public ChatMessageKind Kind { get; }

    public long? Seq { get; set; }

    public long Time { get; set; }

    /// <summary>Tool call id used to join <c>tool/call</c> and <c>tool/result</c>.</summary>
    public string? CallId { get; init; }

    /// <summary>Agent turn this row belongs to (0 for user/system rows outside the process fold).</summary>
    public int Turn { get; set; }

    public int Step { get; set; }

    /// <summary>
    /// Internal turn banner (<c>▶ 新一轮开始</c> / <c>■ 本轮结束</c>). It is kept in
    /// <see cref="SessionViewModel.Messages"/> for turn timing and grouping but never rendered on its own:
    /// the turn fold carries that state. Anchoring the fold on a banner instead of the first real process
    /// row would pin the fold above the prompt that started the turn.
    /// </summary>
    public bool IsTurnBoundary { get; set; }

    public ICommand ToggleReasoningCommand { get; }

    public ICommand ToggleArgumentsCommand { get; }

    public ICommand ToggleResultCommand { get; }

    // ---- Classification / layout ---------------------------------------------------------------

    public bool IsUser => Kind == ChatMessageKind.User;

    public bool IsAssistant => Kind == ChatMessageKind.Assistant;

    public bool IsTool => Kind == ChatMessageKind.Tool;

    public bool IsSystem => Kind == ChatMessageKind.System;

    public bool IsArtifact => Kind == ChatMessageKind.Artifact;

    public bool IsCompaction => Kind == ChatMessageKind.Compaction;

    /// <summary>True after a <c>tool/result</c> has been applied; used to keep duplicate results idempotent.</summary>
    public bool HasResultApplied => _hasResultApplied;

    /// <summary>Produced files carried by an <see cref="ChatMessageKind.Artifact"/> row.</summary>
    public ObservableCollection<FileChangeViewModel> Files { get; } = new();

    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public string Header => _headerOverride ?? Kind switch
    {
        ChatMessageKind.User => "你",
        ChatMessageKind.Assistant => "助手",
        ChatMessageKind.Tool => string.IsNullOrWhiteSpace(ToolName) ? "工具" : "工具 · " + ToolName,
        ChatMessageKind.Artifact => "文件",
        ChatMessageKind.Compaction => "上下文压缩",
        _ => string.Empty
    };

    public bool HasHeader => Kind != ChatMessageKind.System;

    /// <summary>The generic header row is suppressed for the compaction card (its own header carries the meta).</summary>
    public bool ShowHeaderRow => HasHeader && !IsCompaction;

    public string TimeText => Time <= 0
        ? string.Empty
        : DateTimeOffset.FromUnixTimeMilliseconds(Time).ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    // ---- Context compaction card ---------------------------------------------------------------

    /// <summary>Durable compaction identity (<c>source.compactionId</c> of the checkpoint event).</summary>
    public string CompactionId { get; private set; } = string.Empty;

    public ICommand ToggleCompactionCommand { get; }

    public bool IsCompactionExpanded
    {
        get => _isCompactionExpanded;
        set
        {
            if (SetProperty(ref _isCompactionExpanded, value))
            {
                OnPropertyChanged(nameof(CompactionChevron));
            }
        }
    }

    public string CompactionChevron => IsCompactionExpanded ? "▾" : "▸";

    /// <summary>Collapsed-header metadata: 自动/手动压缩 · 已压缩 N 项 · 约 M tokens.</summary>
    public string CompactionMetaText => _compactionMetaText;

    public bool IsCompactionFailed => _isCompactionFailed;

    public bool IsCompactionManual => _isCompactionManual;

    /// <summary>Safe summary projection (Markdown) from <c>compaction/summary.data.summary</c>.</summary>
    public string CompactionSummary => _compactionSummary;

    public bool HasCompactionSummary => _hasCompactionSummary;

    /// <summary>Hint shown when the summary is missing (page split) or the compaction failed.</summary>
    public string CompactionHintText => _compactionHintText;

    public bool HasCompactionHint => _hasCompactionHint;

    /// <summary>
    /// Refreshes the card from the compaction state machine. Idempotent so the same checkpoint observed from a
    /// history page and from a live event cannot create divergent cards.
    /// </summary>
    public void ApplyCompactionState(
        string? summary,
        int shadowedItemCount,
        int? shadowedTokenCount,
        bool manual,
        bool failed)
    {
        _isCompactionManual = manual;
        _isCompactionFailed = failed;
        _compactionSummary = string.IsNullOrWhiteSpace(summary) ? string.Empty : summary!;
        _hasCompactionSummary = !string.IsNullOrWhiteSpace(_compactionSummary);

        var parts = new List<string>(4);
        parts.Add(manual ? "手动压缩" : "自动压缩");
        if (shadowedItemCount > 0)
        {
            parts.Add("已压缩 " + shadowedItemCount.ToString("N0", CultureInfo.CurrentCulture) + " 项");
        }

        if (shadowedTokenCount is > 0)
        {
            parts.Add("约 " + FormatCompactTokens(shadowedTokenCount.Value) + " tokens");
        }

        if (failed)
        {
            parts.Add("失败");
        }

        _compactionMetaText = string.Join(" · ", parts);
        _compactionHintText = failed
            ? "本次压缩未完成，历史仍可正常查看。"
            : _hasCompactionSummary
                ? "此前的历史仍保留在上方；模型侧已改用该摘要。"
                : "摘要尚未加载，向上翻页可补全。";
        _hasCompactionHint = true;
        Raise(nameof(CompactionMetaText), nameof(CompactionSummary), nameof(HasCompactionSummary),
            nameof(CompactionHintText), nameof(HasCompactionHint), nameof(IsCompactionFailed),
            nameof(IsCompactionManual), nameof(Header));
        RaiseCopyState();
    }

    private static string FormatCompactTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (tokens >= 1_000)
        {
            return (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    // ---- Body ----------------------------------------------------------------------------------

    /// <summary>Visible text (what the reveal pump has produced so far).</summary>
    public string Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value))
            {
                Raise(nameof(HasText), nameof(IsMarkdown));
                RaiseCopyState();
            }
        }
    }

    /// <summary>Full received text; <see cref="Text"/> catches up to it.</summary>
    public string TargetText => _targetText;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    // ---- Copy ----------------------------------------------------------------------------------

    /// <summary>
    /// Text the row's "copy" action writes to the clipboard. Messages copy their full body, tool rows copy
    /// the result (falling back to the arguments when no result arrived yet), and a compaction card copies
    /// its summary. The raw text is used, so code fences/tables keep their Markdown source exactly as the
    /// agent produced it (the transcript's plain rendering is a lossy view).
    /// </summary>
    public string CopyText => IsCompaction
        ? CompactionSummary
        : IsTool
            ? (HasResult ? ResultText : ArgumentsJson)
            : TargetText;

    /// <summary>Label for the row's copy action (tool rows are explicit about what gets copied).</summary>
    public string CopyLabel => IsTool
        ? (HasResult ? "复制结果" : "复制参数")
        : IsCompaction ? "复制摘要" : "复制";

    /// <summary>True when the copy action has something to put on the clipboard.</summary>
    public bool HasCopyableText => !string.IsNullOrWhiteSpace(CopyText);

    private void RaiseCopyState()
        => Raise(nameof(CopyText), nameof(CopyLabel), nameof(HasCopyableText), nameof(ShowFooter));

    /// <summary>
    /// Trailing metadata row (status, duration, copy). Visible even without a status: a finished answer
    /// still needs the copy entry.
    /// </summary>
    public bool ShowFooter => HasStatus || HasDuration || HasCopyableText;

    /// <summary>Image blocks carried by this message/tool result, in content order.</summary>
    public ObservableCollection<ChatImagePartViewModel> Images { get; } = new();

    public bool HasImages => Images.Count > 0;

    public void AddImage(ChatImagePartViewModel image)
    {
        Images.Add(image);
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(ShowImagesOutsideTool));
    }

    /// <summary>Message-level images render after the body; tool-card images render inside the tool block.</summary>
    public bool ShowImagesOutsideTool => HasImages && !IsTool;

    /// <summary>Final assistant rows render through ArcMarkdownView; streaming rows stay plain text.</summary>
    public bool IsMarkdown => IsAssistant && !IsStreaming && !string.IsNullOrWhiteSpace(TargetText);

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                Raise(nameof(IsMarkdown), nameof(IsIdleAssistant), nameof(ReasoningHeader), nameof(ShowStreamingCaret));
            }
        }
    }

    public bool IsIdleAssistant => IsAssistant && !IsStreaming;

    private bool _showStreamingCaret;

    public bool ShowStreamingCaret
    {
        get => _showStreamingCaret && IsStreaming;
        private set => SetProperty(ref _showStreamingCaret, value);
    }

    // ---- Reasoning -----------------------------------------------------------------------------

    public string ReasoningText
    {
        get => _reasoningText;
        private set
        {
            if (SetProperty(ref _reasoningText, value))
            {
                Raise(nameof(HasReasoning), nameof(ReasoningHeader), nameof(ReasoningPreview));
            }
        }
    }

    public bool HasReasoning => !string.IsNullOrWhiteSpace(ReasoningText);

    public string ReasoningHeader => IsStreaming ? "正在思考…" : "思考过程";

    public string ReasoningChevron => IsReasoningExpanded ? "▾" : "▸";

    public bool IsReasoningExpanded
    {
        get => _isReasoningExpanded;
        set
        {
            if (SetProperty(ref _isReasoningExpanded, value))
            {
                OnPropertyChanged(nameof(ReasoningChevron));
            }
        }
    }

    public string ReasoningPreview
    {
        get
        {
            var flat = ReasoningText.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= 90 ? flat : flat[..90] + "…";
        }
    }

    // ---- Tool card -----------------------------------------------------------------------------

    public string ToolName
    {
        get => _toolName;
        private set
        {
            if (SetProperty(ref _toolName, value))
            {
                OnPropertyChanged(nameof(Header));
            }
        }
    }

    public string ArgumentsJson
    {
        get => _argumentsJson;
        private set
        {
            if (SetProperty(ref _argumentsJson, value))
            {
                Raise(nameof(HasArguments));
                RaiseCopyState();
            }
        }
    }

    /// <summary>
    /// The untruncated tool arguments exactly as the host recorded them. <see cref="ArgumentsJson"/> is a
    /// pretty-printed, size-capped view for the tool card; file-change extraction must see the full payload.
    /// </summary>
    public string RawArguments { get; private set; } = string.Empty;

    public bool HasArguments => !IsUser && !string.IsNullOrWhiteSpace(ArgumentsJson);

    public bool IsArgumentsExpanded
    {
        get => _isArgumentsExpanded;
        set
        {
            if (SetProperty(ref _isArgumentsExpanded, value))
            {
                OnPropertyChanged(nameof(ArgumentsToggleText));
            }
        }
    }

    public string ArgumentsToggleText => IsArgumentsExpanded ? "收起参数" : "参数";

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
            {
                Raise(nameof(HasResult), nameof(ResultPreview), nameof(HasLongResult), nameof(ResultToggleText));
                RaiseCopyState();
            }
        }
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    public bool HasLongResult => ResultText.Length > ToolPreviewLength;

    public bool IsResultExpanded
    {
        get => _isResultExpanded;
        set
        {
            if (SetProperty(ref _isResultExpanded, value))
            {
                Raise(nameof(ResultToggleText));
            }
        }
    }

    public string ResultToggleText => IsResultExpanded ? "收起结果" : "展开结果";

    public string ResultPreview
    {
        get
        {
            if (ResultText.Length <= ToolPreviewLength)
            {
                return ResultText;
            }

            return ResultText[..ToolPreviewLength] + "\n…";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(ShowFooter));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsError
    {
        get => _isError;
        set
        {
            if (SetProperty(ref _isError, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    /// <summary>"1.2s" while a tool call was running, empty for non-tool rows.</summary>
    public string DurationText { get; private set; } = string.Empty;

    public bool HasDuration => !string.IsNullOrEmpty(DurationText);

    public bool IsOptimistic { get; set; }

    /// <summary>True once the authoritative final text is known (the reveal pump may still be running).</summary>
    private bool IsFinal
    {
        get => _isFinal;
        set => _isFinal = value;
    }

    // ---- Factories -----------------------------------------------------------------------------

    public static ChatMessageViewModel CreateUser(string text, long time, bool optimistic = false, string? header = null)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.User)
        {
            Time = time,
            IsOptimistic = optimistic
        };
        message.SetStaticText(text);
        if (optimistic)
        {
            message.StatusText = "已发送，等待确认";
        }

        if (!string.IsNullOrWhiteSpace(header))
        {
            message._headerOverride = header;
        }

        return message;
    }

    public static ChatMessageViewModel CreateAssistant(string text, string? reasoning, long time, long? seq = null)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.Assistant) { Time = time, Seq = seq };
        message.SetStaticText(text);
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            message.ReasoningText = TrimReasoning(reasoning!);
        }

        return message;
    }

    public static ChatMessageViewModel CreateSystem(string text, long time, bool isError = false, long? seq = null)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.System) { Time = time, Seq = seq, IsError = isError };
        message.SetStaticText(text);
        return message;
    }

    public static ChatMessageViewModel CreateTool(string toolName, string? callId, string? argumentsJson, long time, long? seq = null)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.Tool)
        {
            Time = time,
            Seq = seq,
            CallId = callId,
            ToolName = toolName,
            StatusText = "执行中"
        };
        message.RawArguments = argumentsJson ?? string.Empty;
        message.ArgumentsJson = PrettyPrint(argumentsJson);
        return message;
    }

    /// <summary>Creates the hidden row that carries a <c>deliverables/presented</c> declaration.</summary>
    public static ChatMessageViewModel CreateArtifacts(
        IReadOnlyList<FileChangeViewModel> files,
        long time,
        int turn,
        long? seq = null)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.Artifact)
        {
            Time = time,
            Seq = seq,
            Turn = turn
        };
        foreach (var file in files)
        {
            message.Files.Add(file);
        }

        return message;
    }

    /// <summary>Creates an empty assistant row that will be filled by streamed deltas.</summary>
    public static ChatMessageViewModel CreateStreamingAssistant(int turn, int step, long time)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.Assistant)
        {
            Time = time,
            Turn = turn,
            Step = step,
            IsStreaming = true,
            IsFinal = false
        };
        message.ShowStreamingCaret = true;
        return message;
    }

    /// <summary>
    /// Creates the top-level「上下文压缩」card. <paramref name="seq"/> is the checkpoint event's seq, which is
    /// also the card's timeline position (the checkpoint replacement user message is not rendered itself).
    /// </summary>
    public static ChatMessageViewModel CreateCompaction(
        string compactionId,
        string? summary,
        int shadowedItemCount,
        int? shadowedTokenCount,
        bool manual,
        bool failed,
        long time,
        long? seq = null)
    {
        var message = new ChatMessageViewModel(ChatMessageKind.Compaction)
        {
            Time = time,
            Seq = seq,
            CompactionId = compactionId
        };
        message.ApplyCompactionState(summary, shadowedItemCount, shadowedTokenCount, manual, failed);
        return message;
    }

    // ---- Streaming / reveal --------------------------------------------------------------------

    public void AppendTextDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        _targetText += delta;
        OnPropertyChanged(nameof(TargetText));
        RaiseCopyState();
    }

    public void AppendReasoningDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta) || ReasoningText.Length >= MaxReasoningLength)
        {
            return;
        }

        var room = MaxReasoningLength - ReasoningText.Length;
        ReasoningText += delta.Length <= room ? delta : delta[..room];
    }

    /// <summary>Final text from <c>assistant/message</c>; authoritative over whatever was streamed.</summary>
    public void ApplyFinalText(string text, string? reasoning)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _targetText = text;
            OnPropertyChanged(nameof(TargetText));
            RaiseCopyState();
        }

        if (!string.IsNullOrWhiteSpace(reasoning) && !HasReasoning)
        {
            ReasoningText = TrimReasoning(reasoning!);
        }

        IsFinal = true;
        if (_targetText.Length == 0)
        {
            IsStreaming = false;
        }
    }

    /// <summary>Advances the reveal pump; returns true when any character became visible.</summary>
    public bool RevealStep(int characters)
    {
        if (!IsStreaming || _revealedChars >= _targetText.Length)
        {
            return false;
        }

        _revealedChars = Math.Min(_targetText.Length, _revealedChars + Math.Max(1, characters));
        Text = _targetText[.._revealedChars];
        return true;
    }

    public bool IsRevealComplete => _revealedChars >= _targetText.Length;

    /// <summary>Stops the reveal pump: shows everything and switches to Markdown when final.</summary>
    public void FinishReveal()
    {
        _revealedChars = _targetText.Length;
        if (_targetText.Length > 0)
        {
            Text = _targetText;
        }

        ShowStreamingCaret = false;
        IsStreaming = false;
    }

    /// <summary>True when the pump can retire this row (final and fully revealed).</summary>
    public bool CanRetireStreaming => IsStreaming && IsFinal && IsRevealComplete;

    // ---- Tool result ---------------------------------------------------------------------------

    public void ApplyToolResult(string output, long finishedAt, bool isError)
    {
        _hasResultApplied = true;
        ResultText = Truncate(output);
        IsError = isError;
        StatusText = isError ? "失败" : "完成";
        _finishedAt = finishedAt;
        if (_startedAt > 0 && finishedAt > _startedAt)
        {
            var seconds = (finishedAt - _startedAt) / 1000.0;
            DurationText = seconds < 10
                ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                : seconds.ToString("0", CultureInfo.InvariantCulture) + "s";
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(HasDuration));
        }
    }

    public void MarkStarted(long startedAt) => _startedAt = startedAt;

    public void MarkResultCompleted(long finishedAt, bool isError)
    {
        _hasResultApplied = true;
        StatusText = isError ? "失败" : "完成";
        IsError = isError;
        _finishedAt = finishedAt;
    }

    // ---- Merge helpers -------------------------------------------------------------------------

    public string TextOf() => TargetText;

    public void MarkSent()
    {
        IsOptimistic = false;
        StatusText = "已发送";
    }

    /// <summary>
    /// Promotes an optimistic prompt bubble to its durable <c>user/message</c> identity *in place*: the row
    /// keeps its position in the transcript (and therefore in <see cref="SessionViewModel.Timeline"/>), only
    /// the durable sequence/time and the delivery state change. Re-adding the durable row at the end instead
    /// would move the prompt below the running turn's fold.
    /// </summary>
    public void AdoptDurableUser(long seq, long time)
    {
        Seq = seq;
        Time = time;
        IsOptimistic = false;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(HasStatus));
    }

    /// <summary>Lets the session view model annotate delivery state (queued / no receipt yet).</summary>
    public void SetStatus(string? status) => StatusText = status ?? string.Empty;

    private void SetStaticText(string text)
    {
        _targetText = text ?? string.Empty;
        _revealedChars = _targetText.Length;
        _text = _targetText;
        IsStreaming = false;
        IsFinal = true;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HasText));
        RaiseCopyState();
    }

    private static string TrimReasoning(string value)
    {
        value = value.Trim();
        return value.Length <= MaxReasoningLength ? value : value[..MaxReasoningLength] + "\n…（已截断）";
    }

    public static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxToolPayloadLength
            ? value
            : value[..MaxToolPayloadLength] + $"\n…（已截断，共 {value.Length} 字符）";
    }

    private static string PrettyPrint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        var text = json.Trim();
        if (!(text.StartsWith('{') || text.StartsWith('[')))
        {
            return Truncate(text);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var pretty = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return Truncate(pretty);
        }
        catch (JsonException)
        {
            return Truncate(text);
        }
    }

    public static string KindLabel(ChatMessageKind kind) => kind switch
    {
        ChatMessageKind.User => "你",
        ChatMessageKind.Assistant => "助手",
        ChatMessageKind.Tool => "工具",
        ChatMessageKind.Compaction => "压缩",
        _ => "系统"
    };

    internal static string JoinLines(IEnumerable<string> lines) => new StringBuilder().AppendJoin("\n", lines).ToString();
}
