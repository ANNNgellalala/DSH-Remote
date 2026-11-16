using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DshRemote.App.Services;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>
/// One open conversation.
/// <para>
/// Preferred path: <see cref="ISessionFollowClient"/> — one <c>OpenSessionAsync</c> baseline snapshot, then a
/// gap-repairing <c>FollowEventsAsync</c> live stream. Fallback for older clients: history + observe.
/// Assistant rows are assembled from streamed attempt deltas (or the final message), revealed with a
/// character pump, then rendered as Markdown.
/// </para>
/// </summary>
public sealed class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDshClient _client;
    private readonly ISessionFollowClient? _follow;
    private readonly SessionSummary _summary;
    private readonly DispatcherTimer _revealTimer;
    private readonly DispatcherTimer _stateTimer;
    private readonly List<SessionEventRecord> _pending = new();
    private CancellationTokenSource _cts = new();
    private Task? _observeTask;
    private bool _historyLoaded;
    private long _firstPageMilliseconds;
    private bool _loadingStarted;
    private bool _disposed;
    private bool _isRunning;
    private bool _isLoading;
    private bool _isLoadingOlder;
    private bool _isSending;
    private bool _stopRequested;
    private bool _hasOlder;
    private bool _hasError;
    private bool _isReadOnlySession;
    private bool _isQueueExpanded;
    private bool _isSteerMode;
    private bool _refreshingState;
    private bool _hadConnectionLoss;
    private int _hiddenEvents;
    private int _pendingInputCount;
    private string _draft = string.Empty;
    private string _title;
    private string _errorMessage = string.Empty;
    private string _followStatusText = string.Empty;
    private long _lastSeq;
    private long _oldestSeq = long.MaxValue;
    private long? _beforeSeq;
    private ChatMessageViewModel? _liveAssistant;
    private int _currentTurn;
    private bool _isComposerExpanded;
    private readonly Dictionary<int, long> _turnEndTimes = new();
    private readonly Dictionary<int, ChatTurnViewModel> _timelineTurns = new();

    /// <summary>Retry budget for a broken live subscription (disconnect → up to 5 retries, 5 s apart).</summary>
    internal const int MaxFollowRetries = 5;

    /// <summary>Delay between two follow-subscription retry attempts.</summary>
    private static readonly TimeSpan FollowRetryInterval = TimeSpan.FromSeconds(5);

    private readonly object _followRetryGate = new();
    private DispatcherTimer? _followReconnectTimer;
    private int _followRetryCount;
    private int _followResyncStreak;
    private volatile bool _followGaveUp;

    /// <summary>
    /// Test seam: replaces <see cref="FollowRetryInterval"/> waits so retry-cap behaviour can be exercised
    /// without making the suite wait 25 s. Production code leaves this <c>null</c>.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? FollowRetryDelayOverride { get; set; }

    /// <summary>
    /// Suppresses the per-add timeline rebuild while a history page is merged in bulk, so the transcript is
    /// projected once against the complete set. Besides being much cheaper, it lets the fold's auto-expand
    /// policy see the real process size instead of a partial first row (which would expand a huge turn).
    /// </summary>
    private bool _suspendTimelineRebuild;

    /// <summary>
    /// Live session events are applied in small batches on a short timer instead of one by one. A running
    /// turn can deliver a burst (e.g. stream catch-up on open) whose per-event UI work (~50-100 ms each)
    /// would otherwise block the UI thread for seconds and trip the Android ANR watchdog.
    /// </summary>
    private readonly Queue<SessionEventRecord> _liveQueue = new();
    private DispatcherTimer? _liveFlushTimer;
    private const int LiveBatchMax = 128;
    private static readonly TimeSpan LiveFlushInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>Optimistic user bubbles waiting for their durable <c>user/message</c> echo.</summary>
    private readonly List<PendingUserPrompt> _pendingUserPrompts = new();
    private readonly DispatcherTimer _pendingPromptTimer;
    private static readonly TimeSpan PendingPromptTimeout = TimeSpan.FromSeconds(10);

    private readonly IImageClient? _imageClient;
    private readonly Dictionary<string, Bitmap?> _imageCache = new(StringComparer.Ordinal);
    private readonly SessionCacheStore? _cache;

    /// <summary>
    /// Shell-owned per-session draft store. The composer text is written through on every edit so
    /// switching chats (which builds a new view-model) never loses an unsent message.
    /// </summary>
    private readonly SessionDraftStore? _drafts;
    private readonly List<SessionEventRecord> _cacheRecords = new();
    private readonly DispatcherTimer _cacheTimer;
    private bool _cacheDirty;
    private long _cacheCursor;
    private bool _turnBoundaryPending;
    private ChatImagePartViewModel? _selectedImage;

    // ---- Session-log surface / compaction state ------------------------------------------------
    // Compaction state machine keyed by compactionId. It is fed by the log-only compaction/* events and by
    // the checkpoint replacement user/message; the human transcript keeps append events only.
    private readonly Dictionary<string, CompactionState> _compactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingToolResult> _pendingToolResults = new(StringComparer.Ordinal);
    private const int MaxPendingToolResults = 512;
    private bool _isCompacting;
    private int _surfaceReplacementCount;
    private int _prunedResultCount;
    private int _suppressedProjectionRows;

    // ---- Model / reasoning-effort picker state -------------------------------------------------
    private readonly IModelClient? _modelClient;
    private DshModelCatalogResult? _modelCatalogResult;
    private DshModelCatalog? _modelCatalog;
    private Task? _modelCatalogLoad;
    private bool _modelCatalogLoaded;
    private DshModelSelection? _modelSelection;
    private bool _isModelPickerOpen;
    private bool _isModelCatalogLoading;
    private bool _isModelSelecting;
    private string _modelCatalogError = string.Empty;
    private int _modelPickerLevel;
    private string? _pickerProviderId;
    private string? _pickerProviderName;
    private ModelDescriptorViewModel? _pickerDescriptor;
    private int _modelSelectionGeneration;
    private List<ModelProviderViewModel> _modelProviders = new();
    private List<ModelDescriptorViewModel> _modelModels = new();
    private List<ModelEffortViewModel> _modelEfforts = new();
    private List<string> _modelPickerWarnings = new();

    private sealed class PendingUserPrompt
    {
        public required ChatMessageViewModel Bubble { get; init; }

        public required string Text { get; init; }

        public DateTimeOffset SentAt { get; } = DateTimeOffset.Now;

        /// <summary>Client-minted request id from <c>SendPromptAsync</c> (echoed as <c>source.rpcId</c>).</summary>
        public string? RequestId { get; set; }

        public bool Matched { get; set; }
    }

    /// <summary>
    /// One context compaction observed in the session log. The summary arrives as a log-only event, the
    /// checkpoint position arrives as a replacement <c>user/message</c>; either may be missing when the
    /// loaded history window starts in the middle of a compaction.
    /// </summary>
    private sealed class CompactionState
    {
        public required string CompactionId { get; init; }

        public bool Manual { get; set; }

        public int? Turn { get; set; }

        public long? SummarySeq { get; set; }

        public string? SummaryText { get; set; }

        public int ShadowedItemCount { get; set; }

        public int? ShadowedTokenCount { get; set; }

        public long? CheckpointSeq { get; set; }

        public long CheckpointTime { get; set; }

        public string? Error { get; set; }

        public ChatMessageViewModel? Row { get; set; }
    }

    /// <summary>A <c>tool/result</c> whose <c>tool/call</c> has not been loaded yet (page split / live order).</summary>
    private readonly record struct PendingToolResult(string Output, long Time, bool IsError);

    public SessionViewModel(
        IDshClient client,
        SessionSummary summary,
        ICommand backCommand,
        InteractionHub hub,
        SettingsViewModel settings,
        SessionCacheStore? cache = null,
        SessionDraftStore? drafts = null)
    {
        _client = client;
        _follow = client as ISessionFollowClient;
        _imageClient = client as IImageClient;
        _modelClient = client as IModelClient;
        _cache = cache;
        _drafts = drafts;
        _summary = summary;
        _draft = drafts?.Get(summary.SessionId) ?? string.Empty;
        _modelSelection = summary.ModelSelection;
        Hub = hub;
        Settings = settings;
        _title = summary.Title ?? string.Empty;
        BackCommand = backCommand;
        SendCommand = new AsyncRelayCommand(SendAsync);
        StopCommand = new AsyncRelayCommand(StopAsync);
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync);
        ReconnectCommand = new AsyncRelayCommand(ReconnectFromChatAsync);
        ToggleQueueCommand = new RelayCommand(() => IsQueueExpanded = !IsQueueExpanded);
        PickImageCommand = new AsyncRelayCommand(PickImageAsync);
        CloseImageCommand = new RelayCommand(() => SelectedImage = null);
        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _revealTimer.Tick += (_, _) => PumpReveal();
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _stateTimer.Tick += (_, _) => _ = RefreshStateAsync();
        _pendingPromptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pendingPromptTimer.Tick += (_, _) => CheckPendingUserPrompts();
        _cacheTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _cacheTimer.Tick += (_, _) =>
        {
            SaveCacheIfDirty();
            _drafts?.Flush();
        };
        if (_cache is not null)
        {
            _cacheTimer.Start();
        }
        _client.StateChanged += OnClientStateChanged;
        Messages.CollectionChanged += (_, _) =>
        {
            if (!_suspendTimelineRebuild)
            {
                RebuildTimeline();
            }
        };
        CollapseComposerCommand = new RelayCommand(CollapseComposerFullscreen);
        OpenModelPickerCommand = new AsyncRelayCommand(OpenModelPickerAsync);
        CloseModelPickerCommand = new RelayCommand(CloseModelPicker);
        ModelPickerBackCommand = new RelayCommand(GoBackModelPickerLevel);
        RefreshModelCatalogCommand = new AsyncRelayCommand(() => EnsureModelCatalogAsync(forceRefresh: true));
    }

    public string SessionId => _summary.SessionId;

    public SessionSummary Summary => _summary;

    public InteractionHub Hub { get; }

    /// <summary>Global settings (composer mode etc.), shared with the shell.</summary>
    public SettingsViewModel Settings { get; }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    /// <summary>
    /// Transcript timeline: user/system rows render directly; assistant turns render as one fold containing
    /// reasoning + tool calls, with only the final answer visible in the main transcript.
    /// </summary>
    public ObservableCollection<object> Timeline { get; } = new();

    /// <summary>Full-screen composer editor (enabled by the global "输入时展开全屏" setting).</summary>
    public bool IsComposerExpanded
    {
        get => _isComposerExpanded;
        private set
        {
            if (SetProperty(ref _isComposerExpanded, value))
            {
                Raise(nameof(HasComposerDraft));
            }
        }
    }

    public bool HasComposerDraft => !string.IsNullOrWhiteSpace(Draft);

    public ICommand CollapseComposerCommand { get; }

    /// <summary>Opens the full-screen composer when the global setting enables it.</summary>
    public void OpenComposerFullscreen()
    {
        if (!Settings.ExpandComposerFullscreen || IsReadOnlySession || _disposed)
        {
            return;
        }

        IsComposerExpanded = true;
    }

    public void CollapseComposerFullscreen()
    {
        if (_isComposerExpanded)
        {
            IsComposerExpanded = false;
        }
    }

    public ObservableCollection<QueueItemViewModel> Queue { get; } = new();

    /// <summary>Images picked in the composer, waiting to be sent with the next prompt.</summary>
    public ObservableCollection<PendingImageViewModel> PendingImages { get; } = new();

    public bool HasPendingImages => PendingImages.Count > 0;

    /// <summary>Set by the view: platform picker on Android, StorageProvider file picker on desktop.</summary>
    public Func<CancellationToken, Task<DshImageAttachment?>>? ImagePicker { get; set; }

    public bool CanAttachImage => !IsReadOnlySession && !IsSending && !_disposed;

    public ICommand PickImageCommand { get; }

    public ICommand CloseImageCommand { get; }

    /// <summary>Image opened in the full-screen viewer overlay (null when closed).</summary>
    public ChatImagePartViewModel? SelectedImage
    {
        get => _selectedImage;
        private set
        {
            if (SetProperty(ref _selectedImage, value))
            {
                Raise(nameof(HasSelectedImage));
            }
        }
    }

    public bool HasSelectedImage => _selectedImage is not null;

    /// <summary>
    /// Opens the full-screen image viewer on an image that is not part of the transcript (used by the
    /// long-screenshot result banner to preview the generated PNG).
    /// </summary>
    public void ShowImage(ChatImagePartViewModel image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_disposed)
        {
            return;
        }

        SelectedImage = image;
    }

    public ICommand BackCommand { get; }

    public ICommand SendCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand LoadOlderCommand { get; }

    /// <summary>
    /// Quick user escape hatch from a dead chat: jumps straight to the connection page instead of retrying
    /// the broken subscription in place. Raised through <see cref="FollowAbandoned"/> so the shell can
    /// close this session, drop the dead client and show the connect page.
    /// </summary>
    public ICommand ReconnectCommand { get; }

    public ICommand ToggleQueueCommand { get; }

    public bool IsFollowMode => _follow is not null;

    /// <summary>
    /// Raised once when this chat gives up on its live subscription: either the retry budget
    /// (<see cref="MaxFollowRetries"/> × <see cref="FollowRetryInterval"/>) is exhausted, or the user tapped
    /// 重新连接. The shell responds by returning to the connection page.
    /// </summary>
    public event EventHandler? FollowAbandoned;

    /// <summary>True once this chat stopped trying to reconnect its live subscription.</summary>
    public bool IsFollowAbandoned => _followGaveUp;

    /// <summary>Retries spent since the last healthy stream (test/diagnostic seam).</summary>
    internal int FollowRetryCount
    {
        get
        {
            lock (_followRetryGate)
            {
                return _followRetryCount;
            }
        }
    }

    /// <summary>Raised after any transcript mutation (including in-place updates) for auto-scroll.</summary>
    public event EventHandler? TranscriptUpdated;

    /// <summary>
    /// Raised after the host accepted a prompt for this session. The shell uses it to retire the
    /// "blank/new session" state immediately, before the next list refresh reports it.
    /// </summary>
    public event EventHandler? PromptSent;

    // ---- Header -------------------------------------------------------------------------------

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? "(未命名会话)" : _title;
        private set
        {
            _title = value;
            OnPropertyChanged();
        }
    }

    public bool HasCwd => !string.IsNullOrWhiteSpace(_summary.Cwd);

    public string CwdText => _summary.Cwd ?? string.Empty;

    public bool HasModel => !string.IsNullOrWhiteSpace(_summary.Model);

    public string ModelText => _summary.Model ?? string.Empty;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseState();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        // Internal (rather than private) so the App test suite can drive the runtime state machine without a
        // running Avalonia dispatcher loop; the UI only ever reads this property.
        internal set
        {
            if (SetProperty(ref _isRunning, value))
            {
                if (value)
                {
                    // Switching the model is an idle-only action in this App: as soon as a turn starts the
                    // chip is disabled and any open panel is dismissed.
                    CloseModelPicker();
                }

                DiagnosticsLog.Trace($"session {SessionId}: running={value}");
                RaiseState();
                Raise(nameof(CanOpenModelPicker), nameof(ModelChipHint));
            }
        }
    }

    public bool IsIdle => !IsRunning;

    /// <summary>
    /// True between <c>compaction/start</c> and <c>compaction/end</c>. The transcript stays unchanged during a
    /// compaction (replacements are model-side); the chat page shows a small "正在压缩上下文…" status.
    /// </summary>
    public bool IsCompacting
    {
        get => _isCompacting;
        private set => SetProperty(ref _isCompacting, value);
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                Raise(nameof(CanSend), nameof(CanStop), nameof(StatusText), nameof(ShowSessionStatusRow));
            }
        }
    }

    public bool StopRequested
    {
        get => _stopRequested;
        private set
        {
            if (SetProperty(ref _stopRequested, value))
            {
                Raise(nameof(StatusText), nameof(CanStop), nameof(ShowSessionStatusRow));
            }
        }
    }

    public bool IsLoadingOlder
    {
        get => _isLoadingOlder;
        private set => SetProperty(ref _isLoadingOlder, value);
    }

    public bool HasOlder
    {
        get => _hasOlder;
        private set
        {
            if (SetProperty(ref _hasOlder, value))
            {
                Raise(nameof(ShowBeginning), nameof(HasOlder));
            }
        }
    }

    /// <summary>"已到会话开头" caption when the oldest page is loaded.</summary>
    public bool ShowBeginning => _historyLoaded && !HasOlder && Messages.Count > 0;

    /// <summary>Time from opening the session to the first history page being visible (debug metric).</summary>
    public long FirstPageMilliseconds
    {
        get => _firstPageMilliseconds;
        private set
        {
            if (SetProperty(ref _firstPageMilliseconds, value))
            {
                OnPropertyChanged(nameof(FirstPageText));
                OnPropertyChanged(nameof(HasFirstPageText));
            }
        }
    }

    public string FirstPageText => FirstPageMilliseconds > 0
        ? $"首屏 {FirstPageMilliseconds} ms"
        : string.Empty;

    public bool HasFirstPageText => FirstPageMilliseconds > 0;

    /// <summary>Raised after an older page is prepended (argument: number of inserted rows).</summary>
    public event EventHandler<int>? OlderPageInserted;

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                HasError = !string.IsNullOrWhiteSpace(value);
            }
        }
    }

    public string FollowStatusText
    {
        get => _followStatusText;
        private set
        {
            if (SetProperty(ref _followStatusText, value))
            {
                OnPropertyChanged(nameof(HasFollowStatus));
            }
        }
    }

    public bool HasFollowStatus => !string.IsNullOrWhiteSpace(FollowStatusText);

    public bool IsReadOnlySession
    {
        get => _isReadOnlySession;
        private set
        {
            if (SetProperty(ref _isReadOnlySession, value))
            {
                if (value)
                {
                    CloseModelPicker();
                }

                Raise(nameof(CanEditSession), nameof(CanSend), nameof(CanStop),
                    nameof(CanOpenModelPicker), nameof(ModelChipHint));
            }
        }
    }

    public bool CanEditSession => !IsReadOnlySession;

    // ---- Model / reasoning-effort picker -------------------------------------------------------

    /// <summary>
    /// Compact header chip: "&lt;model name&gt; · &lt;effort name&gt;". Falls back to the raw model id while the
    /// catalog is unknown, to the catalog default when this session has no selection yet (display only —
    /// never written back to the desktop), and finally to "选择模型".
    /// </summary>
    public string ModelChipText
    {
        get
        {
            var selection = DisplaySelection;
            if (selection is null || string.IsNullOrWhiteSpace(selection.Model))
            {
                var rawModel = _summary.Model;
                if (string.IsNullOrWhiteSpace(rawModel))
                {
                    return "选择模型";
                }

                return ResolveModelName(providerId: null, rawModel) ?? rawModel!;
            }

            var name = ResolveModelName(selection.Provider, selection.Model) ?? selection.Model;
            var effort = ResolveEffortName(selection);
            return string.IsNullOrWhiteSpace(effort) ? name : name + " · " + effort;
        }
    }

    /// <summary>
    /// Model line of the header chip (never includes the reasoning effort). Falls back to the raw model id
    /// while the catalog is unknown and to "选择模型" before any selection exists.
    /// </summary>
    public string ModelChipModelName
    {
        get
        {
            var selection = DisplaySelection;
            if (selection is not null && !string.IsNullOrWhiteSpace(selection.Model))
            {
                return ResolveModelName(selection.Provider, selection.Model) ?? selection.Model;
            }

            var rawModel = _summary.Model;
            return string.IsNullOrWhiteSpace(rawModel)
                ? "选择模型"
                : ResolveModelName(providerId: null, rawModel) ?? rawModel!;
        }
    }

    /// <summary>Reasoning-effort line of the header chip; empty when the model has no effort.</summary>
    public string ModelChipEffortName
    {
        get
        {
            var selection = DisplaySelection;
            return selection is null ? string.Empty : ResolveEffortName(selection) ?? string.Empty;
        }
    }

    public bool HasModelChipEffort => !string.IsNullOrWhiteSpace(ModelChipEffortName);

    /// <summary>
    /// True when the desktop exposes <c>session/modelCatalog</c> + <c>session/selectModel</c>. Before the first
    /// probe this is optimistic (the client exists); an explicitly reported <c>Unsupported</c> hides the entry.
    /// </summary>
    public bool IsModelPickerAvailable => _modelClient is not null &&
                                          _modelCatalogResult?.Support != DshModelCatalogSupport.Unsupported;

    public bool CanOpenModelPicker => !_disposed && IsModelPickerAvailable && !IsRunning &&
                                      !IsReadOnlySession && !IsModelSelecting;

    /// <summary>Why the chip cannot open the picker (empty when it can).</summary>
    public string ModelChipHint
    {
        get
        {
            if (!IsModelPickerAvailable)
            {
                return "当前桌面 DSH 不支持切换模型";
            }

            return IsRunning ? "运行中不可切换模型" : string.Empty;
        }
    }

    /// <summary>
    /// Two-way bound to the picker popup. Setting it (from XAML or the commands) opens/closes the cascade;
    /// programmatic closes (successful selection, turn start, read-only session) set it back to false.
    /// </summary>
    public bool IsModelPickerOpen
    {
        get => _isModelPickerOpen;
        set
        {
            if (value)
            {
                if (_isModelPickerOpen || !CanOpenModelPicker)
                {
                    return;
                }

                _isModelPickerOpen = true;
                // A fresh open starts clean: a selection error from the previous attempt must not linger.
                ModelCatalogError = string.Empty;
                ResetModelPickerNavigation();
                OnPropertyChanged();
            }
            else
            {
                CloseModelPicker();
            }
        }
    }

    public bool IsModelCatalogLoading
    {
        get => _isModelCatalogLoading;
        private set => SetProperty(ref _isModelCatalogLoading, value);
    }

    public string ModelCatalogError
    {
        get => _modelCatalogError;
        private set
        {
            if (SetProperty(ref _modelCatalogError, value))
            {
                Raise(nameof(HasModelCatalogError));
            }
        }
    }

    public bool HasModelCatalogError => !string.IsNullOrWhiteSpace(ModelCatalogError);

    /// <summary>True while <c>session/selectModel</c> is in flight (rows and the chip stay disabled).</summary>
    public bool IsModelSelecting
    {
        get => _isModelSelecting;
        private set
        {
            if (SetProperty(ref _isModelSelecting, value))
            {
                Raise(nameof(CanOpenModelPicker));
            }
        }
    }

    /// <summary>0 = provider level, 1 = model level, 2 = reasoning-effort level.</summary>
    public int ModelPickerLevel => _modelPickerLevel;

    public bool IsModelProviderLevel => _modelPickerLevel == 0;

    public bool IsModelLevel => _modelPickerLevel == 1;

    public bool IsModelEffortLevel => _modelPickerLevel == 2;

    public bool CanGoBackModelPicker => _modelPickerLevel > 0;

    public string ModelPickerTitle => _modelPickerLevel switch
    {
        0 => "选择模型",
        2 => "选择推理等级",
        _ => _pickerProviderName ?? "选择模型"
    };

    public IReadOnlyList<ModelProviderViewModel> ModelProviders => _modelProviders;

    public IReadOnlyList<ModelDescriptorViewModel> ModelModels => _modelModels;

    public IReadOnlyList<ModelEffortViewModel> ModelEfforts => _modelEfforts;

    /// <summary>Providers that failed to load in the desktop catalog ("&lt;provider&gt; 加载失败：&lt;message&gt;").</summary>
    public IReadOnlyList<string> ModelPickerWarnings => _modelPickerWarnings;

    public ICommand OpenModelPickerCommand { get; }

    public ICommand CloseModelPickerCommand { get; }

    public ICommand ModelPickerBackCommand { get; }

    public ICommand RefreshModelCatalogCommand { get; }

    /// <summary>Current session selection; falls back to the catalog default for display only.</summary>
    private DshModelSelection? DisplaySelection
    {
        get
        {
            if (_modelSelection is not null)
            {
                return _modelSelection;
            }

            var fallback = _modelCatalog?.Default;
            return fallback is null || string.IsNullOrWhiteSpace(fallback.Model) ? null : fallback;
        }
    }

    internal async Task OpenModelPickerAsync()
    {
        if (_disposed || !CanOpenModelPicker)
        {
            return;
        }

        if (!_isModelPickerOpen)
        {
            IsModelPickerOpen = true;
            DiagnosticsLog.Trace($"session {SessionId}: model picker open");
        }

        await EnsureModelCatalogAsync().ConfigureAwait(true);
    }

    internal void CloseModelPicker()
    {
        if (!_isModelPickerOpen)
        {
            return;
        }

        _isModelPickerOpen = false;
        ResetModelPickerNavigation();
        DiagnosticsLog.Trace($"session {SessionId}: model picker close");
        OnPropertyChanged(nameof(IsModelPickerOpen));
    }

    internal void GoBackModelPickerLevel()
    {
        if (_disposed || _modelPickerLevel <= 0)
        {
            return;
        }

        _modelPickerLevel--;
        if (_modelPickerLevel <= 0)
        {
            _pickerProviderId = null;
            _pickerProviderName = null;
            _pickerDescriptor = null;
            _modelModels = new List<ModelDescriptorViewModel>();
            _modelEfforts = new List<ModelEffortViewModel>();
            Raise(nameof(ModelModels), nameof(ModelEfforts));
        }
        else
        {
            _pickerDescriptor = null;
            _modelEfforts = new List<ModelEffortViewModel>();
            BuildModelList();
            Raise(nameof(ModelEfforts));
        }

        RaiseModelPickerNavigation();
    }

    /// <summary>Loads the desktop catalog once (per connection generation); a retry is allowed after failure.</summary>
    internal Task EnsureModelCatalogAsync(bool forceRefresh = false)
    {
        if (_disposed || _modelClient is null)
        {
            return Task.CompletedTask;
        }

        if (_modelCatalogLoad is { IsCompleted: false })
        {
            return _modelCatalogLoad;
        }

        if (!forceRefresh &&
            (_modelCatalogLoaded || _modelCatalogResult?.Support == DshModelCatalogSupport.Unsupported))
        {
            return Task.CompletedTask;
        }

        _modelCatalogLoad = LoadModelCatalogAsync(forceRefresh);
        return _modelCatalogLoad;
    }

    private async Task LoadModelCatalogAsync(bool forceRefresh)
    {
        IsModelCatalogLoading = true;
        ModelCatalogError = string.Empty;
        try
        {
            var result = await _modelClient!.GetModelCatalogAsync(forceRefresh, _cts.Token).ConfigureAwait(true);
            ApplyModelCatalogResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _modelCatalogLoaded = false;
            ModelCatalogError = "模型目录加载失败：" + Describe(ex);
        }
        finally
        {
            IsModelCatalogLoading = false;
        }
    }

    private void ApplyModelCatalogResult(DshModelCatalogResult result)
    {
        _modelCatalogResult = result;
        _modelCatalog = result.Catalog;

        if (result.Support == DshModelCatalogSupport.Unsupported)
        {
            // Old desktop: hide the entry silently (no error surfaced to the user).
            _modelCatalog = null;
            _modelCatalogLoaded = true;
            ModelCatalogError = string.Empty;
            _modelPickerWarnings = new List<string>();
            BuildProviderList();
            CloseModelPicker();
            Raise(nameof(ModelChipText), nameof(ModelChipModelName), nameof(ModelChipEffortName), nameof(HasModelChipEffort), nameof(IsModelPickerAvailable), nameof(CanOpenModelPicker),
                nameof(ModelChipHint), nameof(ModelPickerWarnings));
            return;
        }

        if (result.Support == DshModelCatalogSupport.Unavailable)
        {
            _modelCatalogLoaded = false;
            BuildProviderList();
            BuildModelPickerWarnings();
            ModelCatalogError = string.IsNullOrWhiteSpace(result.Message)
                ? "模型目录加载失败，请重试。"
                : "模型目录加载失败：" + result.Message;
        }
        else
        {
            _modelCatalogLoaded = true;
            ModelCatalogError = string.Empty;
            BuildProviderList();
            BuildModelPickerWarnings();
        }

        RefreshPickerAfterCatalogChange();
        Raise(nameof(ModelChipText), nameof(ModelChipModelName), nameof(ModelChipEffortName), nameof(HasModelChipEffort), nameof(IsModelPickerAvailable), nameof(CanOpenModelPicker),
            nameof(ModelChipHint), nameof(ModelPickerWarnings));
    }

    private void RefreshPickerAfterCatalogChange()
    {
        if (!_isModelPickerOpen)
        {
            return;
        }

        switch (_modelPickerLevel)
        {
            case 1:
                if (FindGroup(_pickerProviderId) is null)
                {
                    ResetModelPickerNavigation();
                }
                else
                {
                    BuildModelList();
                }

                break;
            case 2:
                BuildEffortList();
                break;
        }
    }

    private void ResetModelPickerNavigation()
    {
        _modelPickerLevel = 0;
        _pickerProviderId = null;
        _pickerProviderName = null;
        _pickerDescriptor = null;
        _modelModels = new List<ModelDescriptorViewModel>();
        _modelEfforts = new List<ModelEffortViewModel>();
        Raise(nameof(ModelModels), nameof(ModelEfforts));
        RaiseModelPickerNavigation();
    }

    private void RaiseModelPickerNavigation()
    {
        Raise(
            nameof(ModelPickerLevel),
            nameof(IsModelProviderLevel),
            nameof(IsModelLevel),
            nameof(IsModelEffortLevel),
            nameof(ModelPickerTitle),
            nameof(CanGoBackModelPicker));
    }

    private void BuildProviderList()
    {
        var providers = new List<ModelProviderViewModel>();
        var groups = _modelCatalog?.Groups;
        if (groups is not null)
        {
            foreach (var group in groups)
            {
                var models = group.Models;
                if (models is null || models.Count == 0)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(group.Name) ? group.Id : group.Name;
                var detail = models.Count == 1 ? "1 个模型" : models.Count + " 个模型";
                providers.Add(new ModelProviderViewModel(group.Id, name, detail, SelectProviderRow));
            }
        }

        _modelProviders = providers;
        OnPropertyChanged(nameof(ModelProviders));
    }

    private void BuildModelList()
    {
        var models = new List<ModelDescriptorViewModel>();
        var group = FindGroup(_pickerProviderId);
        if (group?.Models is not null)
        {
            foreach (var descriptor in group.Models)
            {
                var name = string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.Id : descriptor.Name;
                var badge = descriptor.Badge?.Text ?? string.Empty;
                var detail = string.IsNullOrWhiteSpace(descriptor.Description) ? badge : descriptor.Description!;
                models.Add(new ModelDescriptorViewModel(
                    group.Id,
                    descriptor.Id,
                    name,
                    detail,
                    descriptor.Reasoning is not null,
                    BuildEfforts(group.Id, descriptor),
                    IsSelectedModel(group.Id, descriptor.Id),
                    SelectDescriptorRowAsync));
            }
        }

        _modelModels = models;
        OnPropertyChanged(nameof(ModelModels));
    }

    private void BuildEffortList()
    {
        var coreDescriptor = _pickerDescriptor is null
            ? null
            : FindDescriptor(_pickerDescriptor.ProviderId, _pickerDescriptor.Id);
        _modelEfforts = coreDescriptor is null || _pickerDescriptor is null
            ? new List<ModelEffortViewModel>()
            : BuildEfforts(_pickerDescriptor.ProviderId, coreDescriptor).ToList();
        OnPropertyChanged(nameof(ModelEfforts));
    }

    private IReadOnlyList<ModelEffortViewModel> BuildEfforts(string providerId, DshModelDescriptor descriptor)
    {
        var reasoning = descriptor.Reasoning;
        if (reasoning is null)
        {
            return Array.Empty<ModelEffortViewModel>();
        }

        var efforts = new List<ModelEffortViewModel>();
        if (string.IsNullOrWhiteSpace(reasoning.DefaultEffort))
        {
            // No provider default: offer an explicit "default" row that omits reasoningEffort entirely.
            efforts.Add(new ModelEffortViewModel(
                null,
                "默认（由提供方决定）",
                "不指定推理等级，由提供方自行决定。",
                IsSelectedEffort(providerId, descriptor, effortId: null),
                SelectEffortRowAsync));
        }

        var catalogEfforts = reasoning.Efforts;
        if (catalogEfforts is not null)
        {
            foreach (var effort in catalogEfforts)
            {
                var name = string.IsNullOrWhiteSpace(effort.Name) ? effort.Id : effort.Name;
                efforts.Add(new ModelEffortViewModel(
                    effort.Id,
                    name,
                    effort.Description ?? string.Empty,
                    IsSelectedEffort(providerId, descriptor, effort.Id),
                    SelectEffortRowAsync));
            }
        }

        return efforts;
    }

    private void BuildModelPickerWarnings()
    {
        var warnings = new List<string>();
        var failures = _modelCatalog?.Failures;
        if (failures is not null)
        {
            foreach (var failure in failures)
            {
                var name = string.IsNullOrWhiteSpace(failure.Name) ? failure.Id : failure.Name;
                var message = string.IsNullOrWhiteSpace(failure.Message) ? "未知错误" : failure.Message;
                warnings.Add(name + " 加载失败：" + message);
            }
        }

        _modelPickerWarnings = warnings;
        OnPropertyChanged(nameof(ModelPickerWarnings));
    }

    /// <summary>Level 0 → level 1: remember the provider and render its models.</summary>
    internal void SelectProviderRow(ModelProviderViewModel provider)
    {
        if (_disposed || provider is null)
        {
            return;
        }

        _pickerProviderId = provider.Id;
        _pickerProviderName = provider.Name;
        _pickerDescriptor = null;
        _modelPickerLevel = 1;
        BuildModelList();
        RaiseModelPickerNavigation();
    }

    /// <summary>Level 1 → level 2 for reasoning models; otherwise commits the model immediately.</summary>
    internal async Task SelectDescriptorRowAsync(ModelDescriptorViewModel descriptor)
    {
        if (_disposed || descriptor is null)
        {
            return;
        }

        if (descriptor.IsDrillable)
        {
            _pickerDescriptor = descriptor;
            _modelPickerLevel = 2;
            BuildEffortList();
            RaiseModelPickerNavigation();
            return;
        }

        await ApplySelectionAsync(descriptor.ProviderId, descriptor.Id, reasoningEffort: null).ConfigureAwait(true);
    }

    /// <summary>Level 2 → commits the effort (or the implicit default when <c>Id</c> is null).</summary>
    internal async Task SelectEffortRowAsync(ModelEffortViewModel effort)
    {
        if (_disposed || effort is null || _pickerDescriptor is null)
        {
            return;
        }

        await ApplySelectionAsync(_pickerDescriptor.ProviderId, _pickerDescriptor.Id, effort.Id).ConfigureAwait(true);
    }

    /// <summary>
    /// Applies one selection through <c>session/selectModel</c>. Latest operation wins: an older response that
    /// arrives after a newer submission is discarded instead of overwriting it. Failures keep the previous
    /// selection and surface a Chinese message in <see cref="ModelCatalogError"/>.
    /// </summary>
    internal async Task ApplySelectionAsync(string provider, string model, string? reasoningEffort)
    {
        if (_disposed || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (_modelClient is null)
        {
            ModelCatalogError = "当前桌面 DSH 不支持切换模型。";
            return;
        }

        var generation = ++_modelSelectionGeneration;
        IsModelSelecting = true;
        ModelCatalogError = string.Empty;
        try
        {
            var selected = await _modelClient
                .SelectModelAsync(SessionId, provider, model, reasoningEffort, _cts.Token)
                .ConfigureAwait(true);
            if (generation != _modelSelectionGeneration || _disposed)
            {
                return;
            }

            ApplyModelSelection(selected);
            CloseModelPicker();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _modelSelectionGeneration || _disposed)
            {
                return;
            }

            ModelCatalogError = MapModelSelectionError(ex);
        }
        finally
        {
            if (generation == _modelSelectionGeneration)
            {
                IsModelSelecting = false;
            }
        }
    }

    /// <summary>Replaces the displayed selection (selected response, durable event or follow baseline).</summary>
    internal void ApplyModelSelection(DshModelSelection? selection)
    {
        _modelSelection = selection;
        RefreshPickerSelectionMarks();
        Raise(nameof(ModelChipText), nameof(ModelChipModelName), nameof(ModelChipEffortName), nameof(HasModelChipEffort));
    }

    /// <summary>Follows a durable <c>model/selection</c> event (data: <c>{provider,model,reasoningEffort?}</c>).</summary>
    internal void ApplyModelSelectionEvent(SessionEventRecord record)
    {
        var data = record.Data;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var provider = ReadString(data, "provider");
        var model = ReadString(data, "model");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var effort = ReadString(data, "reasoningEffort");
        ApplyModelSelection(new DshModelSelection(
            provider!,
            model!,
            string.IsNullOrWhiteSpace(effort) ? null : effort));
    }

    /// <summary>Calibrates the selection from a follow snapshot's <c>projections.modelSelection.next</c>.</summary>
    internal void ApplyModelSelectionBaseline(DshModelSelection? selection)
    {
        if (selection is null)
        {
            return;
        }

        ApplyModelSelection(selection);
    }

    private void RefreshPickerSelectionMarks()
    {
        if (!_isModelPickerOpen)
        {
            return;
        }

        switch (_modelPickerLevel)
        {
            case 1:
                BuildModelList();
                break;
            case 2:
                BuildEffortList();
                break;
        }
    }

    private bool IsSelectedModel(string providerId, string modelId)
    {
        var selection = DisplaySelection;
        return selection is not null &&
               string.Equals(selection.Provider, providerId, StringComparison.Ordinal) &&
               string.Equals(selection.Model, modelId, StringComparison.Ordinal);
    }

    private bool IsSelectedEffort(string providerId, DshModelDescriptor descriptor, string? effortId)
    {
        var selection = DisplaySelection;
        if (selection is null ||
            !string.Equals(selection.Provider, providerId, StringComparison.Ordinal) ||
            !string.Equals(selection.Model, descriptor.Id, StringComparison.Ordinal))
        {
            return false;
        }

        var effective = selection.ReasoningEffort;
        if (string.IsNullOrWhiteSpace(effective))
        {
            effective = descriptor.Reasoning?.DefaultEffort;
        }

        return string.Equals(effective ?? string.Empty, effortId ?? string.Empty, StringComparison.Ordinal);
    }

    private DshModelGroup? FindGroup(string? providerId)
    {
        var groups = _modelCatalog?.Groups;
        if (groups is null || string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        foreach (var group in groups)
        {
            if (string.Equals(group.Id, providerId, StringComparison.Ordinal))
            {
                return group;
            }
        }

        return null;
    }

    private DshModelDescriptor? FindDescriptor(string? providerId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var groups = _modelCatalog?.Groups;
        if (groups is null)
        {
            return null;
        }

        foreach (var group in groups)
        {
            if (!string.IsNullOrWhiteSpace(providerId) &&
                !string.Equals(group.Id, providerId, StringComparison.Ordinal))
            {
                continue;
            }

            var models = group.Models;
            if (models is null)
            {
                continue;
            }

            foreach (var model in models)
            {
                if (string.Equals(model.Id, modelId, StringComparison.Ordinal))
                {
                    return model;
                }
            }
        }

        return null;
    }

    private string? ResolveModelName(string? providerId, string? modelId)
    {
        var descriptor = FindDescriptor(providerId, modelId);
        if (descriptor is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.Id : descriptor.Name;
    }

    /// <summary>
    /// Effort label for the chip: explicit effort → catalog name, else the model's <c>defaultEffort</c>,
    /// else nothing ("默认" is only shown while a model is known to have efforts).
    /// </summary>
    private string? ResolveEffortName(DshModelSelection selection)
    {
        var descriptor = FindDescriptor(selection.Provider, selection.Model);
        var effortId = selection.ReasoningEffort;
        if (string.IsNullOrWhiteSpace(effortId))
        {
            effortId = descriptor?.Reasoning?.DefaultEffort;
        }

        if (string.IsNullOrWhiteSpace(effortId))
        {
            return null;
        }

        var efforts = descriptor?.Reasoning?.Efforts;
        if (efforts is not null)
        {
            foreach (var effort in efforts)
            {
                if (string.Equals(effort.Id, effortId, StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(effort.Name) ? effort.Id : effort.Name;
                }
            }
        }

        return effortId;
    }

    /// <summary>Maps the desktop's model-selection errors onto the short Chinese strings the panel shows.</summary>
    internal static string MapModelSelectionError(Exception ex)
    {
        if (ex is DshRemoteException remote)
        {
            switch (remote.Code)
            {
                case "session/model-unavailable":
                    return "该模型当前不可用，请重新选择其他模型。";
                case "session/writer-held":
                    return "当前会话被其他 DSH 进程占用，请先退出其他 DSH 再试。";
                case "gateway/endpoint-unavailable":
                    return "当前桌面 DSH 版本不支持切换模型。";
            }

            if (remote.IsTransportFailure)
            {
                return "连接已断开，请重新连接桌面后再试。";
            }

            return "切换模型失败：" + remote.Message;
        }

        return ex switch
        {
            TimeoutException => "切换模型超时，请稍后重试。",
            HttpRequestException => "连接已断开，请重新连接桌面后再试。",
            _ => "切换模型失败：" + ex.Message
        };
    }

    /// <summary>Reloads the catalog after a reconnect (the desktop cache is keyed by connection generation).</summary>
    private async Task ReloadModelCatalogAfterReconnectAsync()
    {
        if (_disposed || _modelClient is null || !_modelCatalogLoaded)
        {
            return;
        }

        await EnsureModelCatalogAsync(forceRefresh: true).ConfigureAwait(true);
    }

    // ---- Queue / runtime state -----------------------------------------------------------------

    public bool HasQueue => Queue.Count > 0;

    public string QueueHeaderText => $"队列 {Queue.Count}";

    public bool IsQueueExpanded
    {
        get => _isQueueExpanded;
        private set => SetProperty(ref _isQueueExpanded, value);
    }

    public int PendingInputCount
    {
        get => _pendingInputCount;
        private set
        {
            if (SetProperty(ref _pendingInputCount, value))
            {
                Raise(nameof(HasPendingInput), nameof(PendingInputText));
            }
        }
    }

    public bool HasPendingInput => PendingInputCount > 0;

    public string PendingInputText => $"待输入 {PendingInputCount}";

    public bool IsSteerMode
    {
        get => _isSteerMode;
        set
        {
            if (SetProperty(ref _isSteerMode, value))
            {
                Raise(nameof(CanSend), nameof(SendButtonText), nameof(ComposerHint));
            }
        }
    }

    public bool CanSteer => _follow is not null;

    /// <summary>The composer switches to steer mode while a turn is running.</summary>
    public bool ShowSteerToggle => IsFollowMode;

    public string SendButtonText => IsSteerMode && IsRunning ? "插话" : "发送";

    public string ComposerHint => IsSteerMode && IsRunning
        ? "插话给正在运行的 Agent…（Enter 发送，Shift+Enter 换行）"
        : "发消息… (Enter 发送, Shift+Enter 换行)";

    /// <summary>
    /// Transitional status text only. "空闲/运行中" is deliberately not shown: the transcript itself makes the
    /// running state obvious, and the header no longer carries a state badge.
    /// </summary>
    public string StatusText => IsLoading
        ? "加载中…"
        : IsSending
            ? "发送中…"
            : StopRequested
                ? "正在停止…"
                : string.Empty;

    public bool CanSend => !IsReadOnlySession && !IsSending && !_disposed &&
                           (!string.IsNullOrWhiteSpace(Draft) || HasPendingImages);

    public bool CanStop => !IsReadOnlySession && !_disposed && (IsRunning || IsSending || StopRequested);

    public bool ShowSessionStatusRow => IsRunning || IsSending || StopRequested || HasQueue || HasPendingInput;

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value))
            {
                // Write through immediately: the view-model dies on the next chat switch, and the store is
                // what lets the composer come back with the same text (see SessionDraftStore).
                _drafts?.Set(SessionId, value);
                Raise(nameof(CanSend), nameof(HasComposerDraft));
            }
        }
    }

    public bool ShowEmptyMessages => Messages.Count == 0 && !IsLoading;

    public bool IsOpening => IsLoading && Messages.Count == 0;

    // ---- Load ---------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        if (_loadingStarted || _disposed)
        {
            return;
        }

        _loadingStarted = true;
        var loadStarted = Stopwatch.GetTimestamp();
        DiagnosticsLog.Trace($"session {SessionId}: load start");
        await ReloadCoreAsync().ConfigureAwait(true);
        DiagnosticsLog.Trace(
            $"session {SessionId}: load done ms={Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds:F0} messages={Messages.Count}");

        // Warm the host model catalog in the background so the header chip shows display names before
        // the user opens the picker (the catalog is host-level and cached per connection generation).
        if (!_disposed && IsModelPickerAvailable)
        {
            _ = EnsureModelCatalogAsync();
        }
    }

    private Task ReconnectFromChatAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        AbandonFollow(string.Empty, showError: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the live-subscription retry loop for good and tells the shell to return to the connection page.
    /// Idempotent: only the first caller raises <see cref="FollowAbandoned"/>.
    /// </summary>
    private void AbandonFollow(string reason, bool showError)
    {
        if (_disposed)
        {
            return;
        }

        bool first;
        lock (_followRetryGate)
        {
            first = !_followGaveUp;
            if (first)
            {
                _followGaveUp = true;
            }
        }

        if (!first)
        {
            return;
        }

        PostIfAlive(() =>
        {
            StopFollowReconnectWatch();
            OnPropertyChanged(nameof(IsFollowAbandoned));
            if (showError)
            {
                ErrorMessage = reason;
            }
            else
            {
                ErrorMessage = string.Empty;
            }

            SetFollowStatus("实时订阅已断开，已停止重试。", clearAfterMs: 60000);
        });

        try
        {
            FollowAbandoned?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Session] {SessionId}: FollowAbandoned handler failed: {ex.Message}");
        }
    }

    /// <summary>UI-thread post that is dropped once the view-model has been disposed.</summary>
    private void PostIfAlive(Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

    private async Task ReloadCoreAsync()
    {
        // Tear down a previous generation (retry / resubscribe) without leaking the enumeration.
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_observeTask is not null)
        {
            try
            {
                await _observeTask.ConfigureAwait(true);
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
        _cts = new CancellationTokenSource();
        var cts = _cts;
        _observeTask = null;
        _historyLoaded = false;
        _pending.Clear();
        _cacheRecords.Clear();
        _cacheDirty = false;
        _cacheCursor = 0;
        _turnBoundaryPending = false;
        _liveAssistant = null;
        _lastSeq = 0;
        _oldestSeq = long.MaxValue;
        Messages.Clear();
        _revealTimer.Stop();
        _pendingUserPrompts.Clear();
        _pendingPromptTimer.Stop();
        PendingImages.Clear();
        SelectedImage = null;
        ReleaseImageCache();
        _timelineTurns.Clear();
        _turnEndTimes.Clear();
        _compactions.Clear();
        _pendingToolResults.Clear();
        _surfaceReplacementCount = 0;
        _prunedResultCount = 0;
        _suppressedProjectionRows = 0;
        IsCompacting = false;
        _currentTurn = 0;
        IsComposerExpanded = false;
        IsReadOnlySession = false;

        IsLoading = true;
        var firstPageStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Phase 0/1: paint the transcript. A cache hit costs one local read plus a tiny state read; the
            // desktop then only sends the delta afterwards (the follow baseline is seeded from the cached
            // seq, so no history page and no full follow snapshot are transferred).
            var paintedFromCache = await TryLoadCachedWindowAsync(cts.Token).ConfigureAwait(true);
            if (!paintedFromCache)
            {
                await LoadFirstWindowAsync(cts.Token).ConfigureAwait(true);
            }

            _historyLoaded = true;
            _stateTimer.Start();
            FirstPageMilliseconds = firstPageStopwatch.ElapsedMilliseconds;
            IsLoading = false;
            System.Diagnostics.Debug.WriteLine(
                $"[Session] {SessionId}: {(paintedFromCache ? "cache" : "first page")} {Messages.Count} rows in " +
                $"{FirstPageMilliseconds} ms (hasOlder={HasOlder})");
            Raise(nameof(ShowEmptyMessages), nameof(IsOpening), nameof(ShowBeginning),
                nameof(FirstPageMilliseconds), nameof(FirstPageText));
            TranscriptUpdated?.Invoke(this, EventArgs.Empty);

            // Phase 2: subscribe to live follow in the background; its snapshot skips events already shown.
            // Bail out when this generation was superseded or the view was closed while history loaded.
            if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(_cts, cts))
            {
                return;
            }

            if (_follow is not null)
            {
                if (!paintedFromCache)
                {
                    var snapshot = await _follow.OpenSessionAsync(SessionId, 1, cts.Token).ConfigureAwait(true);
                    if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(_cts, cts))
                    {
                        return;
                    }

                    // The follow snapshot carries the session's current model selection (next ?? lastUsed);
                    // calibrate once so the chip matches the desktop even if the session list is stale.
                    ApplyModelSelectionBaseline(snapshot.ModelSelection);

                    var baselineRecords = NormalizeHistory(snapshot.Events)
                        .Where(record => record.Seq <= 0 || record.Seq > _lastSeq)
                        .ToList();
                    if (baselineRecords.Count > 0)
                    {
                        AddMessagesBatched(BuildMessages(baselineRecords, trackRuntime: false));
                        CacheRemember(baselineRecords);

                        _lastSeq = Math.Max(_lastSeq, baselineRecords.Max(record => record.Seq));
                        var baselineOldest = baselineRecords.Where(record => record.Seq > 0)
                            .Select(record => record.Seq)
                            .DefaultIfEmpty(long.MaxValue)
                            .Min();
                        if (baselineOldest < _oldestSeq)
                        {
                            _oldestSeq = baselineOldest;
                        }
                    }

                    _cacheCursor = Math.Max(_cacheCursor, snapshot.Cursor);
                    ApplyRuntimeState(snapshot.State);
                }

                _observeTask = FollowLoopAsync(_follow, cts.Token);
                if (_client.State == DshConnectionState.Reconnecting)
                {
                    // The transport dropped before this chat subscribed; no state-change event will fire
                    // for it, so start the 5 s retry gauge explicitly.
                    _hadConnectionLoss = true;
                    StartFollowReconnectWatch();
                }
            }
            else
            {
                _observeTask = ObserveLoopAsync(cts.Token);
                await RefreshStateAsync().ConfigureAwait(true);
            }

            Raise(nameof(HasOlder), nameof(ShowBeginning), nameof(FirstPageText));
            TranscriptUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsSubagentSession(ex))
        {
            IsReadOnlySession = true;
            ErrorMessage = "该会话是子代理（subagent）会话，需从父会话打开查看。";
        }
        catch (Exception ex)
        {
            ErrorMessage = "加载会话失败: " + Describe(ex);
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(ShowEmptyMessages), nameof(IsOpening));
        }
    }

    /// <summary>
    /// Paints the last rendered window from the on-disk cache and seeds the follow baseline so the desktop
    /// only has to send the delta. Returns false when there is no usable cache (or it is stale/rewound), in
    /// which case the caller falls back to the normal first-page load.
    /// </summary>
    private async Task<bool> TryLoadCachedWindowAsync(CancellationToken cancellationToken)
    {
        if (_cache is null || !_cache.TryLoadMessages(SessionId, out var cached) || cached is null)
        {
            return false;
        }

        var records = cached.ToRecords();
        if (records.Count == 0)
        {
            return false;
        }

        // The cache is only usable when the desktop still has at least that seq. A different cursor means
        // the log was rewound/compacted; drop the cache and rebuild instead of rendering mismatched rows.
        SessionRuntimeState? state = null;
        try
        {
            state = await _client.GetStateAsync(SessionId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[cache] {SessionId}: state read failed ({ex.Message}); rebuilding");
            return false;
        }

        if (state is not null && cached.LastSeq > state.Cursor)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[cache] {SessionId}: cached seq {cached.LastSeq} is past the server cursor {state.Cursor}; dropping cache");
            _cache.DeleteMessages(SessionId);
            return false;
        }

        _cacheRecords.Clear();
        _cacheRecords.AddRange(records);
        _cacheDirty = false;
        _cacheCursor = Math.Max(cached.Cursor, state?.Cursor ?? 0);
        _oldestSeq = records[0].Seq;
        _lastSeq = records[^1].Seq;
        _beforeSeq = _oldestSeq;
        HasOlder = true;
        _turnBoundaryPending = false;

        AddMessagesBatched(BuildMessages(records, trackRuntime: false));
        if (state is not null)
        {
            ApplyRuntimeState(state);
        }

        // Seed the incremental baseline: FollowEventsAsync opens session/follow and its snapshot/repair only
        // transfers events after this seq instead of replaying a whole 80-message snapshot.
        if (_client is ISessionBaselineClient baselineClient)
        {
            baselineClient.SeedFollowBaseline(SessionId, _lastSeq, _cacheCursor);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[cache] {SessionId}: painted {records.Count} records (seq {_oldestSeq}..{_lastSeq}, cursor={_cacheCursor})");
        await Task.CompletedTask.ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// First-screen load without a cache: the newest history page plus passive state. The turn-boundary
    /// read-ahead is deferred to the first explicit "load older" (see <see cref="TryExtendTurnBoundaryLazilyAsync"/>),
    /// so opening a conversation costs one page instead of up to 1–3 extra pages.
    /// </summary>
    private async Task LoadFirstWindowAsync(CancellationToken cancellationToken)
    {
        const int firstPageSize = 30;
        SessionHistoryPage page;
        if (_client is IHistoryPagingClient pagingClient)
        {
            var lite = await pagingClient
                .OpenSessionLiteAsync(SessionId, firstPageSize, cancellationToken)
                .ConfigureAwait(true);
            page = lite.Page;
            _cacheCursor = Math.Max(_cacheCursor, page.Cursor);
            if (lite.State is not null)
            {
                ApplyRuntimeState(lite.State);
                _cacheCursor = Math.Max(_cacheCursor, lite.State.Cursor);
            }
        }
        else
        {
            page = await _client
                .GetHistoryAsync(SessionId, maxMessages: firstPageSize, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            _cacheCursor = Math.Max(_cacheCursor, page.Cursor);
        }

        // maxMessages on the wire is a *message* budget aligned to the server's message/turn windows, not an
        // event count: one page can span a whole long turn. Trim to a display-friendly event count and mark
        // that older pages exist when trimming (or the page budget itself) hid anything.
        var allRecords = NormalizeHistory(page.Records);
        var trimmed = TrimNewestPage(allRecords, FirstPageEventRows);
        var records = trimmed.Records;
        var hasOlder = trimmed.HadMore || page.HasMoreOlder || page.HasMore;

        // The backwards cursor MUST be the oldest row we actually rendered, never the raw page start.
        var beforeSeq = records.Count > 0 ? records[0].Seq : page.BeforeSeq;

        // Lazy turn-boundary read-ahead: the newest message-aligned page may start inside the newest turn, so
        // its initiating prompt/steers sit just above it. The read-ahead that used to run during the open is
        // deferred to the first "load older" click (still bounded, still same repair), keeping the open cheap.
        _turnBoundaryPending = hasOlder && records.Count > 0 && !WindowStartsAtTurnBoundary(records);

        AddMessagesBatched(BuildMessages(records, trackRuntime: false));
        CacheRemember(records);

        if (records.Count > 0)
        {
            _oldestSeq = records[0].Seq;
            _lastSeq = records[^1].Seq;
        }

        _beforeSeq = beforeSeq;
        HasOlder = hasOlder;
    }

    /// <summary>
    /// Runs the deferred turn-boundary read-ahead exactly once, on the first "load older". Keeps the reopen
    /// fix (prompt + steer of the newest turn) without paying for it on every open.
    /// </summary>
    private async Task<bool> TryExtendTurnBoundaryLazilyAsync()
    {
        if (!_turnBoundaryPending || _beforeSeq is not { } cursor || _cacheRecords.Count == 0 || _disposed)
        {
            return false;
        }

        _turnBoundaryPending = false;
        var current = _cacheRecords.OrderBy(record => record.Seq).ToList();
        var extended = await ExtendInitialWindowToTurnBoundaryAsync(current, cursor, HasOlder, _cts.Token)
            .ConfigureAwait(true);
        var older = extended.Records.Where(record => record.Seq < _oldestSeq).ToList();
        if (older.Count == 0)
        {
            return false;
        }

        var built = BuildMessages(older, trackRuntime: false);
        _suspendTimelineRebuild = true;
        try
        {
            for (var index = built.Count - 1; index >= 0; index--)
            {
                Messages.Insert(0, built[index]);
            }
        }
        finally
        {
            _suspendTimelineRebuild = false;
        }

        RebuildTimeline();
        CacheRemember(older);
        _oldestSeq = older.Min(record => record.Seq);
        _beforeSeq = extended.BeforeSeq ?? _oldestSeq;
        HasOlder = extended.HasOlder;
        Raise(nameof(ShowBeginning), nameof(FirstPageText));
        OlderPageInserted?.Invoke(this, built.Count);
        return true;
    }

    /// <summary>Appends records to the window the cache will persist (de-duplicated by seq).</summary>
    private void CacheRemember(IEnumerable<SessionEventRecord> records)
    {
        if (_cache is null)
        {
            return;
        }

        var known = new HashSet<long>(_cacheRecords.Select(record => record.Seq));
        foreach (var record in records)
        {
            if (record.Seq <= 0 || !known.Add(record.Seq))
            {
                continue;
            }

            _cacheRecords.Add(record);
            _cacheDirty = true;
        }
    }

    /// <summary>Persists the rendered window when it changed; safe to call repeatedly (no-op when clean).</summary>
    private void SaveCacheIfDirty()
    {
        if (_cache is null || !_cacheDirty || !_historyLoaded || _lastSeq <= 0 ||
            _cacheRecords.Count == 0 || _disposed)
        {
            return;
        }

        _cacheDirty = false;
        try
        {
            _cache.SaveMessages(SessionId, _lastSeq, Math.Max(_cacheCursor, _lastSeq), _cacheRecords);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[cache] {SessionId}: save failed: {ex.Message}");
        }
    }

    /// <summary>Initial history window after the optional turn-boundary extension.</summary>
    private readonly record struct InitialWindow(List<SessionEventRecord> Records, long? BeforeSeq, bool HasOlder);

    /// <summary>Upper bound on extra pages read while growing the initial window to a turn boundary.</summary>
    private const int InitialWindowMaxPages = 8;

    /// <summary>Upper bound on extra events read while growing the initial window to a turn boundary.</summary>
    private const int InitialWindowMaxRecords = 3000;

    /// <summary>
    /// Event budget for the first screen. The server pages by messages, so one page can hold far more events
    /// than the first layout should build; the trimmed tail is rendered immediately and the rest is recovered
    /// by <see cref="LoadOlderAsync"/> (the backwards cursor points at the first kept row, so nothing is lost).
    /// </summary>
    private const int FirstPageEventRows = 90;

    /// <summary>
    /// Event budget per "load older" step. Larger than the first screen because the user asked for history:
    /// one step should surface roughly a screenful of turns without making the click feel endless.
    /// </summary>
    private const int OlderPageEventRows = 320;

    /// <summary>
    /// Message budget per read-ahead page. Larger than the first page because one page must cover enough of a
    /// tool-heavy running turn to reach its <c>turn/start</c> in a handful of round trips.
    /// </summary>
    private const int InitialWindowPageMessages = 60;

    /// <summary>
    /// Grows the newest history page backwards to the <c>turn/start</c> of the oldest turn it contains.
    /// <para>
    /// The server pages history by a <em>message</em> budget aligned to message/turn windows, so a session whose
    /// newest page starts in the middle of a turn returns a page that starts in the middle of that turn. The
    /// user prompts that opened the turn — and every steer/interjection sent while it ran — sit before that page
    /// and are never replayed by the follow snapshot (which only adds events after the cursor). Reopening such a
    /// session therefore showed the folded actions without the prompts; this read-ahead restores them.
    /// </para>
    /// <para>
    /// Bounded read-ahead. A running turn reads up to <see cref="InitialWindowMaxPages"/> /
    /// <see cref="InitialWindowMaxRecords"/> to reach its <c>turn/start</c>. A finished conversation only needs
    /// its newest turn complete (prompt, steers and final answer), so it stops as soon as a fetched page
    /// contains a <c>turn/start</c> — normally one extra page, never the whole history.
    /// </para>
    /// </summary>
    private async Task<InitialWindow> ExtendInitialWindowToTurnBoundaryAsync(
        List<SessionEventRecord> records,
        long? beforeSeq,
        bool hasOlder,
        CancellationToken cancellationToken)
    {
        if (!hasOlder || records.Count == 0 || beforeSeq is not { } cursor)
        {
            return new InitialWindow(records, beforeSeq, hasOlder);
        }

        if (WindowStartsAtTurnBoundary(records))
        {
            return new InitialWindow(records, beforeSeq, hasOlder);
        }

        var finishedConversation = !IsRunning;
        var maxPages = finishedConversation ? 3 : InitialWindowMaxPages;
        var maxRecords = finishedConversation ? 1200 : InitialWindowMaxRecords;
        var combined = new List<SessionEventRecord>(records);
        var oldest = records[0].Seq;
        var more = hasOlder;
        var added = 0;
        try
        {
            for (var pageIndex = 0; pageIndex < maxPages && more && added < maxRecords; pageIndex++)
            {
                SessionHistoryPage olderPage;
                if (_client is IHistoryPagingClient pagingClient && cursor > 0)
                {
                    olderPage = await pagingClient
                        .GetOlderHistoryAsync(SessionId, cursor, InitialWindowPageMessages, cancellationToken)
                        .ConfigureAwait(true);
                }
                else
                {
                    olderPage = await _client
                        .GetHistoryAsync(SessionId, cursor, InitialWindowPageMessages, cancellationToken)
                        .ConfigureAwait(true);
                }

                var older = NormalizeHistory(olderPage.Records)
                    .Where(record => record.Seq < oldest)
                    .ToList();
                more = olderPage.HasMoreOlder || olderPage.HasMore;
                if (older.Count == 0)
                {
                    break;
                }

                combined.InsertRange(0, older);
                added += older.Count;
                oldest = older[0].Seq;
                cursor = oldest;
                if (added >= maxRecords || WindowStartsAtTurnBoundary(combined))
                {
                    break;
                }

                // The newest message-aligned page starts at a message, so the turn/start can only arrive in an
                // older page; once it does, the last turn is fully covered and a finished conversation stops.
                if (finishedConversation &&
                    older.Any(record => string.Equals(record.Type, "turn/start", StringComparison.Ordinal)))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best effort: a failed read-ahead must never break the first screen; keep the plain page.
            System.Diagnostics.Debug.WriteLine(
                $"[Session] {SessionId}: initial window extension stopped after {added} records: {ex.Message}");
            return new InitialWindow(records, beforeSeq, hasOlder);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[Session] {SessionId}: extended initial window by {added} records to a turn boundary (hasOlder={more})");
        return new InitialWindow(combined, cursor, more);
    }

    /// <summary>
    /// True when the oldest turn-scoped record in the window is a <c>turn/start</c> (the window begins at a
    /// conversation boundary) — log-only/control records before it do not count.
    /// </summary>
    private static bool WindowStartsAtTurnBoundary(IReadOnlyList<SessionEventRecord> records)
    {
        foreach (var record in records)
        {
            if (IsLogOnlyRecord(record.Type))
            {
                continue;
            }

            return record.Type is "turn/start";
        }

        return true;
    }

    /// <summary>Events that never belong to a turn's transcript (metadata/control envelopes).</summary>
    private static bool IsLogOnlyRecord(string type) => type is
        "permission/preset" or "sandbox/mode" or "approval/policy" or "agent/inbox/spliced" or
        "system/message" or "request/header" or "request/context" or "session/title" or
        "session/title-llm-request";

    /// <summary>
    /// Keeps one <c>session/follow</c> consumer alive. A broken stream is retried at most
    /// <see cref="MaxFollowRetries"/> times, <see cref="FollowRetryInterval"/> apart; when the budget is
    /// exhausted the chat raises <see cref="FollowAbandoned"/> so the shell returns to the connect page
    /// instead of retrying forever.
    /// </summary>
    private async Task FollowLoopAsync(ISessionFollowClient follow, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed && !IsFollowAbandoned)
        {
            // While the socket is down (or recovering) keep the 5 s retry gauge running: a retry that
            // blocks inside the transport waiting for a new connection generation would otherwise never
            // surface a failure, so the chat could wait for the supervisor far longer than the budget.
            if (_client.State != DshConnectionState.Connected)
            {
                StartFollowReconnectWatch();
            }

            Exception? failure = null;
            var resynced = false;
            try
            {
                await foreach (var record in follow
                                   .FollowEventsAsync(SessionId, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (_disposed)
                    {
                        return;
                    }

                    NoteFollowStreamAlive();
                    Dispatcher.UIThread.Post(() => OnLiveEvent(record));
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    // The channel completed without cancellation: the subscription is gone.
                    failure = new DshRemoteException(
                        DshRemoteException.Codes.Transport,
                        "follow stream ended");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsResyncException(ex))
            {
                PostIfAlive(() => SetFollowStatus("消息缺口，正在补齐…", clearAfterMs: 10000));
                try
                {
                    var snapshot = await follow.OpenSessionAsync(SessionId, 0, cancellationToken)
                        .ConfigureAwait(false);
                    if (_disposed || cancellationToken.IsCancellationRequested || IsFollowAbandoned)
                    {
                        return;
                    }

                    // The resync itself succeeded, so the transport error budget starts over; a separate
                    // streak counter stops a pathological "snapshot → gap → snapshot" server from looping
                    // forever without ever delivering an event.
                    ResetFollowRetryBudget();
                    if (++_followResyncStreak > MaxFollowRetries)
                    {
                        AbandonFollow(
                            $"实时订阅反复出现消息缺口（已尝试 {MaxFollowRetries} 次补齐）。请重新连接桌面。",
                            showError: true);
                        return;
                    }

                    Dispatcher.UIThread.Post(() => ApplyResyncSnapshot(snapshot));
                    resynced = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException resyncTimeout)
                {
                    // A non-cancellation timeout (e.g. HttpClient.RequestTimeout) is a real failure.
                    failure = resyncTimeout;
                }
                catch (Exception resyncError)
                {
                    failure = resyncError;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (cancellationToken.IsCancellationRequested || _disposed || IsFollowAbandoned)
            {
                return;
            }

            if (resynced)
            {
                // A fresh baseline repaired the gap; reopen the stream from it. A short pause keeps a
                // pathological "snapshot → gap → snapshot" server from becoming a hot RPC loop.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (!await ScheduleFollowRetryAsync(failure, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Spends one retry from the fixed budget and waits <see cref="FollowRetryInterval"/> before the next
    /// attempt. Returns <c>false</c> when the budget is exhausted or the session is shutting down.
    /// </summary>
    private async Task<bool> ScheduleFollowRetryAsync(Exception? failure, CancellationToken cancellationToken)
    {
        if (_client.State == DshConnectionState.Connected)
        {
            // Stream-level error on a live socket: the transport gauge is not the owner of this retry.
            StopFollowReconnectWatch();
        }

        if (!TryConsumeFollowRetry(out var attempt))
        {
            AbandonFollow(BuildFollowGaveUpMessage(failure), showError: true);
            return false;
        }

        var detail = DescribeFollowFailure(failure);
        var status = $"订阅断开，{FollowRetryInterval.TotalSeconds:0} 秒后重试（{attempt}/{MaxFollowRetries}）…";
        PostIfAlive(() =>
        {
            ErrorMessage = "实时订阅已断开: " + detail;
            SetFollowStatus(status, clearAfterMs: 6000);
        });

        try
        {
            var delay = FollowRetryDelayOverride;
            if (delay is null)
            {
                await Task.Delay(FollowRetryInterval, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await delay(FollowRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (cancellationToken.IsCancellationRequested || _disposed || IsFollowAbandoned)
        {
            return false;
        }

        PostIfAlive(() => SetFollowStatus("正在重连并补齐…", clearAfterMs: 6000));
        return true;
    }

    private bool TryConsumeFollowRetry(out int attempt)
    {
        lock (_followRetryGate)
        {
            attempt = _followRetryCount;
            if (_followGaveUp || _followRetryCount >= MaxFollowRetries)
            {
                return false;
            }

            attempt = ++_followRetryCount;
            return true;
        }
    }

    private void ResetFollowRetryBudget()
    {
        lock (_followRetryGate)
        {
            _followRetryCount = 0;
        }
    }

    /// <summary>Called whenever the stream delivers data again: the retry budget starts over.</summary>
    private void NoteFollowStreamAlive()
    {
        _followResyncStreak = 0;
        ResetFollowRetryBudget();
        PostIfAlive(() =>
        {
            if (!HasError)
            {
                return;
            }

            if (ErrorMessage.StartsWith("实时订阅已断开", StringComparison.Ordinal) ||
                ErrorMessage.StartsWith("补齐失败", StringComparison.Ordinal))
            {
                ErrorMessage = string.Empty;
            }
        });
    }

    /// <summary>
    /// Starts the 5 s retry gauge used while the socket itself is reconnecting. Without it a stream whose
    /// pump is blocked waiting for a new connection generation would never surface a failure, so the chat
    /// could sit "reconnecting" forever.
    /// </summary>
    private void StartFollowReconnectWatch()
    {
        if (!IsFollowMode || _disposed || IsFollowAbandoned)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            StartFollowReconnectWatchCore();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && !IsFollowAbandoned)
                {
                    StartFollowReconnectWatchCore();
                }
            });
        }
    }

    private void StartFollowReconnectWatchCore()
    {
        if (_followReconnectTimer is null)
        {
            var timer = new DispatcherTimer { Interval = FollowRetryInterval };
            timer.Tick += (_, _) => OnFollowReconnectWatchTick();
            _followReconnectTimer = timer;
        }

        if (!_followReconnectTimer.IsEnabled)
        {
            _followReconnectTimer.Start();
        }
    }

    private void StopFollowReconnectWatch()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            StopFollowReconnectWatchCore();
        }
        else
        {
            Dispatcher.UIThread.Post(StopFollowReconnectWatchCore);
        }
    }

    private void StopFollowReconnectWatchCore()
    {
        if (_followReconnectTimer is { IsEnabled: true })
        {
            _followReconnectTimer.Stop();
        }
    }

    private void OnFollowReconnectWatchTick()
    {
        if (_disposed || IsFollowAbandoned)
        {
            StopFollowReconnectWatchCore();
            return;
        }

        if (_client.State == DshConnectionState.Connected)
        {
            StopFollowReconnectWatchCore();
            return;
        }

        if (!TryConsumeFollowRetry(out var attempt))
        {
            StopFollowReconnectWatchCore();
            AbandonFollow(
                $"实时连接已断开，已重试 {MaxFollowRetries} 次仍无法连接。请检查桌面 relay 后重新连接。",
                showError: true);
            return;
        }

        SetFollowStatus($"连接断开，正在重试（{attempt}/{MaxFollowRetries}）…", clearAfterMs: 6000);
    }

    private static string DescribeFollowFailure(Exception? failure) => failure switch
    {
        null => "订阅已结束",
        DshRemoteException remote when remote.Code == DshRemoteException.Codes.FollowBusy
            => "上一路实时订阅正在释放，等待重新建立",
        DshRemoteException remote => remote.Message,
        _ => failure.Message
    };

    private static string BuildFollowGaveUpMessage(Exception? failure)
    {
        var detail = DescribeFollowFailure(failure);
        return $"实时订阅已断开，已重试 {MaxFollowRetries} 次仍无法连接（{detail}）。请重新连接桌面。";
    }

    private void ApplyResyncSnapshot(SessionSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        var records = NormalizeHistory(snapshot.Events)
            .Where(record => record.Seq <= 0 || record.Seq > _lastSeq)
            .ToList();
        foreach (var record in records)
        {
            if (record.Seq > 0)
            {
                _lastSeq = Math.Max(_lastSeq, record.Seq);
            }
        }

        ApplyLiveBatch(records, "resync");
        ApplyRuntimeState(snapshot.State);
        ApplyModelSelectionBaseline(snapshot.ModelSelection);
        SetFollowStatus(records.Count > 0 ? $"已重连，补齐 {records.Count} 条事件" : "已重连");
    }

    private async Task ObserveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var record in _client
                               .ObserveEventsAsync(SessionId, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                Dispatcher.UIThread.Post(() => OnLiveEvent(record));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    ErrorMessage = "实时事件流已断开: " + Describe(ex);
                    IsRunning = false;
                }
            });
        }
    }

    private void SetFollowStatus(string text, int clearAfterMs = 2500)
    {
        FollowStatusText = text;
        var captured = text;
        _ = Task.Delay(clearAfterMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && FollowStatusText == captured)
            {
                FollowStatusText = string.Empty;
            }
        }), TaskScheduler.Default);
    }

    private void OnClientStateChanged(object? sender, DshConnectionState state)
    {
        PostIfAlive(() =>
        {
            switch (state)
            {
                case DshConnectionState.Reconnecting:
                    _hadConnectionLoss = true;
                    CloseModelPicker();
                    SetFollowStatus("连接断开，正在重连…", clearAfterMs: 60000);
                    StartFollowReconnectWatch();
                    break;
                case DshConnectionState.Connected when _hadConnectionLoss:
                    _hadConnectionLoss = false;
                    StopFollowReconnectWatchCore();
                    ResetFollowRetryBudget();
                    SetFollowStatus("已重连，正在补齐…", clearAfterMs: 6000);
                    _ = RefreshStateAsync();
                    _ = ReloadModelCatalogAfterReconnectAsync();
                    break;
                case DshConnectionState.Failed:
                    StopFollowReconnectWatchCore();
                    CloseModelPicker();
                    SetFollowStatus("连接失败，正在重试…", clearAfterMs: 60000);
                    break;
                case DshConnectionState.Disconnected:
                    StopFollowReconnectWatchCore();
                    CloseModelPicker();
                    break;
            }
        });
    }

    // ---- Runtime state ------------------------------------------------------------------------

    private async Task RefreshStateAsync()
    {
        if (_refreshingState || _disposed || _client is null)
        {
            return;
        }

        _refreshingState = true;
        try
        {
            var state = await _client.GetStateAsync(SessionId, _cts.Token).ConfigureAwait(true);
            _cacheCursor = Math.Max(_cacheCursor, state.Cursor);
            ApplyRuntimeState(state);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Session] state refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshingState = false;
        }
    }

    private void ApplyRuntimeState(SessionRuntimeState? state)
    {
        if (state is null || _disposed)
        {
            return;
        }

        IsRunning = state.Running;
        PendingInputCount = state.PendingInput?.Count ?? 0;
        var previousIds = Queue.Select(item => item.Id).ToList();
        var nextItems = state.Queue ?? Array.Empty<QueuedPrompt>();
        var nextIds = nextItems.Select(item => item.Id).ToList();
        var same = previousIds.Count == nextIds.Count && previousIds.SequenceEqual(nextIds, StringComparer.Ordinal);
        if (!same)
        {
            var existing = Queue.ToDictionary(item => item.Id, StringComparer.Ordinal);
            Queue.Clear();
            foreach (var prompt in nextItems)
            {
                if (existing.TryGetValue(prompt.Id, out var vm))
                {
                    vm.Text = prompt.Text ?? vm.Text;
                    vm.OriginalText = prompt.Text ?? vm.OriginalText;
                    Queue.Add(vm);
                }
                else
                {
                    var item = new QueueItemViewModel(prompt, this);
                    item.OriginalText = prompt.Text ?? string.Empty;
                    Queue.Add(item);
                }
            }

            Raise(nameof(HasQueue), nameof(QueueHeaderText), nameof(ShowSessionStatusRow));
            if (Queue.Count == 0)
            {
                IsQueueExpanded = false;
            }
        }
    }

    internal async Task UpdateQueueItemAsync(QueueItemViewModel item, QueueEditAction action, string? text = null)
    {
        if (_follow is null || _disposed)
        {
            ErrorMessage = "当前客户端不支持队列编辑。";
            return;
        }

        try
        {
            await _follow.UpdateQueueAsync(SessionId, item.Id, action, text, _cts.Token).ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
            if (action == QueueEditAction.Remove && Queue.Contains(item))
            {
                Queue.Remove(item);
                Raise(nameof(HasQueue), nameof(QueueHeaderText));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "队列操作失败: " + Describe(ex);
        }
    }

    // ---- Send / steer / stop ------------------------------------------------------------------

    private async Task SendAsync()
    {
        var text = (Draft ?? string.Empty).Trim();
        var attachments = PendingImages.Select(image => image.Attachment).ToList();
        if ((text.Length == 0 && attachments.Count == 0) || IsSending || IsReadOnlySession || _disposed)
        {
            return;
        }

        IsComposerExpanded = false;
        var steer = IsSteerMode && IsRunning;
        DiagnosticsLog.Trace($"session {SessionId}: send (steer={steer}, images={attachments.Count})");
        var message = ChatMessageViewModel.CreateUser(
            text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            optimistic: true,
            header: steer ? "插话（steer）" : null);
        foreach (var pendingImage in PendingImages.ToList())
        {
            if (pendingImage.Preview is not null)
            {
                message.AddImage(ChatImagePartViewModel.FromLocalBytes(
                    pendingImage.Attachment.Data,
                    pendingImage.Attachment.MediaType));
            }
        }

        Messages.Add(message);
        PendingImages.Clear();
        Raise(nameof(ShowEmptyMessages), nameof(HasPendingImages), nameof(CanSend));

        // The optimistic bubble is retired when its durable user/message echo arrives (matched by the
        // echoed rpcId, or by text while the echo carries no id). Without an echo it times out.
        var pending = new PendingUserPrompt { Bubble = message, Text = text };
        _pendingUserPrompts.Add(pending);
        if (!_pendingPromptTimer.IsEnabled)
        {
            _pendingPromptTimer.Start();
        }

        Draft = string.Empty;
        IsSending = true;
        ErrorMessage = string.Empty;

        try
        {
            if (steer && attachments.Count == 0 && _follow is not null)
            {
                await _follow.SteerAsync(SessionId, text, _cts.Token).ConfigureAwait(true);
                if (!pending.Matched)
                {
                    message.MarkSent();
                }
            }
            else
            {
                var requestId = await _client
                    .SendPromptAsync(SessionId, text, attachments.Count > 0 ? attachments : null, steer, _cts.Token)
                    .ConfigureAwait(true);
                if (!pending.Matched)
                {
                    pending.RequestId = requestId;
                    message.MarkSent();
                }

                IsRunning = true;
            }

            // The host accepted the prompt: this session is no longer "blank" (a workspace's new
            // session), even before the next list refresh carries the new metadata.
            PromptSent?.Invoke(this, EventArgs.Empty);
            await RefreshStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _pendingUserPrompts.Remove(pending);
        }
        catch (Exception ex)
        {
            _pendingUserPrompts.Remove(pending);
            message.IsError = true;
            ErrorMessage = "发送失败: " + Describe(ex);
        }
        finally
        {
            if (_pendingUserPrompts.Count == 0)
            {
                _pendingPromptTimer.Stop();
            }

            IsSending = false;
            TranscriptUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Images -------------------------------------------------------------------------------

    private async Task PickImageAsync()
    {
        if (!CanAttachImage)
        {
            return;
        }

        var picker = ImagePicker;
        if (picker is null)
        {
            ErrorMessage = "当前平台没有可用的图片选择器。";
            return;
        }

        try
        {
            var attachment = await picker(_cts.Token).ConfigureAwait(true);
            if (attachment is null || attachment.Data.Length == 0)
            {
                return;
            }

            Bitmap? preview = null;
            try
            {
                using var stream = new MemoryStream(attachment.Data);
                preview = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                ErrorMessage = "图片预览失败: " + ex.Message;
            }

            PendingImages.Add(new PendingImageViewModel(attachment, preview, RemovePendingImage));
            Raise(nameof(HasPendingImages), nameof(CanSend));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = "选择图片失败: " + ex.Message;
        }
    }

    private void ReleaseImageCache()
    {
        foreach (var bitmap in _imageCache.Values)
        {
            bitmap?.Dispose();
        }
        _imageCache.Clear();
    }

    private void RemovePendingImage(PendingImageViewModel image)
    {
        PendingImages.Remove(image);
        Raise(nameof(HasPendingImages), nameof(CanSend));
    }

    private ChatImagePartViewModel CreateImagePart(DshImageRef image)
    {
        ChatImagePartViewModel? viewModel = null;
        var openCommand = new RelayCommand(() => SelectedImage = viewModel);
        viewModel = new ChatImagePartViewModel(image, openCommand, SessionId);

        if (_imageCache.TryGetValue(image.AttachmentId, out var cached))
        {
            if (cached is not null)
            {
                viewModel.SetBitmap(cached);
            }
            else
            {
                viewModel.SetError("图片暂不可用");
            }

            return viewModel;
        }

        _ = LoadImageAsync(viewModel, image);
        return viewModel;
    }

    private async Task LoadImageAsync(ChatImagePartViewModel viewModel, DshImageRef image)
    {
        if (_imageClient is null)
        {
            viewModel.SetError("当前客户端不支持图片读取");
            return;
        }

        try
        {
            // Content-addressed disk cache: reopening a chat (or cold starting) must not re-download images.
            byte[] bytes;
            if (_cache is not null && _cache.TryReadImage(image.AttachmentId, out var cachedBytes) &&
                cachedBytes is not null)
            {
                bytes = cachedBytes;
            }
            else
            {
                var data = await _imageClient.GetImageAsync(SessionId, image, _cts.Token).ConfigureAwait(true);
                bytes = data.Bytes;
                _cache?.SaveImage(image.AttachmentId, bytes, image.MediaType);
            }

            Bitmap bitmap;
            try
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                _imageCache[image.AttachmentId] = null;
                viewModel.SetError("图片解码失败: " + ex.Message);
                return;
            }

            _imageCache[image.AttachmentId] = bitmap;
            viewModel.SetBitmap(bitmap);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _imageCache[image.AttachmentId] = null;
            viewModel.SetError(Describe(ex));
        }
    }

    /// <summary>Attaches ordered image parts of an event to a transcript row.</summary>
    private void AttachImageParts(ChatMessageViewModel message, SessionEventRecord record)
    {
        foreach (var part in DshContent.PartsOf(record))
        {
            if (part is DshImagePart imagePart)
            {
                message.AddImage(CreateImagePart(imagePart.Image));
            }
        }
    }

    /// <summary>Retires optimistic user bubbles whose durable echo never arrived.</summary>
    private void CheckPendingUserPrompts()
    {
        if (_pendingUserPrompts.Count == 0)
        {
            _pendingPromptTimer.Stop();
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var pending in _pendingUserPrompts.ToList())
        {
            if (now - pending.SentAt < PendingPromptTimeout)
            {
                continue;
            }

            _pendingUserPrompts.Remove(pending);
            pending.Bubble.MarkSent();
            pending.Bubble.SetStatus("已发送（未收到服务端回执）");
        }

        if (_pendingUserPrompts.Count == 0)
        {
            _pendingPromptTimer.Stop();
        }
    }

    private async Task StopAsync()
    {
        if (!CanStop)
        {
            return;
        }

        StopRequested = true;
        try
        {
            await _client.CancelAsync(SessionId, _cts.Token).ConfigureAwait(true);
            await RefreshStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = "停止失败: " + Describe(ex);
        }
    }

    private async Task LoadOlderAsync()
    {
        if (!HasOlder || IsLoadingOlder || _oldestSeq == long.MaxValue || _disposed)
        {
            return;
        }

        IsLoadingOlder = true;
        try
        {
            // The first explicit "load older" also completes the newest turn's boundary (the read-ahead that
            // used to run during the open). Subsequent clicks page normally.
            if (await TryExtendTurnBoundaryLazilyAsync().ConfigureAwait(true))
            {
                return;
            }

            SessionHistoryPage page;
            if (_client is IHistoryPagingClient pagingClient && _beforeSeq is { } beforeSeq)
            {
                page = await pagingClient.GetOlderHistoryAsync(SessionId, beforeSeq, 30, _cts.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                page = await _client.GetHistoryAsync(SessionId, _oldestSeq, 30, _cts.Token).ConfigureAwait(true);
            }

            var older = page.Records
                .Where(record => record.Seq < _oldestSeq)
                .OrderBy(record => record.Seq)
                .ToList();

            // Keep the rows nearest the current top so the transcript stays contiguous, but move the backwards
            // cursor to the first KEPT row — not the raw page start. With the raw start, the dropped older slice
            // would sit before the cursor and the next read would skip it permanently (the "history is missing
            // after scrolling to the beginning" defect).
            var dropped = older.Count > OlderPageEventRows;
            if (dropped)
            {
                older = older.Skip(older.Count - OlderPageEventRows).ToList();
            }

            var previousBefore = _beforeSeq;
            var nextBefore = older.Count > 0 ? older[0].Seq : page.BeforeSeq;
            var madeProgress = nextBefore is { } candidate &&
                               (previousBefore is not { } previous || candidate < previous);

            var built = BuildMessages(older, trackRuntime: false);
            _suspendTimelineRebuild = true;
            try
            {
                for (var index = built.Count - 1; index >= 0; index--)
                {
                    Messages.Insert(0, built[index]);
                }
            }
            finally
            {
                _suspendTimelineRebuild = false;
            }

            RebuildTimeline();

            if (older.Count > 0)
            {
                _oldestSeq = older.Min(record => record.Seq);
            }

            if (nextBefore is { } cursor)
            {
                _beforeSeq = cursor;
            }

            CacheRemember(older);
            _cacheCursor = Math.Max(_cacheCursor, page.Cursor);
            SaveCacheIfDirty();

            // Never offer another read that cannot move backwards (a host with an inclusive/stuck cursor would
            // otherwise loop forever on the first page).
            HasOlder = madeProgress && (page.HasMoreOlder || page.HasMore || dropped);
            Raise(nameof(ShowBeginning), nameof(FirstPageText));
            // The view restores the reading position using the number of inserted rows.
            OlderPageInserted?.Invoke(this, built.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = "加载更早历史失败: " + Describe(ex);
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    // ---- Event application --------------------------------------------------------------------

    private void OnLiveEvent(SessionEventRecord record)
    {
        if (_disposed)
        {
            return;
        }

        if (record.Seq > 0)
        {
            if (record.Seq <= _lastSeq)
            {
                return;
            }

            _lastSeq = record.Seq;
            CacheRemember(new[] { record });
        }

        if (!_historyLoaded)
        {
            _pending.Add(record);
            return;
        }

        EnqueueLiveEvent(record);
    }

    private void EnqueueLiveEvent(SessionEventRecord record)
    {
        _liveQueue.Enqueue(record);
        if (_liveFlushTimer is null)
        {
            _liveFlushTimer = new DispatcherTimer { Interval = LiveFlushInterval };
            _liveFlushTimer.Tick += (_, _) => FlushLiveEvents();
        }

        if (!_liveFlushTimer.IsEnabled)
        {
            _liveFlushTimer.Start();
        }
    }

    private void FlushLiveEvents()
    {
        _liveFlushTimer?.Stop();
        if (_disposed || _liveQueue.Count == 0)
        {
            return;
        }

        var batch = new List<SessionEventRecord>(Math.Min(_liveQueue.Count, LiveBatchMax));
        while (batch.Count < LiveBatchMax && _liveQueue.Count > 0)
        {
            batch.Add(_liveQueue.Dequeue());
        }

        ApplyLiveBatch(batch, "live");

        if (_liveQueue.Count > 0 && _liveFlushTimer is not null && !_disposed)
        {
            _liveFlushTimer.Start();
        }
    }

    /// <summary>
    /// Applies several session events with one timeline rebuild and one transcript notification. UI work per
    /// event (record projection + fold rebuild) is the expensive part; doing it once per burst keeps the UI
    /// thread responsive while a running turn streams.
    /// </summary>
    private void ApplyLiveBatch(IReadOnlyList<SessionEventRecord> batch, string source)
    {
        if (_disposed || batch.Count == 0)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var refreshState = false;
        _suspendTimelineRebuild = true;
        try
        {
            foreach (var record in batch)
            {
                ApplyRecord(record, Messages.Add, FindToolCallMessage, trackRuntime: true);
                if (record.Type is "turn/start" or "step/start")
                {
                    refreshState = true;
                }
            }
        }
        finally
        {
            _suspendTimelineRebuild = false;
        }

        RebuildTimeline();
        Raise(nameof(ShowEmptyMessages));
        if (refreshState)
        {
            _ = RefreshStateAsync();
        }

        TranscriptUpdated?.Invoke(this, EventArgs.Empty);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed > 80 || batch.Count > 8)
        {
            DiagnosticsLog.Trace($"perf: {source} batch n={batch.Count} ms={elapsed:F0} messages={Messages.Count}");
        }
    }

    /// <summary>
    /// Finds the durable tool row for a <c>tool/result</c>. Matches by callId first and, when the callId is
    /// unavailable or the row was loaded from another page, by the cited <c>tool/call</c> seq
    /// (<c>sourceEventSeqs[0]</c> of an append result).
    /// </summary>
    private ChatMessageViewModel? FindToolCallMessage(string? callId, long? sourceSeq)
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            var message = Messages[index];
            if (!message.IsTool)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(callId) && string.Equals(message.CallId, callId, StringComparison.Ordinal))
            {
                return message;
            }

            if (sourceSeq is { } seq && message.Seq == seq)
            {
                return message;
            }
        }

        return null;
    }

    private List<ChatMessageViewModel> BuildMessages(IReadOnlyList<SessionEventRecord> records, bool trackRuntime)
    {
        var built = new List<ChatMessageViewModel>();
        foreach (var record in NormalizeHistory(records))
        {
            // Page-local match first (history page built before it is merged), then fall back to the already
            // loaded window: a follow baseline can carry the result of a call that the first page already
            // rendered, and without the fallback the result would sit in the pending map forever.
            ApplyRecord(
                record,
                built.Add,
                (callId, sourceSeq) => FindToolCall(callId, sourceSeq, built) ?? FindToolCallMessage(callId, sourceSeq),
                trackRuntime);
        }

        return built;
    }

    /// <summary>Page-local variant of <see cref="FindToolCallMessage"/> used while projecting a history page.</summary>
    private static ChatMessageViewModel? FindToolCall(string? callId, long? sourceSeq, List<ChatMessageViewModel> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (!message.IsTool)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(callId) && string.Equals(message.CallId, callId, StringComparison.Ordinal))
            {
                return message;
            }

            if (sourceSeq is { } seq && message.Seq == seq)
            {
                return message;
            }
        }

        return null;
    }

    // ---- Session-log surface / compaction projection -------------------------------------------

    /// <summary>
    /// Remembers a result whose <c>tool/call</c> has not been projected yet. Bounded so a pathological log
    /// cannot grow the map without limit; the oldest entry is dropped first.
    /// </summary>
    private void StashPendingToolResult(string callId, string output, long time, bool isError)
    {
        if (_pendingToolResults.Count >= MaxPendingToolResults && !_pendingToolResults.ContainsKey(callId))
        {
            var oldest = _pendingToolResults.Keys.FirstOrDefault();
            if (oldest is not null)
            {
                _pendingToolResults.Remove(oldest);
            }
        }

        _pendingToolResults[callId] = new PendingToolResult(output, time, isError);
    }

    /// <summary>
    /// Handles a replacement surface event (DSH session-log v4 <c>surfaceOp</c> <c>replace</c>). Replacements
    /// are model-side surface updates: the human transcript keeps the append-origin rows, so they never become
    /// rows by themselves. Summary-compaction checkpoints additionally anchor the compaction card.
    /// </summary>
    private void ApplySurfaceReplacement(
        SessionEventRecord record,
        Action<ChatMessageViewModel> add,
        bool trackRuntime)
    {
        _surfaceReplacementCount++;

        // A replacement of an assistant message (not produced by current DSH versions, but possible for future
        // surface-rewriting producers) settles any live streamed row so a stale "thinking/streaming" row cannot
        // linger outside the fold.
        if (string.Equals(record.Type, "assistant/message", StringComparison.Ordinal))
        {
            FinishLiveAssistant(null);
        }

        if (TryReadCompactionCheckpoint(record, out var compactionId, out var manual))
        {
            ApplyCompactionCheckpoint(record, compactionId, manual, add);
            return;
        }

        // Tool-result pruning and system-prompt replacements are model-only; nothing to show.
        if (trackRuntime && _surfaceReplacementCount == 1)
        {
            DiagnosticsLog.Trace($"session {SessionId}: ignoring surface replacement events (model-only)");
        }
    }

    /// <summary>Reads the compact-checkpoint source identity of a replacement <c>user/message</c>.</summary>
    private static bool TryReadCompactionCheckpoint(SessionEventRecord record, out string compactionId, out bool manual)
    {
        compactionId = string.Empty;
        manual = false;
        if (!string.Equals(record.Type, "user/message", StringComparison.Ordinal) ||
            record.Data.ValueKind != JsonValueKind.Object ||
            !record.Data.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!string.Equals(ReadString(source, "kind"), "compact-checkpoint", StringComparison.Ordinal))
        {
            return false;
        }

        var id = ReadString(source, "compactionId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        compactionId = id!;
        manual = !string.IsNullOrWhiteSpace(ReadString(source, "sourceCommandId"));
        return true;
    }

    private void ApplyCompactionCheckpoint(
        SessionEventRecord record,
        string compactionId,
        bool manual,
        Action<ChatMessageViewModel> add)
    {
        var state = GetOrCreateCompactionState(compactionId, manual, turn: null);
        if (manual)
        {
            state.Manual = true;
        }

        state.CheckpointSeq ??= record.Seq;
        state.CheckpointTime = record.Time;

        if (state.Row is null)
        {
            var row = ChatMessageViewModel.CreateCompaction(
                state.CompactionId,
                state.SummaryText,
                state.ShadowedItemCount,
                state.ShadowedTokenCount,
                state.Manual,
                state.Error is not null,
                record.Time,
                record.Seq);
            state.Row = row;
            add(row);
            return;
        }

        RefreshCompactionRow(state);
    }

    /// <summary>Processes the log-only <c>compaction/*</c> events that drive the compaction card state machine.</summary>
    private void ApplyCompactionEvent(
        SessionEventRecord record,
        Action<ChatMessageViewModel> add,
        bool trackRuntime)
    {
        // compaction/prune carries no compactionId (it only prices a tool-result replacement); handle it
        // before the shared id guard so the accounting is not skipped.
        if (string.Equals(record.Type, "compaction/prune", StringComparison.Ordinal))
        {
            _prunedResultCount++;
            return;
        }

        if (record.Data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var compactionId = ReadString(record.Data, "compactionId");
        if (string.IsNullOrWhiteSpace(compactionId))
        {
            return;
        }

        var manual = !string.IsNullOrWhiteSpace(ReadString(record.Data, "sourceCommandId"));
        switch (record.Type)
        {
            case "compaction/start":
            {
                var state = GetOrCreateCompactionState(compactionId!, manual, ReadInt(record.Data, "turn"));
                if (manual)
                {
                    state.Manual = true;
                }

                if (trackRuntime)
                {
                    IsCompacting = true;
                }

                return;
            }

            case "compaction/summary":
            {
                var state = GetOrCreateCompactionState(compactionId!, manual, turn: null);
                state.SummarySeq = record.Seq;
                state.SummaryText = ReadCompactionSummary(record.Data);
                state.ShadowedItemCount = ReadArrayLength(record.Data, "shadowedSeqs");
                state.ShadowedTokenCount = ReadInt(record.Data, "shadowedTokenCount");
                if (state.Row is not null)
                {
                    RefreshCompactionRow(state);
                }

                return;
            }

            case "compaction/end":
            {
                var state = GetOrCreateCompactionState(compactionId!, manual, ReadInt(record.Data, "turn"));
                state.Error = ReadString(record.Data, "error");
                if (trackRuntime)
                {
                    IsCompacting = false;
                }

                // Failed attempts are logged by the host but never rendered as transcript rows (the card, when
                // the checkpoint exists, simply carries the failure state). Keeping a synthetic row would also
                // duplicate it when the checkpoint arrives from an older page.
                if (state.Row is not null)
                {
                    RefreshCompactionRow(state);
                }
                else if (!string.IsNullOrWhiteSpace(state.Error))
                {
                    DiagnosticsLog.Trace($"session {SessionId}: compaction {compactionId} failed without a checkpoint");
                }

                return;
            }

            default:
                return;
        }
    }

    private CompactionState GetOrCreateCompactionState(string compactionId, bool manual, int? turn)
    {
        if (_compactions.TryGetValue(compactionId, out var existing))
        {
            return existing;
        }

        var state = new CompactionState
        {
            CompactionId = compactionId,
            Manual = manual,
            Turn = turn
        };
        _compactions[compactionId] = state;
        return state;
    }

    private static void RefreshCompactionRow(CompactionState state)
    {
        state.Row?.ApplyCompactionState(
            state.SummaryText,
            state.ShadowedItemCount,
            state.ShadowedTokenCount,
            state.Manual,
            state.Error is not null);
    }

    private static string? ReadCompactionSummary(JsonElement data)
    {
        if (!data.TryGetProperty("summary", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(block, "type"), "text", StringComparison.Ordinal))
            {
                continue;
            }

            var text = ReadString(block, "text");
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            // DSH's compactSummary() joins the summary text blocks with an empty separator; keep the same
            // projection so the card matches the desktop UI (blocks already carry their own newlines).
            builder.Append(text);
        }

        return builder.Length == 0 ? null : builder.ToString().Trim();
    }

    private static int ReadArrayLength(JsonElement data, string property)
        => data.ValueKind == JsonValueKind.Object &&
           data.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    /// <summary>History pages are rendered in seq order with duplicate seqs collapsed.</summary>
    /// <summary>
    /// Trims a newest page (ascending) to a display-friendly event count. maxMessages on the wire is a turn
    /// budget, so a single page can hold far more events than the UI should build at once.
    /// </summary>
    private static (List<SessionEventRecord> Records, bool HadMore) TrimNewestPage(
        List<SessionEventRecord> records,
        int maxRows)
    {
        var limit = Math.Max(maxRows, 1) * 2;
        if (records.Count <= limit)
        {
            return (records, false);
        }

        // The first-screen budget is a *display row* budget. Model-only surface replacements and compaction
        // prune markers are invisible in the transcript, so they must not consume the budget and push real
        // rows out of the first screen (a compaction replay burst can otherwise eat most of the page).
        var countable = 0;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            if (CountsTowardDisplayBudget(records[index]))
            {
                countable++;
            }
        }

        if (countable <= limit)
        {
            return (records, false);
        }

        var counted = 0;
        var cut = 0;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            if (!CountsTowardDisplayBudget(records[index]))
            {
                continue;
            }

            counted++;
            if (counted >= limit)
            {
                cut = index;
                break;
            }
        }

        return (records.Skip(cut).ToList(), true);
    }

    /// <summary>
    /// Whether one raw record should consume the first-screen display budget. Invisible model-only events
    /// (tool-result/system replacements, compaction prune markers) do not; the compaction checkpoint still
    /// does because it anchors a visible card.
    /// </summary>
    private static bool CountsTowardDisplayBudget(SessionEventRecord record)
    {
        if (string.Equals(record.Type, "compaction/prune", StringComparison.Ordinal))
        {
            return false;
        }

        if (record.Type.StartsWith("compaction/", StringComparison.Ordinal))
        {
            return true;
        }

        if (!record.IsReplacementSurfaceEvent)
        {
            return true;
        }

        return TryReadCompactionCheckpoint(record, out _, out _);
    }

    private static List<SessionEventRecord> NormalizeHistory(IReadOnlyList<SessionEventRecord> records)
    {
        var seen = new HashSet<long>();
        var ordered = new List<SessionEventRecord>(records.Count);
        foreach (var record in records.OrderBy(record => record.Seq))
        {
            if (record.Seq > 0 && !seen.Add(record.Seq))
            {
                continue;
            }

            ordered.Add(record);
        }

        return ordered;
    }

    private void ApplyRecord(
        SessionEventRecord record,
        Action<ChatMessageViewModel> add,
        Func<string?, long?, ChatMessageViewModel?> findToolCall,
        bool trackRuntime)
    {
        // Session-log surface semantics (DSH log v4) are handled before ResolveTurn:
        //  - compaction/* events are log-only: they drive the compaction card/state machine, never a row.
        //  - replacement surface events are model-side updates: the human transcript keeps append events
        //    only, so a replacement must never create or rewrite a transcript row (and must not pollute
        //    _currentTurn with the old turn carried by a replayed replacement).
        if (record.Type.StartsWith("compaction/", StringComparison.Ordinal))
        {
            ApplyCompactionEvent(record, add, trackRuntime);
            return;
        }

        if (!record.IsTranscriptEvent)
        {
            ApplySurfaceReplacement(record, add, trackRuntime);
            return;
        }

        var turn = ResolveTurn(record);
        switch (record.Type)
        {
            case "user/message":
            {
                if (!IsRealUserMessage(record, out var text))
                {
                    CountHidden(trackRuntime);
                    return;
                }

                if (trackRuntime)
                {
                    // Promote the optimistic bubble for this prompt *in place*: the transcript position must
                    // not change, otherwise the durable row lands after the running turn's live rows and the
                    // turn fold (anchored on those rows) renders above the prompt that started it.
                    if (TakePendingUserPrompt(record, text) is { } resolved)
                    {
                        resolved.AdoptDurableUser(record.Seq, record.Time);
                        if (!resolved.HasImages)
                        {
                            AttachImageParts(resolved, record);
                        }

                        Raise(nameof(ShowEmptyMessages));
                        return;
                    }
                }

                // Durable replay (history page + follow snapshot after reconnect) must never duplicate a row
                // that is already on screen: the transcript keeps exactly one bubble per durable user message.
                if (HasDurableUserRow(record.Seq))
                {
                    return;
                }

                var userMessage = ChatMessageViewModel.CreateUser(text, record.Time).WithSeq(record.Seq);
                AttachImageParts(userMessage, record);
                add(userMessage);
                return;
            }

            case "assistant/attempt":
            {
                if (trackRuntime)
                {
                    ApplyAttemptDeltas(record);
                }
                else
                {
                    CountHidden(false);
                }

                return;
            }

            case "assistant/live-chunk":
            {
                if (trackRuntime)
                {
                    ApplyLiveChunk(record);
                }
                else
                {
                    CountHidden(false);
                }

                return;
            }

            case "assistant/message":
            {
                var text = AssistantText(record);
                var reasoning = AssistantReasoning(record);
                var live = _liveAssistant;
                FinishLiveAssistant(record);
                if (live is not null)
                {
                    live.Seq = record.Seq;
                    AttachImageParts(live, record);
                    return;
                }

                if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(reasoning) &&
                    DshContent.ImagesOf(record).Count == 0)
                {
                    CountHidden(trackRuntime);
                    return;
                }

                var assistant = ChatMessageViewModel.CreateAssistant(text, reasoning, record.Time, record.Seq);
                assistant.Turn = turn;
                assistant.Step = ReadInt(record.Data, "step") ?? 0;
                AttachImageParts(assistant, record);
                add(assistant);
                return;
            }

            case "tool/call":
            {
                var (name, callId, arguments) = ParseToolCall(record);
                var tool = ChatMessageViewModel.CreateTool(name, callId, arguments, record.Time, record.Seq);
                tool.Turn = turn;
                tool.Step = ReadInt(record.Data, "step") ?? 0;
                tool.MarkStarted(record.Time);
                add(tool);
                // A page split (or a live reorder) can deliver the result before the call; backfill it now.
                if (!string.IsNullOrEmpty(callId) &&
                    _pendingToolResults.Remove(callId!, out var pendingResult))
                {
                    tool.ApplyToolResult(pendingResult.Output, pendingResult.Time, pendingResult.IsError);
                }

                return;
            }

            case "tool/result":
            {
                var (callId, output) = ParseToolResult(record);
                var isError = record.RawJson.Contains("\"isError\":true", StringComparison.OrdinalIgnoreCase) ||
                              record.RawJson.Contains("\"kind\":\"error\"", StringComparison.OrdinalIgnoreCase);
                var target = findToolCall(callId, record.PrimarySourceSeq);
                if (target is not null)
                {
                    // Duplicate results (reconnect replay, repeated follow snapshot) must not re-apply text/images.
                    if (target.HasResultApplied)
                    {
                        return;
                    }

                    target.ApplyToolResult(output, record.Time, isError);
                    AttachImageParts(target, record);
                    return;
                }

                // The matching tool/call is not in the loaded window (page boundary) or has not arrived yet.
                // Never materialise a Turn=0 placeholder row: it would escape the turn fold and render after
                // the final answer / file card. Stash it and backfill when the call becomes visible.
                if (!string.IsNullOrEmpty(callId))
                {
                    StashPendingToolResult(callId!, output, record.Time, isError);
                }

                return;
            }

            case "turn/start":
            {
                _currentTurn = turn;
                _turnEndTimes.Remove(turn);
                if (trackRuntime)
                {
                    IsRunning = true;
                    StopRequested = false;
                }

                var startRow = ChatMessageViewModel.CreateSystem(TurnLabel(record, "▶ 新一轮开始"), record.Time, seq: record.Seq);
                startRow.Turn = turn;
                startRow.IsTurnBoundary = true;
                add(startRow);
                return;
            }

            case "turn/end":
            {
                // Settle a still-streaming assistant row into this turn before closing it; otherwise a row that
                // never received a turn (or kept a stale one) could be folded into the wrong turn or dropped.
                if (_liveAssistant is { } liveRow && turn > 0 && liveRow.Turn != turn)
                {
                    liveRow.Turn = turn;
                }

                FinishLiveAssistant(null);
                _currentTurn = turn;
                _turnEndTimes[turn] = record.Time;
                if (trackRuntime)
                {
                    IsRunning = false;
                    StopRequested = false;
                    Hub.DismissForSession(SessionId, "该请求已在桌面处理");
                }

                var (reasonKind, errorMessage, errorCode) = ReadTurnReason(record);
                if (string.Equals(reasonKind, "error", StringComparison.OrdinalIgnoreCase))
                {                    var detail = string.IsNullOrWhiteSpace(errorMessage) ? "本轮以错误结束" : errorMessage!;
                    var text = "⚠ 本轮失败: " + Truncate(detail, 300);
                    if (!string.IsNullOrWhiteSpace(errorCode))
                    {
                        text += $"（code: {errorCode}）";
                    }

                    var errorRow = ChatMessageViewModel.CreateSystem(text, record.Time, isError: true, seq: record.Seq);
                    errorRow.Turn = turn;
                    add(errorRow);
                    if (trackRuntime)
                    {
                        ErrorMessage = "本轮运行失败: " + Truncate(detail, 300);
                    }

                    return;
                }

                var endRow = ChatMessageViewModel.CreateSystem(TurnLabel(record, "■ 本轮结束"), record.Time, seq: record.Seq);
                endRow.Turn = turn;
                endRow.IsTurnBoundary = true;
                add(endRow);
                if (trackRuntime)
                {
                    Services.PlatformIntegration.NotifyTurnFinished(
                        Title,
                        "本轮运行已结束，点按查看结果。",
                        Hub.ActiveSessionId == SessionId);
                }

                return;
            }

            case "deliverables/presented":
            {
                var files = FileChangeViewModel.ParsePresentedEvent(record.Data);
                if (files.Count == 0)
                {
                    CountHidden(trackRuntime);
                    return;
                }

                var artifact = ChatMessageViewModel.CreateArtifacts(files, record.Time, turn, record.Seq);
                add(artifact);
                return;
            }

            case "workspace/changes":
            {
                // The durable event only names the turn; the host serves the changed-file list out of band.
                // The turn's file card is derived from the file-writing tool calls instead, so this marker
                // adds no transcript row of its own.
                CountHidden(trackRuntime);
                return;
            }

            case "system/message":
            {
                // The agent system prompt is routed through system/message; it is not conversation content.
                CountHidden(trackRuntime);
                return;
            }

            case "session/title":
            {
                var title = ReadString(record.Data, "title");
                if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(_title))
                {
                    Title = title!;
                }

                CountHidden(trackRuntime);
                return;
            }

            case "step/end":
            {
                CountHidden(trackRuntime);
                return;
            }

            case "model/selection":
            {
                // Durable per-session selection written by session/selectModel (our own choice, another phone,
                // or the desktop GUI): keep the chip and an open panel in sync without an extra RPC.
                ApplyModelSelectionEvent(record);
                CountHidden(trackRuntime);
                return;
            }

            default:
                CountHidden(trackRuntime);
                return;
        }
    }

    // ---- Turn folding ---------------------------------------------------------------------------

    /// <summary>Turn number for an event: explicit <c>turn</c> field, nested message turn, else current turn.</summary>
    private int ResolveTurn(SessionEventRecord record)
    {
        var turn = ReadInt(record.Data, "turn") ?? 0;
        if (turn <= 0 && record.Data.ValueKind == JsonValueKind.Object &&
            record.Data.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object)
        {
            turn = ReadInt(message, "turn") ?? 0;
        }

        if (turn > 0)
        {
            _currentTurn = turn;
            return turn;
        }

        return _currentTurn;
    }

    /// <summary>Appends a batch of history rows and projects the timeline exactly once.</summary>
    private void AddMessagesBatched(List<ChatMessageViewModel> messages)
    {
        var started = Stopwatch.GetTimestamp();
        _suspendTimelineRebuild = true;
        try
        {
            foreach (var message in messages)
            {
                Messages.Add(message);
            }
        }
        finally
        {
            _suspendTimelineRebuild = false;
        }

        RebuildTimeline();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed > 80 || messages.Count > 40)
        {
            DiagnosticsLog.Trace($"perf: add batch n={messages.Count} total={Messages.Count} ms={elapsed:F0}");
        }
    }

    /// <summary>
    /// Rebuilds <see cref="Timeline"/>: user/system rows stay as-is, each assistant turn becomes one fold
    /// (reasoning + tool calls + step rows) with only the final answer rendered in the main transcript.
    /// </summary>
    private void RebuildTimeline()
    {
        var started = Stopwatch.GetTimestamp();
        var byTurn = new Dictionary<int, List<ChatMessageViewModel>>();
        foreach (var message in Messages)
        {
            if (message.Turn <= 0 || message.IsUser || (message.IsSystem && message.IsError))
            {
                continue;
            }

            if (!byTurn.TryGetValue(message.Turn, out var list))
            {
                list = new List<ChatMessageViewModel>();
                byTurn[message.Turn] = list;
            }

            list.Add(message);
        }

        var desired = new List<object>(Messages.Count);
        var emitted = new HashSet<int>();
        var runningTurn = IsRunning ? _currentTurn : 0;

        void AppendFold(int turn)
        {
            if (!emitted.Add(turn))
            {
                return;
            }

            if (!_timelineTurns.TryGetValue(turn, out var turnViewModel))
            {
                turnViewModel = new ChatTurnViewModel(turn);
                _timelineTurns[turn] = turnViewModel;
            }

            var turnMessages = byTurn[turn];
            ChatMessageViewModel? final = null;
            for (var index = turnMessages.Count - 1; index >= 0; index--)
            {
                if (turnMessages[index].IsAssistant && !string.IsNullOrWhiteSpace(turnMessages[index].TargetText))
                {
                    final = turnMessages[index];
                    break;
                }
            }

            var process = new List<ChatMessageViewModel>(turnMessages.Count);
            foreach (var item in turnMessages)
            {
                // The turn/step separator rows are redundant inside the fold (its header carries the state).
                // Artifact rows feed the turn's file card and are never rendered as process rows.
                if (ReferenceEquals(item, final) || item.IsTurnBoundary || item.IsArtifact ||
                    (item.IsSystem && (item.Text.StartsWith("▶", StringComparison.Ordinal) ||
                                       item.Text.StartsWith("■", StringComparison.Ordinal))))
                {
                    continue;
                }

                process.Add(item);
            }

            var (files, fileOverflow) = BuildTurnFileChanges(turnMessages);
            var startedAt = turnMessages.Count > 0 ? turnMessages.Min(item => item.Time) : 0;
            var endedAt = _turnEndTimes.TryGetValue(turn, out var endTime) ? endTime : 0;
            var running = IsRunning && turn == runningTurn && endedAt == 0;
            turnViewModel.Update(process, final, running, startedAt, endedAt, final?.ReasoningText, files, fileOverflow);
            desired.Add(turnViewModel);
        }

        foreach (var message in Messages)
        {
            // Artifact rows only contribute to the turn's file card; they are not transcript rows.
            if (message.IsArtifact)
            {
                continue;
            }

            if (message.Turn <= 0 || message.IsUser || (message.IsSystem && message.IsError))
            {
                // Top-level rows are user prompts, system errors and compaction cards. Anything else with no
                // turn attribution is a projection bug (e.g. a tool result whose call was never matched);
                // drop it instead of letting it render after the turn fold's final answer / file card.
                if (message.Turn <= 0 &&
                    !message.IsUser &&
                    !(message.IsSystem && message.IsError) &&
                    message.Kind != ChatMessageKind.Compaction)
                {
                    _suppressedProjectionRows++;
                    continue;
                }

                desired.Add(message);
                continue;
            }

            // Turn banners (`▶ 新一轮开始` / `■ 本轮结束`) are grouping/timing markers only. They are never
            // rendered directly and must never anchor the fold: the durable `turn/start` is logged *before*
            // the user/message that opened the turn, so anchoring there would draw the fold above its prompt.
            if (message.IsTurnBoundary)
            {
                // ... except right after a prompt the user just sent (live send flow): the optimistic bubble
                // already sits above the banner, so showing the running fold there keeps the "正在执行中"
                // header instant. In history replay a banner is followed by the prompt *, not preceded by it.
                if (message.Text.StartsWith("▶", StringComparison.Ordinal) &&
                    desired.Count > 0 && desired[^1] is ChatMessageViewModel { IsUser: true })
                {
                    AppendFold(message.Turn);
                }

                continue;
            }

            AppendFold(message.Turn);
        }

        // Stop timers of turns that are no longer in the transcript (reload / session switch).
        foreach (var stale in _timelineTurns.Keys.Where(turn => !emitted.Contains(turn)).ToList())
        {
            _timelineTurns[stale].Stop();
            _timelineTurns.Remove(stale);
        }

        SyncTimeline(desired);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed > 80)
        {
            DiagnosticsLog.Trace(
                $"perf: rebuild timeline rows={desired.Count} messages={Messages.Count} turns={_timelineTurns.Count} ms={elapsed:F0}");
        }
#if DEBUG
        // Debug-only transcript structure trace (Android logcat / desktop console). Only structure changes are
        // printed; release builds and normal debug use keep the console quiet.
        if (desired.Count != _traceLastTimelineCount)
        {
            _traceLastTimelineCount = desired.Count;
            var summary = string.Join(
                " | ",
                desired.Take(80).Select(item => item switch
                {
                    ChatTurnViewModel turn =>
                        $"TURN#{turn.Turn}[proc={turn.Process.Count},final={turn.Final.Count},expanded={turn.IsExpanded},running={turn.IsRunning}]",
                    ChatMessageViewModel msg =>
                        $"{msg.Kind}#{msg.Seq}(turn={msg.Turn},user={msg.IsUser},text={(string.IsNullOrEmpty(msg.Text) ? string.Empty : msg.Text[..Math.Min(18, msg.Text.Length)].Replace('\n', ' '))})",
                    _ => item.GetType().Name,
                }));
            Console.WriteLine(
                $"[Timeline] {SessionId} count={desired.Count} surfaceReplaced={_surfaceReplacementCount} " +
                $"pruned={_prunedResultCount} suppressed={_suppressedProjectionRows} :: {summary}");
        }
#endif
    }

#if DEBUG
    private int _traceLastTimelineCount = -1;
#endif

    /// <summary>
    /// Tool names that write files. Kept explicit on purpose: a heuristic over argument shape would also match
    /// read-only tools (<c>read</c> carries <c>file_path</c>) and invent file rows that never happened.
    /// </summary>
    private static readonly HashSet<string> FileWriteToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit",
        "write",
        "apply_patch",
        "multi_edit",
        "create_file",
        "write_file",
        "str_replace_editor",
        "notebook_edit",
    };

    /// <summary>Files shown per turn before the card collapses the remainder into a count.</summary>
    private const int MaxTurnFileRows = 12;

    /// <summary>
    /// Derives the turn's file card from its rows: <c>present</c> / <c>deliverables/presented</c> become
    /// "produced" entries, file-writing tool calls become "modified" entries with an approximate line delta.
    /// The projection is a pure function of the rows, so history replay, "load older" and live streaming all
    /// agree without extra mutable state.
    /// </summary>
    private static (List<FileChangeViewModel> Files, int Overflow) BuildTurnFileChanges(
        List<ChatMessageViewModel> turnMessages)
    {
        var produced = new Dictionary<string, FileChangeViewModel>(StringComparer.OrdinalIgnoreCase);
        var producedOrder = new List<string>();
        var modified = new Dictionary<string, (int Added, int Deleted)>(StringComparer.OrdinalIgnoreCase);
        var modifiedOrder = new List<string>();

        static string KeyOf(string path) => path.Trim().Replace('\\', '/');

        // `present` records workspace-relative paths while the durable deliverables event records absolute
        // ones; the same file must still surface as one row.
        static bool SameFile(string left, string right)
            => string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
               left.EndsWith("/" + right, StringComparison.OrdinalIgnoreCase) ||
               right.EndsWith("/" + left, StringComparison.OrdinalIgnoreCase);

        void AddProduced(FileChangeViewModel file)
        {
            var key = KeyOf(file.Path);
            if (key.Length == 0)
            {
                return;
            }

            if (produced.TryGetValue(key, out var exact))
            {
                exact.SetDescription(file.Description);
                return;
            }

            foreach (var existingKey in producedOrder)
            {
                if (!SameFile(existingKey, key))
                {
                    continue;
                }

                var existing = produced[existingKey];
                if (key.Length > existingKey.Length)
                {
                    // Prefer the absolute form (from deliverables/presented) over the tool call's relative one.
                    var replacement = FileChangeViewModel.Produced(file.Path);
                    replacement.SetDescription(existing.Description);
                    replacement.SetDescription(file.Description);
                    var index = producedOrder.IndexOf(existingKey);
                    produced.Remove(existingKey);
                    producedOrder[index] = key;
                    produced[key] = replacement;
                }
                else
                {
                    existing.SetDescription(file.Description);
                }

                return;
            }

            produced[key] = file;
            producedOrder.Add(key);
        }

        void AddModified(string path, int added, int deleted)
        {
            var key = KeyOf(path);
            if (key.Length == 0)
            {
                return;
            }

            if (modified.TryGetValue(key, out var totals))
            {
                modified[key] = (totals.Added + Math.Max(0, added), totals.Deleted + Math.Max(0, deleted));
                return;
            }

            foreach (var existingKey in modifiedOrder)
            {
                if (SameFile(existingKey, key))
                {
                    totals = modified[existingKey];
                    modified[existingKey] = (totals.Added + Math.Max(0, added), totals.Deleted + Math.Max(0, deleted));
                    return;
                }
            }

            modified[key] = (Math.Max(0, added), Math.Max(0, deleted));
            modifiedOrder.Add(key);
        }

        foreach (var message in turnMessages)
        {
            if (message.IsArtifact)
            {
                foreach (var file in message.Files)
                {
                    AddProduced(file);
                }

                continue;
            }

            if (!message.IsTool)
            {
                continue;
            }

            if (string.Equals(message.ToolName, "present", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in FileChangeViewModel.ParsePresentedToolCall(message.RawArguments))
                {
                    AddProduced(file);
                }

                continue;
            }

            if (!FileWriteToolNames.Contains(message.ToolName))
            {
                continue;
            }

            if (FileChangeViewModel.TryParseToolCall(message.ToolName, message.RawArguments) is { } change)
            {
                AddModified(change.Path, change.Added, change.Deleted);
            }
        }

        (int Added, int Deleted)? TakeModified(string producedKey)
        {
            var match = modifiedOrder.FirstOrDefault(candidate => SameFile(candidate, producedKey));
            if (match is null || !modified.TryGetValue(match, out var totals))
            {
                return null;
            }

            modified.Remove(match);
            modifiedOrder.Remove(match);
            return totals;
        }

        var files = new List<FileChangeViewModel>(producedOrder.Count + modifiedOrder.Count);
        foreach (var key in producedOrder)
        {
            var file = produced[key];
            if (TakeModified(key) is { } totals)
            {
                file.AddChange(totals.Added, totals.Deleted);
            }

            files.Add(file);
        }

        foreach (var key in modifiedOrder)
        {
            if (!modified.TryGetValue(key, out var totals))
            {
                continue;
            }

            var file = FileChangeViewModel.Modified(key);
            file.AddChange(totals.Added, totals.Deleted);
            files.Add(file);
        }

        if (files.Count <= MaxTurnFileRows)
        {
            return (files, 0);
        }

        var overflow = files.Count - MaxTurnFileRows;
        return (files.GetRange(0, MaxTurnFileRows), overflow);
    }

    private void SyncTimeline(List<object> desired)
    {
        var common = Math.Min(Timeline.Count, desired.Count);
        var firstDifference = 0;
        while (firstDifference < common && ReferenceEquals(Timeline[firstDifference], desired[firstDifference]))
        {
            firstDifference++;
        }

        if (firstDifference == Timeline.Count && firstDifference == desired.Count)
        {
            return;
        }

        for (var index = Timeline.Count - 1; index >= firstDifference; index--)
        {
            Timeline.RemoveAt(index);
        }

        for (var index = firstDifference; index < desired.Count; index++)
        {
            Timeline.Add(desired[index]);
        }
    }

    // ---- Streaming assistant assembly ---------------------------------------------------------

    private void ApplyAttemptDeltas(SessionEventRecord record)
    {
        var turn = ReadInt(record.Data, "turn") ?? 0;
        var step = ReadInt(record.Data, "step") ?? 0;
        if (!record.Data.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // If live chunks already produced text for this attempt, the durable replay would duplicate it.
        if (_liveAssistant is not null &&
            _liveAssistant.Turn == turn &&
            _liveAssistant.Step == step &&
            _liveAssistant.TargetText.Length > 0)
        {
            return;
        }

        var hasDelta = false;
        foreach (var recordItem in stream.EnumerateArray())
        {
            if (recordItem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadString(recordItem, "type");
            switch (type)
            {
                case "chunk":
                    if (recordItem.TryGetProperty("chunk", out var chunk))
                    {
                        hasDelta |= AppendChunk(chunk, turn, step, record.Time);
                    }

                    break;
                case "text-chunks":
                    if (recordItem.TryGetProperty("texts", out var texts) && texts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in texts.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.String &&
                                AppendAssistantText(part.GetString()!, turn, step, record.Time))
                            {
                                hasDelta = true;
                            }
                        }
                    }

                    break;
                case "reasoning-chunks":
                    if (recordItem.TryGetProperty("texts", out var reasoningTexts) && reasoningTexts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in reasoningTexts.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.String && EnsureLiveAssistant(turn, step, record.Time) is { } live)
                            {
                                live.AppendReasoningDelta(part.GetString()!);
                            }
                        }
                    }

                    break;
            }
        }

        if (hasDelta)
        {
            StartRevealPump();
            TranscriptUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyLiveChunk(SessionEventRecord record)
    {
        var turn = ReadInt(record.Data, "turn") ?? 0;
        var step = ReadInt(record.Data, "step") ?? 0;
        if (!record.Data.TryGetProperty("chunk", out var chunk))
        {
            return;
        }

        if (AppendChunk(chunk, turn, step, record.Time))
        {
            StartRevealPump();
            TranscriptUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool AppendChunk(JsonElement chunk, int turn, int step, long time)
    {
        if (chunk.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var changed = false;
        var type = ReadString(chunk, "type");
        switch (type)
        {
            case "text-delta":
            case "text":
                if (ReadString(chunk, "text") is { Length: > 0 } text)
                {
                    changed = AppendAssistantText(text, turn, step, time);
                }

                break;
            case "reasoning-delta":
            case "reasoning":
                if (ReadString(chunk, "text") is { Length: > 0 } reasoning &&
                    EnsureLiveAssistant(turn, step, time) is { } live)
                {
                    live.AppendReasoningDelta(reasoning);
                    changed = true;
                }

                break;
        }

        return changed;
    }

    private bool AppendAssistantText(string delta, int turn, int step, long time)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return false;
        }

        var live = EnsureLiveAssistant(turn, step, time);
        live.AppendTextDelta(delta);
        return true;
    }

    private ChatMessageViewModel EnsureLiveAssistant(int turn, int step, long time)
    {
        // Live deltas normally carry the turn, but a defensive fallback keeps the row inside the running turn's
        // fold if a host ever omits it (a Turn=0 assistant row would escape the fold and render at the bottom).
        if (turn <= 0)
        {
            turn = _currentTurn;
        }

        if (_liveAssistant is not null &&
            (_liveAssistant.Turn != turn || _liveAssistant.Step != step || _liveAssistant.IsStreaming == false))
        {
            _liveAssistant = null;
        }

        if (_liveAssistant is not null)
        {
            return _liveAssistant;
        }

        var message = ChatMessageViewModel.CreateStreamingAssistant(turn, step, time);
        _liveAssistant = message;
        Messages.Add(message);
        Raise(nameof(ShowEmptyMessages));
        return message;
    }

    private void FinishLiveAssistant(SessionEventRecord? finalRecord)
    {
        var live = _liveAssistant;
        if (live is null)
        {
            return;
        }

        if (finalRecord is not null)
        {
            var text = AssistantText(finalRecord);
            var reasoning = AssistantReasoning(finalRecord);
            live.ApplyFinalText(text, reasoning);
            live.Seq = finalRecord.Seq;
            // The settlement event is authoritative for placement: correct a stale or missing turn/step so the
            // row cannot be folded into the wrong turn (or dropped by the top-level whitelist when Turn is 0).
            var finalTurn = ReadInt(finalRecord.Data, "turn") ?? 0;
            if (finalTurn > 0 && live.Turn != finalTurn)
            {
                live.Turn = finalTurn;
            }

            var finalStep = ReadInt(finalRecord.Data, "step") ?? 0;
            if (finalStep > 0 && live.Step != finalStep)
            {
                live.Step = finalStep;
            }
        }
        else
        {
            // No settlement event (cancelled/aborted): keep whatever was streamed.
            live.ApplyFinalText(string.Empty, null);
        }

        StartRevealPump();
        _liveAssistant = null;
    }

    private void StartRevealPump()
    {
        if (!_revealTimer.IsEnabled)
        {
            _revealTimer.Start();
        }
    }

    private void PumpReveal()
    {
        var started = Stopwatch.GetTimestamp();
        var stillRevealing = false;
        var changed = false;
        var streamingMessages = 0;
        var pendingChars = 0;
        foreach (var message in Messages)
        {
            if (!message.IsStreaming)
            {
                continue;
            }

            streamingMessages++;
            pendingChars += Math.Max(0, message.TargetText.Length - message.Text.Length);
            if (!message.IsRevealComplete)
            {
                var remaining = message.TargetText.Length - message.Text.Length;
                message.RevealStep(Math.Max(2, Math.Min(200, (remaining / 5) + 2)));
                stillRevealing = true;
                changed = true;
                continue;
            }

            if (message.CanRetireStreaming)
            {
                message.FinishReveal();
                changed = true;
            }
        }

        if (!stillRevealing)
        {
            _revealTimer.Stop();
        }

        if (changed)
        {
            TranscriptUpdated?.Invoke(this, EventArgs.Empty);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed > 40)
            {
                DiagnosticsLog.Trace(
                    $"perf: reveal tick ms={elapsed:F0} streaming={streamingMessages} pendingChars={pendingChars} messages={Messages.Count}");
            }
        }
    }

    // ---- Event parsing helpers ----------------------------------------------------------------

    private static bool IsRealUserMessage(SessionEventRecord record, out string text)
    {
        text = DshContent.TextOf(record);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = TextOf(record);
        }

        var imageCount = DshContent.ImagesOf(record).Count;
        if (string.IsNullOrWhiteSpace(text) && imageCount == 0)
        {
            return false;
        }

        var sourceKind = record.Data.ValueKind == JsonValueKind.Object &&
                         record.Data.TryGetProperty("source", out var source) &&
                         source.ValueKind == JsonValueKind.Object
            ? ReadString(source, "kind")
            : null;
        if (!string.Equals(sourceKind, "user", StringComparison.Ordinal))
        {
            return false;
        }

        return !LooksLikeInjectedContext(text);
    }

    private static bool LooksLikeInjectedContext(string text)
        => text.Contains("Current runtime context.", StringComparison.Ordinal) ||
           text.Contains("<system-reminder>", StringComparison.Ordinal) ||
           text.Contains("Time sampled while preparing", StringComparison.Ordinal);

    private static string TextOf(SessionEventRecord record)
    {
        var text = record.TextOf();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        if (record.Data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "text", "output", "result", "message" })
            {
                if (!record.Data.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!.Trim();
                }

                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    var joined = JoinContentBlocks(content, "text");
                    if (!string.IsNullOrWhiteSpace(joined))
                    {
                        return joined;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static string AssistantText(SessionEventRecord record)
    {
        var partsText = DshContent.TextOf(record);
        if (!string.IsNullOrWhiteSpace(partsText))
        {
            return partsText;
        }

        if (MessageContent(record) is { } content)
        {
            var text = JoinContentBlocks(content, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return TextOf(record);
    }

    private static string? AssistantReasoning(SessionEventRecord record)
    {
        if (MessageContent(record) is not { } content)
        {
            return null;
        }

        var reasoning = JoinContentBlocks(content, "reasoning");
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            reasoning = JoinContentBlocks(content, "thinking");
        }

        return string.IsNullOrWhiteSpace(reasoning) ? null : reasoning;
    }

    private static JsonElement? MessageContent(SessionEventRecord record)
    {
        if (record.Data.ValueKind == JsonValueKind.Object &&
            record.Data.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            return content;
        }

        return null;
    }

    private static string JoinContentBlocks(JsonElement content, string blockType)
    {
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (block.TryGetProperty("type", out var type) &&
                type.GetString() == blockType &&
                block.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value!);
                }
            }
        }

        return string.Join("\n\n", parts).Trim();
    }

    private static (string Name, string? CallId, string Arguments) ParseToolCall(SessionEventRecord record)
    {
        if (record.Data.ValueKind != JsonValueKind.Object)
        {
            return ("工具", null, record.RawJson);
        }

        var name = ReadString(record.Data, "name")
                   ?? ReadString(record.Data, "toolName")
                   ?? ReadString(record.Data, "tool")
                   ?? "工具";
        var callId = ReadString(record.Data, "callId")
                     ?? ReadString(record.Data, "toolCallId")
                     ?? ReadString(record.Data, "id");

        string arguments;
        if (record.Data.TryGetProperty("arguments", out var argumentsElement))
        {
            arguments = argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString() ?? string.Empty
                : argumentsElement.GetRawText();
        }
        else if (record.Data.TryGetProperty("input", out var inputElement))
        {
            arguments = inputElement.GetRawText();
        }
        else
        {
            arguments = string.Empty;
        }

        return (name, callId, arguments);
    }

    private static (string? CallId, string Output) ParseToolResult(SessionEventRecord record)
    {
        string? callId = null;
        if (record.Data.ValueKind == JsonValueKind.Object)
        {
            callId = ReadString(record.Data, "toolCallId") ?? ReadString(record.Data, "callId");
            if (callId is null &&
                record.Data.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("source", out var source) &&
                source.ValueKind == JsonValueKind.Object)
            {
                callId = ReadString(source, "callId");
            }
        }

        var output = TextOf(record);
        if (string.IsNullOrWhiteSpace(output) && record.Data.ValueKind == JsonValueKind.Object)
        {
            if (record.Data.TryGetProperty("message", out var message))
            {
                output = message.GetRawText();
            }
        }

        return (callId, string.IsNullOrWhiteSpace(output) ? record.RawJson : output);
    }

    private static (string? Kind, string? Error, string? Code) ReadTurnReason(SessionEventRecord record)
    {
        if (record.Data.ValueKind != JsonValueKind.Object ||
            !record.Data.TryGetProperty("reason", out var reason) ||
            reason.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        var kind = ReadString(reason, "kind");
        string? message = null;
        string? code = null;
        if (reason.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            message = ReadString(error, "message");
            code = ReadString(error, "code");
        }

        message ??= ReadString(reason, "message");
        return (kind, message, code);
    }

    private static string TurnLabel(SessionEventRecord record, string prefix)
    {
        var turn = ReadInt(record.Data, "turn") ?? 0;
        var reason = ReadTurnReason(record).Kind;
        var suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})";
        return turn > 0 ? $"{prefix} · turn {turn}{suffix}" : prefix;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String => kind.GetString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// Matches a durable <c>user/message</c> against an optimistic bubble and hands that bubble back so the
    /// pending row can be promoted in place (keeping its transcript position). Matching prefers the echoed
    /// <c>source.rpcId</c> (client-minted request id); hosts that do not echo ids (demo) fall back to an exact
    /// text match inside the timeout window.
    /// </summary>
    /// <returns>The optimistic bubble that the durable row should replace in place, or <c>null</c>.</returns>
    private ChatMessageViewModel? TakePendingUserPrompt(SessionEventRecord record, string text)
    {
        if (_pendingUserPrompts.Count == 0)
        {
            return null;
        }

        var rpcId = ReadSourceRpcId(record);
        PendingUserPrompt? match = null;

        if (!string.IsNullOrWhiteSpace(rpcId))
        {
            match = _pendingUserPrompts.FirstOrDefault(pending =>
                !pending.Matched &&
                !string.IsNullOrWhiteSpace(pending.RequestId) &&
                string.Equals(pending.RequestId, rpcId, StringComparison.OrdinalIgnoreCase));
        }

        // Fallback per product guidance: an exact text match within the pending window retires the optimistic
        // bubble when the id path cannot decide (steer has no client id, or a host did not echo ours).
        if (match is null)
        {
            var candidates = _pendingUserPrompts
                .Where(pending =>
                    !pending.Matched &&
                    DateTimeOffset.Now - pending.SentAt <= PendingPromptTimeout &&
                    string.Equals(pending.Text.Trim(), text.Trim(), StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 1)
            {
                match = candidates[0];
            }
        }

        if (match is null)
        {
            return null;
        }

        match.Matched = true;
        _pendingUserPrompts.Remove(match);

        if (_pendingUserPrompts.Count == 0)
        {
            _pendingPromptTimer.Stop();
        }

        return match.Bubble;
    }

    /// <summary>True when a durable user row with this seq is already in the transcript (replay overlap).</summary>
    private bool HasDurableUserRow(long seq)
    {
        if (seq <= 0)
        {
            return false;
        }

        foreach (var message in Messages)
        {
            if (message.IsUser && message.Seq == seq)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The request id DSH echoes back as <c>data.source.rpcId</c> for a submitted prompt.</summary>
    private static string? ReadSourceRpcId(SessionEventRecord record)
    {
        if (record.Data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (record.Data.TryGetProperty("source", out var source) &&
            source.ValueKind == JsonValueKind.Object)
        {
            var id = ReadString(source, "rpcId") ?? ReadString(source, "requestId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return ReadString(record.Data, "rpcId") ?? ReadString(record.Data, "requestId");
    }

    private void CountHidden(bool trackRuntime)
    {
        if (trackRuntime && HiddenEvents < 10000)
        {
            HiddenEvents = HiddenEvents + 1;
        }
    }

    public int HiddenEvents
    {
        get => _hiddenEvents;
        private set
        {
            if (SetProperty(ref _hiddenEvents, value))
            {
                Raise(nameof(HasHiddenEvents), nameof(HiddenEventsText));
            }
        }
    }

    public bool HasHiddenEvents => HiddenEvents > 0;

    public string HiddenEventsText => $"另有 {HiddenEvents} 条运行事件（step/job 等）未在时间线展示";

    private void RaiseState()
    {
        Raise(
            nameof(IsIdle),
            nameof(StatusText),
            nameof(CanSend),
            nameof(CanStop),
            nameof(ShowEmptyMessages),
            nameof(ShowSessionStatusRow),
            nameof(SendButtonText),
            nameof(ComposerHint));
    }

    private static bool IsSubagentSession(Exception ex)
        => ex is DshRemoteException { Code: "session/agent-busy" } ||
           ex.Message.Contains("agent-busy", StringComparison.OrdinalIgnoreCase);

    private static bool IsResyncException(Exception ex)
        => ex is SessionFollowResyncException ||
           ex.GetType().Name.Contains("Resync", StringComparison.OrdinalIgnoreCase);

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "请求超时。",
        DshRemoteException remote when remote.Code is DshRemoteException.Codes.Unauthorized or DshRemoteException.Codes.Forbidden
            => "配对 token 失效或桌面拒绝连接，请重新配对。",
        DshRemoteException remote => remote.Message,
        HttpRequestException http when IsAuthFailure(http.Message) => "配对 token 失效或已轮换，请重新配对。",
        HttpRequestException http => "无法连接中继（" + http.Message + "）。请确认手机与桌面在同一局域网、relay 已启动。",
        _ => IsAuthFailure(ex.Message) ? "配对 token 失效或已轮换，请重新配对。" : ex.Message
    };

    private static bool IsAuthFailure(string? message)
        => !string.IsNullOrEmpty(message) &&
           (message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("403", StringComparison.Ordinal) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase));

    internal static string FormatCount(int count, string unit) => count.ToString(CultureInfo.CurrentCulture) + " " + unit;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cacheTimer.Stop();
        SaveCacheIfDirty();
        // Persist any pending composer text before the view-model (and its 2 s flush timer) goes away.
        _drafts?.Flush();
        _disposed = true;
        _client.StateChanged -= OnClientStateChanged;
        _stateTimer.Stop();
        _revealTimer.Stop();
        _pendingPromptTimer.Stop();
        _liveFlushTimer?.Stop();
        _liveFlushTimer = null;
        _liveQueue.Clear();
        StopFollowReconnectWatch();
        foreach (var turn in _timelineTurns.Values)
        {
            turn.Stop();
        }
        _timelineTurns.Clear();
        _pendingUserPrompts.Clear();
        PendingImages.Clear();
        SelectedImage = null;
        ReleaseImageCache();
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }

        var observeTask = _observeTask;
        if (observeTask is not null)
        {
            // Observe faults even if the bounded wait below leaves the task behind.
            _ = observeTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Never let a wedged follow pump keep the chat page (and the back button) hostage: the page
            // already navigated away, so cap the wait and let the canceled loop finish on its own.
            var bounded = Task.WhenAny(observeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            await bounded.ConfigureAwait(false);
            if (!observeTask.IsCompleted)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Session] {SessionId}: follow loop still running after dispose timeout; leaving it canceled");
            }
        }

        _cts.Dispose();
    }
}

internal static class ChatMessageViewModelExtensions
{
    public static ChatMessageViewModel WithSeq(this ChatMessageViewModel message, long seq)
    {
        message.Seq = seq;
        return message;
    }
}
