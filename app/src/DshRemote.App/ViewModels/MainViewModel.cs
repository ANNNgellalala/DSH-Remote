using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using DshRemote.App.Platform;
using DshRemote.App.Services;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

public enum AppPage
{
    Connect,
    Sessions,
    Chat,
    Desktops,
    Settings
}

/// <summary>
/// Root view-model: owns the connection, the session list and the currently open conversation.
/// Commands are safe to call from XAML; every failure lands in <see cref="ErrorMessage"/> instead of
/// escaping into the dispatcher.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly InteractionHub _hub = new();
    private readonly HashSet<string> _hiddenSubagentSessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _archivedSessionIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Sessions that already received a prompt from this client. The host's list snapshot may still mark
    /// them blank until the next refresh, and a "blank" row is what 「＋ 新建会话」 reopens — so this set
    /// keeps a just-used session from being reused as the workspace's new session.
    /// </summary>
    private readonly HashSet<string> _sessionsWithSentPrompt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _workspaceIdBySessionId = new(StringComparer.Ordinal);

    /// <summary>Unsent composer text per session, shared across chat switches (see <see cref="SessionDraftStore"/>).</summary>
    private readonly SessionDraftStore _drafts = new();
    private SessionCacheStore? _cacheStore;
    private bool _isShowingCachedSessions;
    private WorkspaceGroupViewModel? _archivedWorkspaceGroup;
    private WorkspaceGroupViewModel? _selectedWorkspaceGroup;
    private string? _selectedWorkspaceId;
    private bool _isArchivedExpanded = false;
    private string _toastText = string.Empty;
    private int _toastVersion;
    private IDshClient? _client;
    private SessionListViewModel? _selectedSession;
    private bool _showSessionModel = true;
    private AppPage _page = AppPage.Connect;
    private SessionViewModel? _currentSession;
    private bool _isBusy;
    private bool _isConnecting;
    private bool _isReconnecting;
    private bool _isConnected;
    private bool _isListingSessions;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private string _pairingPreview = string.Empty;
    private string _statusText = "未连接";
    private string _statusDetail = string.Empty;
    private bool _disposed;
    private bool _openingSession;
    private bool _returningToConnect;

    public MainViewModel()
    {
        Settings = new SettingsViewModel();
        Settings.Load();
        Settings.PropertyChanged += (_, _) => OnSettingsChanged();
        Profiles = new ProfilesViewModel(GetLegacyPairing) { ConnectRequested = ConnectFromPairingAsync };
        OpenDesktopsCommand = new AsyncRelayCommand(OpenDesktopsAsync);
        CloseDesktopsCommand = new AsyncRelayCommand(CloseDesktopsAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        CloseSettingsCommand = new RelayCommand(CloseSettings);
        _hub.InteractionStateChanged += (_, _) =>
            Raise(nameof(HasPendingInteractions), nameof(PendingInteractionText));
        _hub.ApprovalReceived += (_, approval) =>
        {
            var visible = Page == AppPage.Chat &&
                          string.Equals(CurrentSession?.SessionId, approval.SessionId, StringComparison.Ordinal);
            PlatformIntegration.NotifyApprovalNeeded(approval.ToolName, approval.ReasonText, approval.SessionId, visible);
        };
        _hub.QuestionReceived += (_, question) =>
        {
            var visible = Page == AppPage.Chat &&
                          string.Equals(CurrentSession?.SessionId, question.SessionId, StringComparison.Ordinal);
            var body = question.Items.Count > 0 ? question.Items[0].Text : "Agent 有问题需要你回答";
            PlatformIntegration.NotifyQuestionNeeded(body, question.SessionId, visible);
        };

        ConnectCommand = new AsyncRelayCommand(() => ConnectAsync(demo: false));
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        RefreshSessionsCommand = new AsyncRelayCommand(RefreshSessionsAsync);
        BackToListCommand = new AsyncRelayCommand(BackToListAsync);
        LoadPairingFromFileCommand = new AsyncRelayCommand(LoadPairingFromFileAsync);
        DemoPreviewCommand = new AsyncRelayCommand(() => ConnectAsync(demo: true));
        OpenSessionCommand = new AsyncRelayCommand(parameter =>
            parameter is SessionListViewModel item ? OpenSessionAsync(item) : Task.CompletedTask);
        ForgetPairingCommand = new RelayCommand(ForgetPairing);

        ArchiveSessionCommand = new AsyncRelayCommand(parameter =>
            parameter is SessionListViewModel item ? ArchiveSessionAsync(item) : Task.CompletedTask);
        UnarchiveSessionCommand = new AsyncRelayCommand(parameter =>
            parameter is SessionListViewModel item ? UnarchiveSessionAsync(item) : Task.CompletedTask);
        CreateSessionCommand = new AsyncRelayCommand(parameter =>
            parameter is WorkspaceGroupViewModel group ? CreateSessionInGroupAsync(group) : Task.CompletedTask);
        ToastCommand = new RelayCommand(() => ToastText = string.Empty);
        ToggleArchivedCommand = new RelayCommand(() => IsArchivedExpanded = !IsArchivedExpanded);

        StatusText = Settings.HasDesktopName ? "上次配对已保存 · 未连接" : "未连接";

        if (!string.IsNullOrWhiteSpace(AppHost.StartupPairingUri) &&
            DshPairingInfo.TryParse(AppHost.StartupPairingUri, out var startupPairing) &&
            startupPairing is not null)
        {
            Settings.ApplyPairing(startupPairing);
            var action = AppHost.StartupAction ?? string.Empty;
            _ = StartupConnectRealAsync(
                openFirst: action is "open-first" or "open-first-send",
                sendText: action == "open-first-send");
        }
        else if (!string.IsNullOrWhiteSpace(AppHost.StartupAction))
        {
            _ = RunStartupActionAsync(AppHost.StartupAction!);
        }
    }

    /// <summary>
    /// Called once by the shared <c>App</c> after the view model exists: restores the pairing from the
    /// platform secret store (Android Keystore) and attaches deep-link / notification-tap handlers.
    /// </summary>
    public async Task InitializePlatformAsync()
    {
        // Load saved desktops first: the secure-store probe below can be slow on first use (Keystore), and
        // the management page should not wait for it.
        _ = Profiles.InitializeAsync();

        try
        {
            var readPairing = PlatformIntegration.ReadPairingAsync();
            if (await Task.WhenAny(readPairing, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(true) ==
                readPairing)
            {
                var stored = await readPairing.ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(stored) && Settings.LoadFromSecretJson(stored))
                {
                    Raise(nameof(CanConnect));
                    RefreshPairingPreview();
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Platform] secure pairing read timed out; continuing.");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "读取安全存储失败: " + ex.Message;
        }

        PlatformIntegration.Wire(this);
        // Preferences (full-screen composer, last workspace) are owned by settings.json and are reloaded
        // after the secure-store pairing restore: that snapshot only carries connection fields, and reading
        // the live file last keeps a restart from rolling the settings page back.
        Settings.ReloadPreferences();
        // Picks up the pairing restored above (the earlier load may have run before the secure read).
        await Profiles.InitializeAsync(forceReload: true).ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(AppHost.StartupDeepLink))
        {
            var deepLink = AppHost.StartupDeepLink!;
            AppHost.StartupDeepLink = null;
            PlatformServices.DispatchDeepLink(deepLink, "desktop-smoke");
        }
    }

    /// <summary>Handles a <c>dshremote://pair?...</c> deep link: fill the form and connect immediately.</summary>
    public async Task ConnectFromDeepLinkAsync(string uri)
    {
        if (_disposed || string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        if (!DshPairingInfo.TryParse(uri, out var pairing, separateToken: Settings.Token) || pairing is null)
        {
            ErrorMessage = "配对链接无法识别，请在配对页手动粘贴。";
            Page = AppPage.Connect;
            return;
        }

        await ConnectFromPairingAsync(pairing).ConfigureAwait(true);
    }

    /// <summary>Connects to a pairing (used by deep links and by switching desktops on the management page).</summary>
    public async Task ConnectFromPairingAsync(DshPairingInfo pairing)
    {
        if (_disposed)
        {
            return;
        }

        await CloseCurrentSessionAsync().ConfigureAwait(true);
        Settings.ApplyPairing(pairing);
        Page = AppPage.Connect;
        await ConnectAsync(demo: false).ConfigureAwait(true);
    }

    private DshPairingInfo? GetLegacyPairing()
        => Settings.TryBuildPairing(out var pairing, out _) ? pairing : null;

    private async Task OpenDesktopsAsync()
    {
        if (Page != AppPage.Desktops)
        {
            _desktopsReturnPage = Page;
        }

        Page = AppPage.Desktops;
        await Profiles.InitializeAsync().ConfigureAwait(true);
    }

    private Task CloseDesktopsAsync()
    {
        Page = _desktopsReturnPage;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the dedicated settings page. Reachable from the session-list header; the page it was
    /// opened from is remembered so the back arrow returns there.
    /// </summary>
    private void OpenSettings()
    {
        if (Page != AppPage.Settings)
        {
            _settingsReturnPage = Page;
        }

        ErrorMessage = string.Empty;
        Settings.AutoApprove.Refresh(WorkspaceGroups);
        Page = AppPage.Settings;
    }

    private void CloseSettings()
    {
        // The remembered chat page can be gone (session closed / disconnected meanwhile); fall back to
        // the session list instead of showing an empty chat shell.
        var target = _settingsReturnPage;
        if (target == AppPage.Settings || (target == AppPage.Chat && CurrentSession is null))
        {
            target = AppPage.Sessions;
        }

        Page = target;
    }

    /// <summary>Opens a session requested by a notification tap (refreshes the list if needed).</summary>
    public async Task OpenSessionByIdAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || _disposed || _client is null || !IsConnected)
        {
            return;
        }

        var item = Sessions.FirstOrDefault(session =>
            string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
        if (item is null)
        {
            await LoadSessionsAsync().ConfigureAwait(true);
            item = Sessions.FirstOrDefault(session =>
                string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
        }

        if (item is null)
        {
            Page = AppPage.Sessions;
            ErrorMessage = _hiddenSubagentSessionIds.Contains(sessionId)
                ? "该会话是子代理（subagent）会话，需从父会话打开查看。"
                : "找不到会话: " + sessionId;
            return;
        }

        await OpenSessionAsync(item).ConfigureAwait(true);
    }

    /// <summary>System back navigation: closes the settings page, is consumed on the chat page, and is
    /// allowed through on the list/connect pages.</summary>
    public bool HandleBackRequest()
    {
        if (Page == AppPage.Settings)
        {
            CloseSettingsCommand.Execute(null);
            return true;
        }

        if (Page != AppPage.Chat)
        {
            return false;
        }

        BackToListCommand.Execute(null);
        return true;
    }

    private async Task StartupConnectRealAsync(bool openFirst, bool sendText)
    {        await ConnectAsync(demo: false);
        if (!openFirst)
        {
            return;
        }

        await OpenFirstSessionAsync();
        if (sendText && CurrentSession is not null)
        {
            await Task.Delay(900);
            CurrentSession.Draft = "hello from the phone";
            CurrentSession.SendCommand.Execute(null);
        }
    }

    /// <summary>Desktop-shell smoke helper (see <see cref="AppHost.StartupAction"/>).</summary>
    private async Task RunStartupActionAsync(string action)
    {
        try
        {
            switch (action)
            {
                case "demo":
                    await ConnectAsync(demo: true);
                    break;
                case "demo-chat":
                    await ConnectAsync(demo: true);
                    await Task.Delay(400);
                    await OpenFirstSessionAsync();
                    break;
                case "demo-chat-send":
                    await ConnectAsync(demo: true);
                    await Task.Delay(400);
                    await OpenFirstSessionAsync();
                    await Task.Delay(900);
                    if (CurrentSession is not null)
                    {
                        CurrentSession.Draft = "帮我把 Android 打包命令跑一遍，确认 APK 输出路径。";
                        CurrentSession.SendCommand.Execute(null);
                    }

                    break;
                case "demo-chat-approval":
                    await ConnectAsync(demo: true);
                    await Task.Delay(400);
                    await OpenFirstSessionAsync();
                    await Task.Delay(900);
                    if (CurrentSession is not null)
                    {
                        CurrentSession.Draft = "approval: 清理一下旧日志文件。";
                        CurrentSession.SendCommand.Execute(null);
                    }

                    break;
                case "demo-chat-question":
                    await ConnectAsync(demo: true);
                    await Task.Delay(400);
                    await OpenFirstSessionAsync();
                    await Task.Delay(900);
                    if (CurrentSession is not null)
                    {
                        CurrentSession.Draft = "question: 接下来用哪个构建配置？";
                        CurrentSession.SendCommand.Execute(null);
                    }

                    break;
                case "demo-chat-steer":
                    await ConnectAsync(demo: true);
                    await Task.Delay(400);
                    await OpenFirstSessionAsync();
                    await Task.Delay(900);
                    if (CurrentSession is not null)
                    {
                        CurrentSession.Draft = "先起一个 turn，用来测试 steer。";
                        CurrentSession.SendCommand.Execute(null);
                        await Task.Delay(1200);
                        CurrentSession.IsSteerMode = true;
                        CurrentSession.Draft = "插话：顺便确认一下 steer 气泡的展示。";
                        CurrentSession.SendCommand.Execute(null);
                    }

                    break;
                case "longshot-probe":
                {
                    // Automation probe (requirement 4/5): open the demo transcript, let layout/markdown/
                    // images settle, then render the long screenshot exactly like the in-app button does.
                    await ConnectAsync(demo: true);
                    await Task.Delay(600);
                    await OpenFirstSessionAsync();
                    await Task.Delay(1200);
                    var probePath = AppHost.LongShotProbeOutputPath;
                    if (string.IsNullOrWhiteSpace(probePath))
                    {
                        probePath = Path.Combine(Path.GetTempPath(), "dshremote-longshot-probe.png");
                    }

                    var exitCode = await LongShotProbe.RunAsync(probePath, AppHost.LongShotProbeLimits)
                        .ConfigureAwait(true);
                    Console.Out.Flush();
                    Environment.Exit(exitCode);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupAction] {action} failed: {ex}");
        }
    }

    private async Task OpenFirstSessionAsync()
    {
        if (Sessions.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(AppHost.StartupOpenIndex, 0, Sessions.Count - 1);
        await OpenSessionAsync(Sessions[index]);
    }

    public SettingsViewModel Settings { get; }

    /// <summary>Saved desktops / connection switching (desktop-management page).</summary>
    public ProfilesViewModel Profiles { get; }

    public ICommand OpenDesktopsCommand { get; }

    public ICommand CloseDesktopsCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand CloseSettingsCommand { get; }

    private AppPage _desktopsReturnPage = AppPage.Connect;

    private AppPage _settingsReturnPage = AppPage.Sessions;

    /// <summary>Sessions grouped by DSH workspace (the session list page renders these).</summary>
    public ObservableCollection<WorkspaceGroupViewModel> WorkspaceGroups { get; } = new();

    /// <summary>Workspaces offered by the session-list picker (same group instances as the list page).</summary>
    public ObservableCollection<WorkspaceGroupViewModel> WorkspaceChoices { get; } = new();

    /// <summary>Archived sessions of the selected workspace, shown in the collapsible archived section.</summary>
    public ObservableCollection<SessionListViewModel> SelectedWorkspaceArchivedSessions { get; } = new();

    public WorkspaceGroupViewModel? SelectedWorkspaceGroup
    {
        get => _selectedWorkspaceGroup;
        set
        {
            // A workspace is never deselected by the user, so a null write-back while the picker still
            // has entries is a transient effect of replacing its items. Ignoring it keeps the current
            // workspace from being forgotten (and persisted as null), which otherwise makes the next
            // rebuild fall back to a different workspace. LoadSessionsAsync also snapshots the
            // selection before clearing, because the Clear() write-back arrives when the collection is
            // already empty and therefore reaches the code below.
            if (value is null && WorkspaceChoices.Count > 0)
            {
                return;
            }

            if (!SetProperty(ref _selectedWorkspaceGroup, value))
            {
                return;
            }

            _selectedWorkspaceId = value?.WorkspaceId;
            // Only real selections are remembered; a placeholder null must not erase the preference.
            if (value is not null)
            {
                Settings.LastWorkspaceId = value.WorkspaceId;
            }

            RefreshSelectedWorkspace();
            WorkspaceSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SelectedWorkspaceName => SelectedWorkspaceGroup?.Title ?? "会话";

    public string SelectedWorkspacePath => SelectedWorkspaceGroup?.PathText ?? string.Empty;

    public bool HasSelectedWorkspacePath => !string.IsNullOrWhiteSpace(SelectedWorkspacePath);

    public bool HasSelectedWorkspaceArchived => SelectedWorkspaceArchivedSessions.Count > 0;

    public string ArchivedHeaderText => $"已归档（{SelectedWorkspaceArchivedSessions.Count}）";

    public bool IsArchivedExpanded
    {
        get => _isArchivedExpanded;
        set
        {
            if (SetProperty(ref _isArchivedExpanded, value))
            {
                OnPropertyChanged(nameof(ArchivedChevron));
            }
        }
    }

    public string ArchivedChevron => IsArchivedExpanded ? "▾" : "▸";

    public ICommand ToggleArchivedCommand { get; }

    public event EventHandler? WorkspaceSelectionChanged;

    public ICommand ArchiveSessionCommand { get; }

    public ICommand UnarchiveSessionCommand { get; }

    public ICommand CreateSessionCommand { get; }

    public ICommand ToastCommand { get; }

    /// <summary>Short transient message shown at the top of the session list ("已归档" etc.).</summary>
    public string ToastText
    {
        get => _toastText;
        private set
        {
            if (SetProperty(ref _toastText, value))
            {
                OnPropertyChanged(nameof(HasToast));
            }
        }
    }

    public bool HasToast => !string.IsNullOrWhiteSpace(ToastText);

    /// <summary>Approvals / questions live here; chat binds through <see cref="SessionViewModel.Hub"/>.</summary>
    public InteractionHub Hub => _hub;

    public bool HasPendingInteractions => _hub.HasPendingInteractions;

    public string PendingInteractionText => _hub.PendingBadgeText;

    /// <summary>False on narrow phones: the session rows drop the model column (set by SessionsView).</summary>
    public bool ShowSessionModel
    {
        get => _showSessionModel;
        set => SetProperty(ref _showSessionModel, value);
    }

    /// <summary>Bound to the session list's <c>ListBox.SelectedItem</c> (virtualized list).</summary>
    public SessionListViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value) || value is null)
            {
                return;
            }

            // Clear the highlight before navigating; the chat page owns the screen afterwards.
            _selectedSession = null;
            OnPropertyChanged();
            _ = OpenSessionAsync(value);
        }
    }

    // ---- Commands -----------------------------------------------------------------------------------

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand RefreshSessionsCommand { get; }

    public ICommand BackToListCommand { get; }

    public ICommand LoadPairingFromFileCommand { get; }

    public ICommand DemoPreviewCommand { get; }

    public ICommand OpenSessionCommand { get; }

    public ICommand ForgetPairingCommand { get; }

    // ---- Navigation ---------------------------------------------------------------------------------

    public AppPage Page
    {
        get => _page;
        private set
        {
            if (SetProperty(ref _page, value))
            {
                Raise(
                    nameof(IsConnectPage),
                    nameof(IsSessionsPage),
                    nameof(IsChatPage),
                    nameof(IsDesktopsPage),
                    nameof(IsSettingsPage),
                    nameof(ShowConnectionBar),
                    nameof(IsSessionsEmpty));
            }
        }
    }

    public bool IsConnectPage => Page == AppPage.Connect;

    public bool IsSessionsPage => Page == AppPage.Sessions;

    public bool IsChatPage => Page == AppPage.Chat;

    public bool IsDesktopsPage => Page == AppPage.Desktops;

    public bool IsSettingsPage => Page == AppPage.Settings;

    public SessionViewModel? CurrentSession
    {
        get => _currentSession;
        private set
        {
            if (SetProperty(ref _currentSession, value))
            {
                Raise(nameof(HasCurrentSession));
            }
        }
    }

    public bool HasCurrentSession => CurrentSession is not null;

    // ---- Connection state ---------------------------------------------------------------------------

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                Raise(nameof(ShowConnectionBar), nameof(CanDisconnect), nameof(ConnectionTone));
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                Raise(nameof(ShowConnectionBar), nameof(ConnectionTone), nameof(CanConnect), nameof(IsConnectingOrReconnecting));
            }
        }
    }

    public bool IsReconnecting
    {
        get => _isReconnecting;
        private set
        {
            if (SetProperty(ref _isReconnecting, value))
            {
                Raise(nameof(ShowConnectionBar), nameof(ConnectionTone), nameof(IsConnectingOrReconnecting));
            }
        }
    }

    public bool IsConnectingOrReconnecting => IsConnecting || IsReconnecting;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                Raise(nameof(CanConnect), nameof(ConnectButtonText));
            }
        }
    }

    public bool IsListingSessions
    {
        get => _isListingSessions;
        private set => SetProperty(ref _isListingSessions, value);
    }

    public bool CanDisconnect => IsConnected && !IsBusy;

    public string ConnectionTone => IsConnected
        ? "connected"
        : IsConnecting || IsReconnecting
            ? "connecting"
            : "disconnected";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set
        {
            if (SetProperty(ref _statusDetail, value))
            {
                Raise(nameof(HasStatusDetail));
            }
        }
    }

    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(StatusDetail);

    public bool ShowConnectionBar => Page is AppPage.Sessions or AppPage.Chat &&
                                     (IsConnected || IsConnecting || IsReconnecting);

    // ---- Errors -------------------------------------------------------------------------------------

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

    // ---- Pairing form -------------------------------------------------------------------------------

    public bool CanConnect => !IsBusy && !IsConnecting && SettingsHasPairingInput;

    public string ConnectButtonText => IsConnecting ? "连接中…" : "连接";

    public bool CanLoadPairingFile => AppHost.CanLoadPairingFromFile;

    public bool CanReadPairingFileHint => AppHost.CanLoadPairingFromFile;

    public string PairingPreviewText
    {
        get => _pairingPreview;
        private set
        {
            if (SetProperty(ref _pairingPreview, value))
            {
                Raise(nameof(HasPairingPreview));
            }
        }
    }

    public bool HasPairingPreview => !string.IsNullOrWhiteSpace(PairingPreviewText);

    /// <summary>True while the app is running on the built-in fake client (UI work / demo mode).</summary>
    public bool IsDemoClient => !AppHost.IsRealClientAvailable;

    // ---- Sessions -----------------------------------------------------------------------------------

    public ObservableCollection<SessionListViewModel> Sessions { get; } = new();

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>
    /// True while the visible list came from the on-disk cache and the network refresh is still pending.
    /// The UI uses it for a subtle "offline copy" hint; the list is usable either way.
    /// </summary>
    public bool IsShowingCachedSessions
    {
        get => _isShowingCachedSessions;
        private set
        {
            if (SetProperty(ref _isShowingCachedSessions, value))
            {
                Raise(nameof(HasCachedSessions));
            }
        }
    }

    public bool HasCachedSessions => IsShowingCachedSessions;

    public bool IsSessionsEmpty => Page == AppPage.Sessions && !IsListingSessions &&
                                   (SelectedWorkspaceGroup is null || SelectedWorkspaceGroup.Sessions.Count == 0);

    public string SessionsSummary
    {
        get
        {
            var sessions = SelectedWorkspaceGroup?.Sessions;
            if (sessions is null || sessions.Count == 0)
            {
                return "暂无会话";
            }

            var running = sessions.Count(session => session.Running);
            return running > 0
                ? $"{sessions.Count} 个会话 · {running} 个运行中"
                : $"{sessions.Count} 个会话";
        }
    }

    private bool SettingsHasPairingInput
        => !string.IsNullOrWhiteSpace(Settings.PairingUri) ||
           (!string.IsNullOrWhiteSpace(Settings.Host) && !string.IsNullOrWhiteSpace(Settings.Token));

    // ---- Connect ------------------------------------------------------------------------------------

    private async Task ConnectAsync(bool demo)
    {
        if (IsBusy || IsConnecting || _returningToConnect)
        {
            // A pending forced return-to-connect owns the client teardown; ignore a concurrent connect tap
            // instead of letting the two paths dispose each other's client.
            return;
        }

        DshPairingInfo? pairing;
        string? error;
        if (demo)
        {
            pairing = new DshPairingInfo("demo", 8443, "demo-token", Name: "演示数据");
            error = null;
        }
        else if (!Settings.TryBuildPairing(out pairing, out error))
        {
            ErrorMessage = error ?? "配对信息不完整。";
            return;
        }

        if (pairing is null)
        {
            ErrorMessage = error ?? "配对信息不完整。";
            return;
        }

        ErrorMessage = string.Empty;
        DiagnosticsLog.Trace($"connect start demo={demo} host={pairing.Host}:{pairing.Port}");
        IsBusy = true;
        IsConnecting = true;
        StatusText = "正在连接…";
        StatusDetail = $"{pairing.Host}:{pairing.Port}";

        // Paint the last known list from disk before the network round-trips; the refresh below replaces
        // it. This is what keeps a cold start from looking like "download everything again".
        _cacheStore = SessionCacheStore.ForPairing(pairing);
        _drafts.Attach(_cacheStore);
        HydrateFromCache();

        // 设置页 can reconnect while a conversation is still open behind it: release that session first so
        // the chat page cannot keep a view-model bound to the client that is about to be disposed.
        await CloseCurrentSessionAsync().ConfigureAwait(true);
        await DisposeClientAsync(keepStatus: true);

        try
        {
            var useDemo = demo || string.Equals(pairing.Host, "demo", StringComparison.OrdinalIgnoreCase);
            var client = AppHost.CreateClient(useDemo);
            client.StateChanged += OnClientStateChanged;
            _client = client;
            if (client is IAclGuardClient aclClient)
            {
                Settings.AttachAclClient(aclClient);
            }

            await client.ConnectAsync(pairing).ConfigureAwait(true);
            IsConnected = true;
            DiagnosticsLog.Trace($"connect done demo={demo}");
            StatusText = "已连接";
            StatusDetail = $"{pairing.Host}:{pairing.Port}" +
                           (string.IsNullOrWhiteSpace(pairing.SpkiSha256) ? string.Empty : " · TLS 指纹已固定");
            if (client is IInteractionClient interaction)
            {
                _hub.Attach(interaction);
            }

            if (!useDemo)
            {
                Settings.Save(pairing);
                Settings.Status = "已连接: " + pairing.Host + ":" + pairing.Port;
                _ = PlatformIntegration.PersistPairingAsync(Settings.ToSecretJson());
                await Profiles.RememberConnectedAsync(pairing).ConfigureAwait(true);
            }

            PlatformIntegration.SetConnectionActive(true);

            await LoadSessionsAsync().ConfigureAwait(true);
            if (client is IAutoApproveClient autoApproveClient)
            {
                Settings.AutoApprove.Attach(autoApproveClient, WorkspaceGroups);
            }

            Page = AppPage.Sessions;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = "连接失败: " + Describe(ex);
            StatusText = "连接失败";
            IsConnected = false;
            _ = Profiles.MarkConnectionFailedAsync(pairing);
        }
        finally
        {
            IsConnecting = false;
            IsBusy = false;
            Raise(nameof(IsDemoClient));
        }
    }

    private async Task LoadSessionsAsync()
    {
        if (_client is null || _disposed)
        {
            return;
        }

        IsListingSessions = true;
        try
        {
            // Compact list first: the relay trims the ~92% of `projections` the phone never uses and hides
            // subagent rows server-side. Core falls back to the official `session/list` when the installed
            // relay predates the endpoint, so older desktops keep working unchanged.
            IReadOnlyList<SessionSummary> sessions;
            string? cursor = null;
            var totalCount = 0;
            var hiddenSubagents = 0;
            var subagentsFiltered = false;
            if (_client is ISessionSummaryClient summaryClient)
            {
                var snapshot = await summaryClient.ListSessionSummariesAsync().ConfigureAwait(true);
                sessions = snapshot.Items;
                cursor = snapshot.Cursor;
                totalCount = snapshot.TotalCount;
                hiddenSubagents = snapshot.HiddenSubagentCount;
                subagentsFiltered = snapshot.UsedCompactEndpoint;
            }
            else
            {
                sessions = await _client.ListSessionsAsync().ConfigureAwait(true);
            }

            DshWorkspaceState? workspaceState = null;
            if (_client is IWorkspaceClient workspaceClient)
            {
                try
                {
                    workspaceState = await workspaceClient.GetWorkspaceStateAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Workspaces] state failed: {ex.Message}");
                }
            }

            ApplySessionData(sessions, workspaceState);

            // Refresh the offline cache: the next cold start paints this list before the desktop answers.
            _cacheStore ??= SessionCacheStore.ForPairing(_client.Pairing);
            _cacheStore?.SaveSessionList(
                sessions,
                cursor,
                totalCount,
                subagentsFiltered ? hiddenSubagents : _hiddenSubagentSessionIds.Count,
                subagentsFiltered);
            _cacheStore?.SaveWorkspace(workspaceState);
            IsShowingCachedSessions = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = "读取会话列表失败: " + Describe(ex);
        }
        finally
        {
            IsListingSessions = false;
            Raise(nameof(IsSessionsEmpty));
        }
    }

    /// <summary>Paints cached list/workspace immediately (cold start) before the network refresh lands.</summary>
    private void HydrateFromCache()
    {
        if (_cacheStore is null)
        {
            return;
        }

        try
        {
            _cacheStore.TryLoadSessionList(out var cachedList);
            _cacheStore.TryLoadWorkspace(out var cachedWorkspace);
            var items = cachedList?.Items.Select(item => item.ToSummary()).ToList() ?? new List<SessionSummary>();
            if (items.Count == 0 && cachedWorkspace is null)
            {
                return;
            }

            ApplySessionData(items, cachedWorkspace);
            IsShowingCachedSessions = items.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[cache] hydrate failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the grouped session list from a session list + workspace baseline. Shared by the network
    /// refresh and the cached cold start.
    /// </summary>
    private void ApplySessionData(
        IReadOnlyList<SessionSummary> sessions,
        DshWorkspaceState? workspaceState)
    {
        Sessions.Clear();
        _hiddenSubagentSessionIds.Clear();

        // Subagent (child) sessions appear in the raw list but are only openable from their parent, so
        // they are hidden from the phone list and excluded from the counts. The compact relay endpoint
        // already dropped them; this loop then only sees top-level rows.
        var visible = new List<SessionSummary>(sessions.Count);
        foreach (var summary in sessions)
        {
            if (SessionFilter.IsSubagent(summary))
            {
                _hiddenSubagentSessionIds.Add(summary.SessionId);
                continue;
            }

            visible.Add(summary);
        }

            _archivedSessionIds.Clear();
            if (workspaceState is not null)
            {
                foreach (var archivedId in workspaceState.ArchivedSessionIds)
                {
                    _archivedSessionIds.Add(archivedId);
                }
            }

            var visibleById = visible.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
            var groupedIds = new HashSet<string>(StringComparer.Ordinal);
            var groups = new List<WorkspaceGroupViewModel>();

            // Capture the workspace the user is looking at BEFORE the picker items are replaced.
            // Clearing the picker's ItemsSource makes Avalonia write null through the TwoWay selection
            // binding, so neither the current group nor Settings.LastWorkspaceId can be trusted after
            // that; this snapshot survives the rebuild and is what the selection is restored from.
            var previousSelection = _selectedWorkspaceGroup;
            var previousWorkspaceId = previousSelection is not null
                ? previousSelection.WorkspaceId
                : Settings.LastWorkspaceId;
            var previousWasUngrouped = previousSelection?.IsUngrouped ?? false;

            if (workspaceState is not null)
            {
                foreach (var workspace in workspaceState.Workspaces)
                {
                    var group = new WorkspaceGroupViewModel(
                        workspace.WorkspaceId,
                        string.IsNullOrWhiteSpace(workspace.Title) ? "工作区" : workspace.Title,
                        workspace.Path,
                        createSessionCommand: CreateSessionCommand);

                    foreach (var sessionId in workspace.SessionIds)
                    {
                        if (!groupedIds.Add(sessionId) ||
                            !visibleById.TryGetValue(sessionId, out var summary) ||
                            _archivedSessionIds.Contains(sessionId))
                        {
                            continue;
                        }

                        group.Sessions.Add(CreateSessionItem(summary, isArchived: false));
                    }

                    // Every workspace the host reports is offered, even when it has no visible session
                    // (its sessions may all be archived). Filtering empty ones made a workspace
                    // disappear from the picker entirely once its last session was archived, so it could
                    // no longer be selected to create a new session there.
                    group.RefreshCounters();
                    groups.Add(group);
                }
            }

            // Sessions that belong to no workspace (or when the host has no workspace support).
            var ungrouped = new WorkspaceGroupViewModel(
                null,
                workspaceState is null ? "全部会话" : "其他",
                null,
                isUngrouped: true,
                createSessionCommand: CreateSessionCommand);
            foreach (var summary in visible.Where(summary => !groupedIds.Contains(summary.SessionId)))
            {
                if (_archivedSessionIds.Contains(summary.SessionId))
                {
                    continue;
                }

                ungrouped.Sessions.Add(CreateSessionItem(summary, isArchived: false));
            }

            // Keep an empty "其他" bucket only while it is the current selection.
            if (ungrouped.HasSessions || previousWasUngrouped)
            {
                ungrouped.RefreshCounters();
                groups.Add(ungrouped);
            }

            // Archived sessions are intentionally not surfaced: the client hides them completely.
            // (Archiving remains available from the row swipe action; restoring happens on the desktop.)
            foreach (var group in groups)
            {
                foreach (var item in group.Sessions)
                {
                    Sessions.Add(item);
                }
            }

            // session → workspace map (archived sessions keep their workspace through state.SessionIds)
            _workspaceIdBySessionId.Clear();
            if (workspaceState is not null)
            {
                foreach (var workspace in workspaceState.Workspaces)
                {
                    foreach (var sessionId in workspace.SessionIds)
                    {
                        _workspaceIdBySessionId[sessionId] = workspace.WorkspaceId;
                    }
                }
            }

            foreach (var summary in visible)
            {
                if (!_workspaceIdBySessionId.ContainsKey(summary.SessionId))
                {
                    _workspaceIdBySessionId[summary.SessionId] = null;
                }
            }

            _archivedWorkspaceGroup = null;
            WorkspaceGroups.Clear();
            WorkspaceChoices.Clear();
            foreach (var group in groups)
            {
                WorkspaceGroups.Add(group);
                WorkspaceChoices.Add(group);
            }

            // Re-open the previously selected workspace (which may now be empty), else the ungrouped
            // bucket the user was on, else the one with activity, else the first.
            var selected = (!string.IsNullOrWhiteSpace(previousWorkspaceId)
                               ? groups.FirstOrDefault(group =>
                                   string.Equals(group.WorkspaceId, previousWorkspaceId, StringComparison.Ordinal))
                               : null)
                           ?? (previousWasUngrouped
                               ? groups.FirstOrDefault(group => group.IsUngrouped)
                               : null)
                           ?? groups
                               .OrderByDescending(group => group.Sessions.Any(session => session.Running))
                               .ThenByDescending(group => group.Sessions.Count > 0
                                   ? group.Sessions.Max(session => session.Summary.UpdatedAt)
                                   : 0)
                               .FirstOrDefault();
            SelectedWorkspaceGroup = selected;

            Raise(
                nameof(HasSessions),
                nameof(SessionsSummary),
                nameof(IsSessionsEmpty),
                nameof(HasWorkspaceGroups),
                nameof(ArchivedSessionCount),
                nameof(HasSelectedWorkspaceArchived),
                nameof(ArchivedHeaderText),
                nameof(SelectedWorkspaceName),
                nameof(SelectedWorkspacePath),
                nameof(HasSelectedWorkspacePath));
    }

    private SessionListViewModel CreateSessionItem(SessionSummary summary, bool isArchived)
        => new(
            summary,
            OpenSessionCommand,
            archiveCommand: ArchiveSessionCommand,
            unarchiveCommand: UnarchiveSessionCommand,
            isArchived: isArchived);

    /// <summary>Rebuilds the archived bucket for the selected workspace after a picker change / refresh.</summary>
    private void RefreshSelectedWorkspace()
    {
        SelectedWorkspaceArchivedSessions.Clear();
        if (_archivedWorkspaceGroup is not null)
        {
            foreach (var session in _archivedWorkspaceGroup.Sessions)
            {
                if (WorkspaceMatchesSelection(session.SessionId))
                {
                    SelectedWorkspaceArchivedSessions.Add(session);
                }
            }
        }

        if (SelectedWorkspaceArchivedSessions.Count == 0)
        {
            IsArchivedExpanded = false;
        }

        Raise(
            nameof(SelectedWorkspaceName),
            nameof(SelectedWorkspacePath),
            nameof(HasSelectedWorkspacePath),
            nameof(HasSelectedWorkspaceArchived),
            nameof(ArchivedHeaderText),
            nameof(SessionsSummary),
            nameof(IsSessionsEmpty),
            nameof(HasSessions));
    }

    private bool WorkspaceMatchesSelection(string sessionId)
        => string.Equals(
            _workspaceIdBySessionId.GetValueOrDefault(sessionId),
            _selectedWorkspaceId,
            StringComparison.Ordinal);

    public bool HasWorkspaceGroups => WorkspaceGroups.Count > 0;

    public int ArchivedSessionCount => _archivedSessionIds.Count;

    // ---- Workspace group actions ---------------------------------------------------------------

    private async Task CreateSessionInGroupAsync(WorkspaceGroupViewModel group)
    {
        if (_client is not IWorkspaceClient workspaceClient)
        {
            ErrorMessage = "当前客户端不支持新建会话。";
            return;
        }

        ErrorMessage = string.Empty;
        try
        {
            // A workspace's "new session" is a single slot in the user's mental model: tapping
            // 「＋ 新建会话」 again must return to the still-unused blank session (and its unsent draft)
            // instead of creating another "(空白会话)" row. Once a prompt is sent the session stops
            // being blank and the next tap creates a genuinely new one.
            var existingBlank = group.Sessions
                .Where(session => session.IsBlank && !_sessionsWithSentPrompt.Contains(session.SessionId))
                .OrderByDescending(session => session.Summary.UpdatedAt)
                .FirstOrDefault();
            if (existingBlank is not null)
            {
                await OpenSessionAsync(existingBlank).ConfigureAwait(true);
                return;
            }

            var sessionId = await workspaceClient.CreateSessionAsync(group.WorkspaceId).ConfigureAwait(true);
            ShowToast("已创建新会话");
            await LoadSessionsAsync().ConfigureAwait(true);
            var item = Sessions.FirstOrDefault(session =>
                string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
            if (item is not null)
            {
                await OpenSessionAsync(item).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "新建会话失败: " + Describe(ex);
        }
    }

    private async Task ArchiveSessionAsync(SessionListViewModel item)
    {
        if (_client is not IWorkspaceClient workspaceClient)
        {
            ErrorMessage = "当前客户端不支持归档。";
            return;
        }

        ErrorMessage = string.Empty;
        try
        {
            try
            {
                await workspaceClient.ArchiveSessionAsync(item.SessionId, stopActivity: item.Running).ConfigureAwait(true);
            }
            catch (DshRemoteException ex) when (IsSessionActiveFailure(ex) && !item.Running)
            {
                // Host asks to stop the run first: retry once with stopActivity.
                await workspaceClient.ArchiveSessionAsync(item.SessionId, stopActivity: true).ConfigureAwait(true);
            }

            if (string.Equals(CurrentSession?.SessionId, item.SessionId, StringComparison.Ordinal))
            {
                await CloseCurrentSessionAsync().ConfigureAwait(true);
                Page = AppPage.Sessions;
            }

            ShowToast("已归档");
            await LoadSessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = "归档失败: " + Describe(ex) +
                           (IsSessionActiveFailure(ex) ? "（可先停止会话再归档）" : string.Empty);
        }
    }

    private async Task UnarchiveSessionAsync(SessionListViewModel item)
    {
        if (_client is not IWorkspaceClient workspaceClient)
        {
            ErrorMessage = "当前客户端不支持归档。";
            return;
        }

        ErrorMessage = string.Empty;
        try
        {
            await workspaceClient.UnarchiveSessionAsync(item.SessionId).ConfigureAwait(true);
            ShowToast("已恢复");
            await LoadSessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = "恢复失败: " + Describe(ex);
        }
    }

    private static bool IsSessionActiveFailure(Exception ex)
        => ex is DshRemoteException remote &&
           (remote.Code.Contains("session-active", StringComparison.OrdinalIgnoreCase) ||
            remote.Message.Contains("session-active", StringComparison.OrdinalIgnoreCase) ||
            remote.Message.Contains("active", StringComparison.OrdinalIgnoreCase));

    private void ShowToast(string message)
    {
        ToastText = message;
        var version = ++_toastVersion;
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (version == _toastVersion)
            {
                ToastText = string.Empty;
            }
        }), TaskScheduler.Default);
    }

    private async Task RefreshSessionsAsync()
    {
        if (_client is null)
        {
            return;
        }

        ErrorMessage = string.Empty;
        await LoadSessionsAsync().ConfigureAwait(true);
    }

    private async Task OpenSessionAsync(SessionListViewModel item)
    {
        if (_client is null || _disposed || _openingSession || _returningToConnect)
        {
            return;
        }

        // Opening a session used to be reachable from two paths at once (list selection + row command).
        // Two SessionViewModels for one session meant two follow consumers, and only one of them could own
        // the subscription — the other retried forever. Serialize the setup; the actual load deliberately
        // runs outside the gate so a slow load cannot swallow the next user tap.
        _openingSession = true;
        SessionViewModel? session = null;
        try
        {
            ErrorMessage = string.Empty;
            DiagnosticsLog.Trace($"open session id={item.Summary.SessionId} running={item.Summary.Running}");

            // Switching directly from one chat to another must release the previous follow subscription.
            if (CurrentSession is not null)
            {
                await CloseCurrentSessionAsync().ConfigureAwait(true);
            }

            // A forced return-to-connect may have won the race while the previous session was closing.
            if (_client is null || _disposed || _returningToConnect)
            {
                return;
            }

            session = new SessionViewModel(_client, item.Summary, BackToListCommand, _hub, Settings, _cacheStore, _drafts);
            session.FollowAbandoned += OnSessionFollowAbandoned;
            session.PromptSent += OnSessionPromptSent;
            CurrentSession = session;
            _hub.ActiveSessionId = item.SessionId;

            Page = AppPage.Chat;
        }
        finally
        {
            _openingSession = false;
        }

        if (session is null)
        {
            return;
        }

        await session.LoadAsync().ConfigureAwait(true);
    }

    private Task BackToListAsync()
    {
        var session = CurrentSession;
        CurrentSession = null;
        _hub.ActiveSessionId = null;
        Page = AppPage.Sessions;

        // The page switch above is the user-visible part and must never wait on network I/O. Tear the old
        // chat down and refresh the list in the background: awaiting either one here kept the shared
        // AsyncRelayCommand "running", which left the ‹ back button disabled (looks like a dead button) for
        // as long as a degraded transport kept ListSessionsAsync pending.
        if (session is not null)
        {
            _ = CloseSessionInBackgroundAsync(session);
        }

        _ = RefreshSessionsInBackgroundAsync();
        return Task.CompletedTask;
    }

    private async Task CloseSessionInBackgroundAsync(SessionViewModel session)
    {
        session.FollowAbandoned -= OnSessionFollowAbandoned;
        session.PromptSent -= OnSessionPromptSent;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Main] session dispose failed: {ex.Message}");
        }
    }

    private async Task RefreshSessionsInBackgroundAsync()
    {
        try
        {
            await LoadSessionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Main] session refresh failed: {ex.Message}");
        }
    }

    private void OnSessionPromptSent(object? sender, EventArgs e)
    {
        if (sender is SessionViewModel session)
        {
            _sessionsWithSentPrompt.Add(session.SessionId);
        }
    }

    /// <summary>
    /// A chat gave up on its live subscription (retry budget exhausted) or the user tapped 重新连接.
    /// Drop the dead client and show the connection page so the user has a clear way back in.
    /// </summary>
    private void OnSessionFollowAbandoned(object? sender, EventArgs e)
    {
        if (sender is not SessionViewModel session)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // The user may have switched chats (or left the chat) before this posted callback ran; a
            // stale notification must never tear down a healthy session.
            if (_disposed || !ReferenceEquals(CurrentSession, session))
            {
                return;
            }

            _ = ReturnToConnectAsync(session);
        });
    }

    private async Task ReturnToConnectAsync(SessionViewModel? session)
    {
        if (_disposed || _returningToConnect)
        {
            return;
        }

        if (session is not null && !ReferenceEquals(CurrentSession, session))
        {
            return;
        }

        _returningToConnect = true;
        try
        {
            // Leave the dead chat immediately: teardown/dispose can take a bounded moment and must never
            // look like an unresponsive app.
            Page = AppPage.Connect;
            StatusText = "未连接";
            StatusDetail = string.Empty;
            ErrorMessage = "实时连接已断开，已返回连接页面。请确认桌面 relay 在线后点「连接」，或重新扫码配对。";

            if (session is not null)
            {
                session.FollowAbandoned -= OnSessionFollowAbandoned;
            }

            if (session is not null && ReferenceEquals(CurrentSession, session))
            {
                await CloseCurrentSessionAsync().ConfigureAwait(true);
            }
            else if (session is not null)
            {
                await CloseSessionInBackgroundAsync(session).ConfigureAwait(true);
            }

            if (CurrentSession is not null)
            {
                await CloseCurrentSessionAsync().ConfigureAwait(true);
            }

            await DisposeClientAsync(keepStatus: false).ConfigureAwait(true);
            ResetToConnectPage();
            ErrorMessage = "实时连接已断开，已返回连接页面。请确认桌面 relay 在线后点「连接」，或重新扫码配对。";
        }
        finally
        {
            _returningToConnect = false;
        }
    }

    private void ResetToConnectPage()
    {
        Sessions.Clear();
        WorkspaceGroups.Clear();
        WorkspaceChoices.Clear();
        SelectedWorkspaceGroup = null;
        _hiddenSubagentSessionIds.Clear();
        _archivedSessionIds.Clear();
        _sessionsWithSentPrompt.Clear();
        _ = Profiles.MarkDisconnectedAsync();
        Raise(
            nameof(HasSessions),
            nameof(SessionsSummary),
            nameof(IsSessionsEmpty));
        IsConnected = false;
        IsConnecting = false;
        IsReconnecting = false;
        Page = AppPage.Connect;
        StatusText = "未连接";
        StatusDetail = string.Empty;
        PlatformIntegration.SetConnectionActive(false);
    }

    private async Task DisconnectAsync()
    {
        ErrorMessage = string.Empty;
        await CloseCurrentSessionAsync().ConfigureAwait(true);
        await DisposeClientAsync(keepStatus: false).ConfigureAwait(true);
        ResetToConnectPage();
    }

    private async Task LoadPairingFromFileAsync()
    {
        await Task.Yield();
        if (AppHost.TryLoadPairingFromFile(out var pairing, out var sourcePath, out var error) && pairing is not null)
        {
            Settings.ApplyPairing(pairing);
            ErrorMessage = string.Empty;
            Settings.Status = $"已从 {Path.GetFileName(sourcePath ?? "pairing.json")} 载入: {pairing.Host}:{pairing.Port}" +
                              (string.IsNullOrWhiteSpace(pairing.SpkiSha256) ? " (无指纹)" : " (含 TLS 指纹)");
            RefreshPairingPreview();
        }
        else
        {
            ErrorMessage = error ?? "读取配对文件失败。";
        }
    }

    private void ForgetPairing()
    {
        Settings.Clear();
        ErrorMessage = string.Empty;
        RefreshPairingPreview();
        _ = PlatformIntegration.ClearPairingAsync();
    }

    // ---- Client lifecycle ---------------------------------------------------------------------------

    private void OnClientStateChanged(object? sender, DshConnectionState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || sender != _client)
            {
                return;
            }

            switch (state)
            {
                case DshConnectionState.Connected:
                    IsConnected = true;
                    IsConnecting = false;
                    IsReconnecting = false;
                    StatusText = "已连接";
                    break;
                case DshConnectionState.Connecting:
                    IsConnecting = true;
                    StatusText = "正在连接…";
                    break;
                case DshConnectionState.Reconnecting:
                    IsConnecting = false;
                    IsReconnecting = true;
                    StatusText = "连接断开，正在重连…";
                    StatusDetail = string.Empty;
                    break;
                case DshConnectionState.Failed:
                    IsConnected = false;
                    IsConnecting = false;
                    IsReconnecting = false;
                    StatusText = "连接失败";
                    ErrorMessage = "连接已失败: 请确认桌面 relay 仍在运行、手机与桌面在同一网络，或重新配对。";
                    PlatformIntegration.SetConnectionActive(false);
                    break;
                case DshConnectionState.Disconnected:
                    IsConnected = false;
                    IsConnecting = false;
                    IsReconnecting = false;
                    StatusText = "连接已断开";
                    PlatformIntegration.SetConnectionActive(false);
                    break;
            }
        });
    }

    private async Task DisposeClientAsync(bool keepStatus)
    {
        var client = _client;
        _client = null;
        Settings.DetachAclClient();
        Settings.AutoApprove.Detach();
        _hub.ActiveSessionId = null;
        _hub.Detach();
        if (client is null)
        {
            return;
        }

        client.StateChanged -= OnClientStateChanged;
        try
        {
            await client.DisconnectAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignored
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignored
        }

        if (!keepStatus)
        {
            IsConnected = false;
        }
    }

    private async Task CloseCurrentSessionAsync()
    {
        var session = CurrentSession;
        CurrentSession = null;
        if (session is not null)
        {
            session.FollowAbandoned -= OnSessionFollowAbandoned;
            session.PromptSent -= OnSessionPromptSent;
            await session.DisposeAsync().ConfigureAwait(true);
        }

        // The view-model flushed its own draft on dispose; this covers the shell-owned store too so a
        // process death right after leaving a chat still keeps the text.
        _drafts.Flush();
    }

    private void OnSettingsChanged()
    {
        Raise(nameof(CanConnect), nameof(IsSessionsEmpty));
        RefreshPairingPreview();
    }

    private void RefreshPairingPreview()
    {
        if (Settings.TryBuildPairing(out var pairing, out _) && pairing is not null)
        {
            PairingPreviewText = $"将连接 {pairing.Host}:{pairing.Port}" +
                                 (string.IsNullOrWhiteSpace(pairing.SpkiSha256) ? " (未固定 TLS 指纹)" : " (TLS 指纹已固定)");
        }
        else
        {
            PairingPreviewText = string.Empty;
        }
    }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseCurrentSessionAsync().ConfigureAwait(false);
        _drafts.Flush();
        await DisposeClientAsync(keepStatus: true).ConfigureAwait(false);
        await _hub.DisposeAsync().ConfigureAwait(false);
    }
}
