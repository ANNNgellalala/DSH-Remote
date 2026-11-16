using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>
/// Phone editor for the desktop relay's auto-approval rules (relay 0.7.0+), shaped as a
/// three-level checkbox tree:
///
///   全部工作区
///     └─ 工作区 A
///          ├─ 会话 1
///          └─ 会话 2
///
/// Checking a parent selects every child; clearing a parent clears every child. A partially
/// selected parent is shown as indeterminate (and clears its children when tapped). Fully selected
/// workspaces are saved as workspace rules (future sessions included); partially selected workspaces
/// save only their checked session ids. The rules live on the desktop, so they keep applying while the
/// phone app is closed.
/// </summary>
public sealed class AutoApproveViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(350);

    private IAutoApproveClient? _client;
    private bool _suppress;
    private bool _enabled;
    private bool? _allSelected;
    private bool _allExplicit;
    private bool _isAvailable;
    private bool _isBusy;
    private string _summaryText = "连接桌面后可设置桌面端自动审批。";
    private string _statsText = string.Empty;
    private string _auditText = string.Empty;
    private string _errorText = string.Empty;
    private string _relayVersionText = string.Empty;
    private long _revision;
    private CancellationTokenSource? _saveCts;
    private int _saveGeneration;

    public AutoApproveViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsAvailable && !IsBusy);
    }

    public ICommand RefreshCommand { get; }

    /// <summary>Workspace tree; each workspace owns its session rows.</summary>
    public ObservableCollection<AutoApproveWorkspaceOption> Workspaces { get; } = new();

    /// <summary>
    /// Root checkbox. True = every current workspace/session selected; null = partial; false = none.
    /// Clicking it true selects all children, any other click clears all children.
    /// </summary>
    public bool? AllSelected
    {
        get => _allSelected;
        set => ApplyAllFromUser(value);
    }

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool IsEmpty => Workspaces.Count == 0;

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                NotifyCommands();
                Raise(nameof(CanEdit));
            }
        }
    }

    public bool CanEdit => IsAvailable && !IsBusy;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                RefreshSummaryText();
                QueueSave();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
                Raise(nameof(CanEdit));
            }
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatsText
    {
        get => _statsText;
        private set => SetProperty(ref _statsText, value);
    }

    public string AuditText
    {
        get => _auditText;
        private set => SetProperty(ref _auditText, value);
    }

    public bool HasAudit => !string.IsNullOrWhiteSpace(AuditText);

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string RelayVersionText
    {
        get => _relayVersionText;
        private set => SetProperty(ref _relayVersionText, value);
    }

    public long Revision => _revision;

    /// <summary>Bind to the relay client (called when a desktop connection opens).</summary>
    public void Attach(IAutoApproveClient client, IReadOnlyList<WorkspaceGroupViewModel> catalog)
    {
        _client = client;
        IsAvailable = true;
        SyncCatalog(catalog);
        PendingRefresh = RefreshAsync();
    }

    /// <summary>The refresh started by <see cref="Attach"/> / <see cref="Refresh"/> (tests await it).</summary>
    internal Task? PendingRefresh { get; private set; }

    /// <summary>Drop the relay client (disconnect/replace).</summary>
    public void Detach()
    {
        _client = null;
        _saveCts?.Cancel();
        _saveCts = null;
        IsAvailable = false;
        IsBusy = false;
        _suppress = true;
        try
        {
            _enabled = false;
            Raise(nameof(Enabled));
            _allExplicit = false;
            foreach (var workspace in Workspaces)
            {
                workspace.SetSelected(false);
            }

            Workspaces.Clear();
            _allSelected = false;
            Raise(nameof(AllSelected), nameof(HasWorkspaces), nameof(IsEmpty));
        }
        finally
        {
            _suppress = false;
        }

        SummaryText = "未连接（连接桌面后可设置桌面端自动审批）。";
        StatsText = string.Empty;
        AuditText = string.Empty;
        ErrorText = string.Empty;
        RelayVersionText = string.Empty;
        _revision = 0;
    }

    /// <summary>Refresh the workspace/session catalogue and reload the desktop rules (settings page open).</summary>
    public void Refresh(IReadOnlyList<WorkspaceGroupViewModel> catalog)
    {
        SyncCatalog(catalog);
        if (_client is not null)
        {
            PendingRefresh = RefreshAsync();
        }
    }

    /// <summary>Reload the rule document from the desktop.</summary>
    public async Task RefreshAsync()
    {
        var client = _client;
        if (client is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var config = await client.GetAutoApproveAsync().ConfigureAwait(true);
            ApplyConfig(config);
        }
        catch (Exception ex)
        {
            ErrorText = Describe(ex, "读取桌面自动审批规则失败");
            SummaryText = "无法读取桌面端规则。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Immediate flush of the pending debounced save (tests / page close).</summary>
    internal async Task SaveNowAsync()
    {
        _saveCts?.Cancel();
        _saveCts = null;
        await SaveAsync().ConfigureAwait(true);
    }

    internal bool IsSuppressed => _suppress;

    // ---- user actions -------------------------------------------------------

    private void ApplyAllFromUser(bool? value)
    {
        if (_suppress)
        {
            SetAllState(value);
            return;
        }

        _allExplicit = value == true;
        _suppress = true;
        try
        {
            var selected = value == true;
            foreach (var workspace in Workspaces)
            {
                workspace.SetSelected(selected);
                foreach (var session in workspace.Sessions)
                {
                    session.SetSelected(selected);
                }
            }

            SetAllState(selected);
        }
        finally
        {
            _suppress = false;
        }

        RefreshSummaryText();
        QueueSave();
    }

    internal void OnWorkspaceToggled(AutoApproveWorkspaceOption workspace, bool? value)
    {
        if (_suppress)
        {
            return;
        }

        _allExplicit = false;
        _suppress = true;
        try
        {
            var selected = value == true;
            workspace.SetSelected(selected);
            foreach (var session in workspace.Sessions)
            {
                session.SetSelected(selected);
            }

            RecomputeAllState();
        }
        finally
        {
            _suppress = false;
        }

        RefreshSummaryText();
        QueueSave();
    }

    internal void OnSessionToggled(AutoApproveSessionOption session)
    {
        if (_suppress)
        {
            return;
        }

        _allExplicit = false;
        _suppress = true;
        try
        {
            var workspace = Workspaces.FirstOrDefault(item => item.Sessions.Contains(session));
            if (workspace is not null)
            {
                var selectedCount = workspace.Sessions.Count(item => item.IsSelected);
                workspace.SetSelected(selectedCount == 0 ? false : selectedCount == workspace.Sessions.Count ? true : null);
            }

            RecomputeAllState();
        }
        finally
        {
            _suppress = false;
        }

        RefreshSummaryText();
        QueueSave();
    }

    /// <summary>Derive the root checkbox from the workspace states (never changes <see cref="_allExplicit"/>).</summary>
    private void RecomputeAllState()
    {
        if (_allExplicit)
        {
            SetAllState(true);
            return;
        }

        if (Workspaces.Count == 0)
        {
            SetAllState(false);
            return;
        }

        var allChecked = Workspaces.All(item => item.IsSelected == true);
        if (allChecked)
        {
            SetAllState(true);
            return;
        }

        var anyChecked = Workspaces.Any(item => item.IsSelected != false) ||
                         Workspaces.Any(item => item.Sessions.Any(session => session.IsSelected));
        SetAllState(anyChecked ? null : false);
    }

    private void SetAllState(bool? value)
    {
        if (_allSelected == value)
        {
            return;
        }

        _allSelected = value;
        Raise(nameof(AllSelected));
    }

    // ---- catalogue / config -------------------------------------------------

    private void SyncCatalog(IReadOnlyList<WorkspaceGroupViewModel> catalog)
    {
        var selectedWorkspaces = Workspaces
            .Where(workspace => workspace.IsSelected == true)
            .Select(workspace => workspace.Key)
            .ToHashSet(StringComparer.Ordinal);
        var partialSessions = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => session.IsSelected)
            .Select(session => session.SessionId)
            .ToHashSet(StringComparer.Ordinal);

        _suppress = true;
        try
        {
            var previous = Workspaces.ToDictionary(workspace => workspace.Key, StringComparer.Ordinal);
            Workspaces.Clear();
            foreach (var group in catalog)
            {
                if (string.IsNullOrWhiteSpace(group.WorkspaceId) && string.IsNullOrWhiteSpace(group.Path))
                {
                    continue;
                }

                var workspace = new AutoApproveWorkspaceOption(this, group.WorkspaceId, group.Path, group.Title);
                foreach (var session in group.Sessions)
                {
                    workspace.Sessions.Add(new AutoApproveSessionOption(
                        this,
                        session.SessionId,
                        session.Title,
                        session.CwdFull,
                        session.Summary.ParentSessionId));
                }

                if (previous.TryGetValue(workspace.Key, out var old))
                {
                    workspace.SetSelected(old.IsSelected);
                    foreach (var session in workspace.Sessions)
                    {
                        var oldSession = old.Sessions.FirstOrDefault(item => item.SessionId == session.SessionId);
                        session.SetSelected(oldSession?.IsSelected ?? false);
                    }
                }
                else if (_allExplicit || selectedWorkspaces.Contains(workspace.Key))
                {
                    workspace.SetSelected(true);
                    foreach (var session in workspace.Sessions)
                    {
                        session.SetSelected(true);
                    }
                }
                else if (partialSessions.Count > 0)
                {
                    foreach (var session in workspace.Sessions)
                    {
                        session.SetSelected(partialSessions.Contains(session.SessionId));
                    }

                    var selected = workspace.Sessions.Where(item => item.IsSelected).ToList();
                    workspace.SetSelected(selected.Count == 0
                        ? false
                        : selected.Count == workspace.Sessions.Count ? true : null);
                }

                Workspaces.Add(workspace);
            }

            RecomputeAllState();
        }
        finally
        {
            _suppress = false;
        }

        Raise(nameof(HasWorkspaces), nameof(IsEmpty));
        RefreshSummaryText();
    }

    private void ApplyConfig(DshAutoApproveConfig config)
    {
        _suppress = true;
        try
        {
            _revision = config.Revision;
            _allExplicit = config.All;
            _enabled = config.Enabled;
            Raise(nameof(Enabled));

            var selectedWorkspaceIds = config.Workspaces
                .Select(row => row.WorkspaceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var selectedWorkspacePaths = config.Workspaces
                .Select(row => row.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedSessionIds = config.Sessions.Select(row => row.SessionId).ToHashSet(StringComparer.Ordinal);

            foreach (var workspace in Workspaces)
            {
                var full = config.All ||
                           (workspace.WorkspaceId is not null && selectedWorkspaceIds.Contains(workspace.WorkspaceId)) ||
                           (workspace.Path is not null && selectedWorkspacePaths.Contains(workspace.Path));
                var anySession = false;
                foreach (var session in workspace.Sessions)
                {
                    var selected = config.All || full || selectedSessionIds.Contains(session.SessionId);
                    session.SetSelected(selected);
                    anySession |= selected;
                }

                workspace.SetSelected(config.All || full
                    ? true
                    : anySession ? (workspace.Sessions.Count > 0 && workspace.Sessions.All(item => item.IsSelected) ? true : null) : false);
            }

            RecomputeAllState();
        }
        finally
        {
            _suppress = false;
        }

        RelayVersionText = string.IsNullOrWhiteSpace(config.RelayVersion)
            ? string.Empty
            : "桌面 relay " + config.RelayVersion;
        StatsText = FormatStats(config);
        AuditText = FormatRecent(config.Recent);
        Raise(nameof(HasAudit));
        RefreshSummaryText();
        ErrorText = config.Error ?? string.Empty;
    }

    // ---- save ---------------------------------------------------------------

    private void QueueSave()
    {
        if (_suppress || _client is null)
        {
            return;
        }

        _saveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveCts = cts;
        var generation = ++_saveGeneration;
        _ = SaveAfterDelayAsync(generation, cts.Token);
    }

    private async Task SaveAfterDelayAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDelay, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || generation != _saveGeneration)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        var config = new DshAutoApproveConfig(
            Enabled,
            _allExplicit,
            _allExplicit
                ? Array.Empty<DshAutoApproveWorkspace>()
                : Workspaces
                    .Where(workspace => workspace.IsSelected == true)
                    .Select(workspace => new DshAutoApproveWorkspace(workspace.WorkspaceId, workspace.Path, workspace.Title))
                    .ToList(),
            _allExplicit
                ? Array.Empty<DshAutoApproveSession>()
                : Workspaces
                    .Where(workspace => workspace.IsSelected != true)
                    .SelectMany(workspace => workspace.Sessions)
                    .Where(session => session.IsSelected)
                    .Select(session => new DshAutoApproveSession(
                        session.SessionId,
                        session.Title,
                        session.Cwd,
                        session.ParentSessionId))
                    .ToList(),
            _revision);

        try
        {
            ErrorText = string.Empty;
            var saved = await client.SaveAutoApproveAsync(config).ConfigureAwait(true);
            _revision = saved.Revision;
            StatsText = FormatStats(saved);
            RelayVersionText = string.IsNullOrWhiteSpace(saved.RelayVersion) ? RelayVersionText : "桌面 relay " + saved.RelayVersion;
            RefreshSummaryText();
            Raise(nameof(Revision));
        }
        catch (Exception ex)
        {
            ErrorText = Describe(ex, "保存桌面自动审批规则失败");
        }
    }

    // ---- presentation -------------------------------------------------------

    private void RefreshSummaryText()
    {
        if (!IsAvailable)
        {
            SummaryText = "未连接（连接桌面后可设置桌面端自动审批）。";
            return;
        }

        if (!Enabled)
        {
            SummaryText = "桌面端自动审批已关闭：审批请求仍会正常推送。";
            return;
        }

        if (_allExplicit)
        {
            SummaryText = "桌面端自动审批：全部工作区（含以后新增的会话）。手机 App 关闭后仍然生效。";
            return;
        }

        var fullWorkspaces = Workspaces.Count(workspace => workspace.IsSelected == true);
        var partialSessions = Workspaces
            .Where(workspace => workspace.IsSelected != true)
            .SelectMany(workspace => workspace.Sessions)
            .Count(session => session.IsSelected);
        if (fullWorkspaces == 0 && partialSessions == 0)
        {
            SummaryText = "尚未选择范围：当前不会自动放行任何审批。";
            return;
        }

        var parts = new List<string>();
        if (fullWorkspaces > 0)
        {
            parts.Add($"{fullWorkspaces} 个工作区");
        }

        if (partialSessions > 0)
        {
            parts.Add($"{partialSessions} 个会话");
        }

        SummaryText = $"桌面端自动审批：已选 {string.Join("、", parts)}。手机 App 关闭后仍然生效。";
    }

    private static string FormatStats(DshAutoApproveConfig config)
    {
        var stats = config.Stats;
        if (stats is null || stats.Allowed == 0)
        {
            return "本次桌面运行尚未自动放行审批。";
        }

        var last = stats.LastAllowedAt is { } at
            ? " · 最近 " + at.LocalDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : string.Empty;
        return $"本次桌面运行已自动放行 {stats.Allowed} 次{last}";
    }

    private static string FormatRecent(IReadOnlyList<DshAutoApproveAudit>? recent)
    {
        if (recent is null || recent.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            recent.Take(3).Select(row =>
                $"{row.At.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}  {row.ToolName ?? "?"}  {row.SessionId ?? "?"}"));
    }

    private static string Describe(Exception ex, string prefix) => ex switch
    {
        DshRemoteException { Code: DshRemoteException.Codes.Transport } => prefix + "：桌面未连接。",
        DshRemoteException { Code: DshRemoteException.Codes.Unauthorized } => prefix + "：配对口令已失效，请重新配对。",
        DshRemoteException { Code: DshRemoteException.Codes.Forbidden } => prefix + "：桌面拒绝了该请求。",
        DshRemoteException { Code: DshRemoteException.Codes.ConnectFailed } => prefix + "：" + ex.Message,
        _ => prefix + "：" + ex.Message
    };

    private void NotifyCommands()
    {
        if (RefreshCommand is AsyncRelayCommand refresh)
        {
            refresh.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>One workspace node in the auto-approval tree (checking it checks every session under it).</summary>
public sealed class AutoApproveWorkspaceOption : ObservableObject
{
    private readonly AutoApproveViewModel _owner;
    private bool? _isSelected;

    public AutoApproveWorkspaceOption(AutoApproveViewModel owner, string? workspaceId, string? path, string title)
    {
        _owner = owner;
        WorkspaceId = workspaceId;
        Path = path;
        Title = string.IsNullOrWhiteSpace(title) ? (path ?? "未命名工作区") : title;
    }

    public string? WorkspaceId { get; }
    public string? Path { get; }
    public string Title { get; }
    public string PathText => Path ?? string.Empty;
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);
    public string Key => WorkspaceId ?? Path ?? Title;

    public ObservableCollection<AutoApproveSessionOption> Sessions { get; } = new();

    /// <summary>True (all sessions), null (some sessions) or false (none).</summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (_owner.IsSuppressed)
            {
                SetSelected(value);
                return;
            }

            _owner.OnWorkspaceToggled(this, value);
        }
    }

    internal void SetSelected(bool? value)
    {
        if (SetProperty(ref _isSelected, value, nameof(IsSelected)))
        {
            Raise(nameof(StatusText));
        }
    }

    public string StatusText => _isSelected switch
    {
        true => "全部会话",
        null => "部分会话",
        _ => "未选"
    };
}

/// <summary>One session leaf in the auto-approval tree.</summary>
public sealed class AutoApproveSessionOption : ObservableObject
{
    private readonly AutoApproveViewModel _owner;
    private bool _isSelected;

    public AutoApproveSessionOption(
        AutoApproveViewModel owner,
        string sessionId,
        string title,
        string cwd,
        string? parentSessionId)
    {
        _owner = owner;
        SessionId = sessionId;
        Title = title;
        Cwd = cwd;
        ParentSessionId = parentSessionId;
    }

    public string SessionId { get; }
    public string Title { get; }
    public string Cwd { get; }
    public string? ParentSessionId { get; }
    public string Meta => string.IsNullOrWhiteSpace(Cwd) ? SessionId : Cwd;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_owner.IsSuppressed)
            {
                SetSelected(value);
                return;
            }

            if (SetProperty(ref _isSelected, value))
            {
                _owner.OnSessionToggled(this);
            }
        }
    }

    internal void SetSelected(bool value)
    {
        SetProperty(ref _isSelected, value, nameof(IsSelected));
    }
}
