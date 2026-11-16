using System.Collections.ObjectModel;
using System.Windows.Input;
using DshRemote.App.Platform;
using DshRemote.App.Services;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>One saved desktop in the management page.</summary>
public sealed class DesktopProfileViewModel : ObservableObject
{
    private readonly Action<DesktopProfileViewModel> _connect;
    private readonly Action<DesktopProfileViewModel, string> _rename;
    private readonly Action<DesktopProfileViewModel> _delete;
    private DesktopProfileRecord _record;
    private bool _isEditing;
    private string _editingName = string.Empty;
    private bool _isActive;

    public DesktopProfileViewModel(
        DesktopProfileRecord record,
        Action<DesktopProfileViewModel> connect,
        Action<DesktopProfileViewModel, string> rename,
        Action<DesktopProfileViewModel> delete)
    {
        _record = record;
        _connect = connect;
        _rename = rename;
        _delete = delete;
        _editingName = DisplayName;
        ConnectCommand = new AsyncRelayCommand(() =>
        {
            _connect(this);
            return Task.CompletedTask;
        });
        BeginRenameCommand = new RelayCommand(() =>
        {
            EditingName = DisplayName;
            IsEditing = true;
        });
        SaveNameCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(EditingName))
            {
                _rename(this, EditingName.Trim());
            }

            IsEditing = false;
        });
        CancelRenameCommand = new RelayCommand(() => IsEditing = false);
        DeleteCommand = new RelayCommand(() => _delete(this));
    }

    public string Id => _record.Id;

    public DesktopProfileRecord Record => _record;

    /// <summary>Pairing reconstructed from the stored profile (v2 mode/gateway/fallback survive a round-trip).</summary>
    public DshPairingInfo ToPairing()
    {
        DshPairingInfo? fallback = null;
        if (!string.IsNullOrWhiteSpace(_record.LocalFallbackUri) &&
            DshPairingInfo.TryParse(_record.LocalFallbackUri, out var parsedFallback) &&
            parsedFallback is not null)
        {
            fallback = parsedFallback;
        }

        return new DshPairingInfo(
            _record.Host,
            _record.Port,
            _record.Token,
            _record.SpkiSha256,
            DisplayName,
            string.Equals(_record.Mode, "gateway", StringComparison.OrdinalIgnoreCase)
                ? DshPairingMode.Gateway
                : DshPairingMode.Local,
            _record.GatewayOnline,
            fallback);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_record.Name)
        ? DefaultName(_record)
        : _record.Name;

    public string Host => _record.Host;

    public int Port => _record.Port;

    public string AddressText => $"{_record.Host}:{_record.Port}";

    /// <summary>LAN (direct relay) vs 网关 (v2 gateway pairing).</summary>
    public string ModeText
    {
        get
        {
            if (string.Equals(_record.Mode, "gateway", StringComparison.OrdinalIgnoreCase))
            {
                return _record.GatewayOnline switch
                {
                    true => "网关 · 在线",
                    false => "网关 · 离线",
                    _ => "网关"
                };
            }

            return "LAN";
        }
    }

    public string TokenMasked
    {
        get
        {
            var token = _record.Token ?? string.Empty;
            if (token.Length <= 6)
            {
                return new string('•', Math.Max(0, token.Length));
            }

            return token[..3] + new string('•', 8) + token[^3..];
        }
    }

    public string LastStatusText => string.IsNullOrWhiteSpace(_record.LastStatus)
        ? "从未连接"
        : _record.LastStatus!;

    public string LastConnectedText
    {
        get
        {
            if (_record.LastConnectedAt <= 0)
            {
                return string.Empty;
            }

            var time = DateTimeOffset.FromUnixTimeMilliseconds(_record.LastConnectedAt).ToLocalTime();
            return "最近 " + time.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(ShowRow));
            }
        }
    }

    public bool ShowRow => !IsEditing;

    public string EditingName
    {
        get => _editingName;
        set => SetProperty(ref _editingName, value);
    }

    public ICommand ConnectCommand { get; }

    public ICommand BeginRenameCommand { get; }

    public ICommand SaveNameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    public ICommand DeleteCommand { get; }

    internal void ApplyRecord(DesktopProfileRecord record)
    {
        _record = record;
        Raise(
            nameof(DisplayName),
            nameof(Host),
            nameof(Port),
            nameof(AddressText),
            nameof(ModeText),
            nameof(TokenMasked),
            nameof(LastStatusText),
            nameof(LastConnectedText),
            nameof(Record));
    }

    private static string DefaultName(DesktopProfileRecord record)
    {
        var token = record.Token ?? string.Empty;
        var suffix = token.Length > 6 ? token[..6] : token;
        return string.IsNullOrWhiteSpace(suffix) ? record.Host : "桌面 " + suffix;
    }
}

/// <summary>Desktop-management page state: saved profiles, add/scan/rename/delete and switching.</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly Func<DshPairingInfo?> _legacyPairing;
    private ProfileStoreDocument _document = new();
    private string _activeId = string.Empty;
    private bool _isLoading;
    private bool _isBusy;
    private string _message = string.Empty;
    private bool _hasError;
    private string _pairingInput = string.Empty;

    public ProfilesViewModel(Func<DshPairingInfo?> legacyPairing)
    {
        _legacyPairing = legacyPairing;
        AddAndConnectCommand = new AsyncRelayCommand(AddAndConnectAsync);
        ScanQrCommand = new AsyncRelayCommand(ScanQrAsync);
        LoadFromFileAndAddCommand = new AsyncRelayCommand(LoadFromFileAndAddAsync);
        RefreshCommand = new AsyncRelayCommand(() => InitializeAsync(forceReload: true));
    }

    public ObservableCollection<DesktopProfileViewModel> Profiles { get; } = new();

    /// <summary>Main shell hook: connect to the selected profile (also used for newly added ones).</summary>
    public Func<DshPairingInfo, Task>? ConnectRequested { get; set; }

    public string PairingInput
    {
        get => _pairingInput;
        set => SetProperty(ref _pairingInput, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public ICommand AddAndConnectCommand { get; }

    public ICommand ScanQrCommand { get; }

    public ICommand LoadFromFileAndAddCommand { get; }

    public ICommand RefreshCommand { get; }

    public DesktopProfileViewModel? ActiveProfile => Profiles.FirstOrDefault(profile => profile.IsActive);

    public string ActiveProfileName => ActiveProfile?.DisplayName ?? "未配对";

    public string ActiveProfileAddress => ActiveProfile?.AddressText ?? string.Empty;

    public string ActiveProfileTokenMasked => ActiveProfile?.TokenMasked ?? string.Empty;

    public string ActiveProfileMode => ActiveProfile?.ModeText ?? string.Empty;

    public bool HasActiveProfile => ActiveProfile is not null;

    public bool HasActiveAddress => !string.IsNullOrWhiteSpace(ActiveProfileAddress);

    public bool HasActiveMode => !string.IsNullOrWhiteSpace(ActiveProfileMode);

    public bool HasProfiles => Profiles.Count > 0;

    public string ProfilesSummary => Profiles.Count == 0
        ? "还没有保存的桌面"
        : $"{Profiles.Count} 个已保存桌面";

    /// <summary>Loads the persisted profiles (migrating the legacy single pairing when needed).</summary>
    public async Task InitializeAsync(bool forceReload = false)
    {
        // A background load (started before the secure pairing restore finished) may still be in flight:
        // wait for it, then re-check so the legacy pairing gets migrated once it is available.
        if (IsLoading)
        {
            var waited = 0;
            while (IsLoading && waited < 5000)
            {
                await Task.Delay(50).ConfigureAwait(true);
                waited += 50;
            }
        }

        if (!forceReload && Profiles.Count > 0)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var document = await ProfileStore.LoadAsync().ConfigureAwait(true) ?? new ProfileStoreDocument();
            _document = document;
            _activeId = document.ActiveId ?? string.Empty;

            if (document.Profiles.Count == 0)
            {
                var legacy = _legacyPairing();
                if (legacy is not null)
                {
                    document.Profiles.Add(CreateRecord(legacy));
                    _activeId = document.Profiles[0].Id;
                    await ProfileStore.SaveAsync(document).ConfigureAwait(true);
                }
            }

            RebuildProfiles();
        }
        catch (Exception ex)
        {
            HasError = true;
            Message = "读取桌面列表失败: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Records a successful connection: adds/updates the profile and marks it active.</summary>
    public async Task RememberConnectedAsync(DshPairingInfo pairing)
    {
        var record = FindRecord(pairing);
        if (record is null)
        {
            record = CreateRecord(pairing);
            _document.Profiles.Add(record);
        }
        else
        {
            ApplyPairingMetadata(record, pairing);
        }

        record.LastConnectedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        record.LastStatus = "已连接";
        _activeId = record.Id;
        _document.ActiveId = _activeId;
        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        RebuildActiveState();
        RaiseActive();
    }

    public async Task MarkConnectionFailedAsync(DshPairingInfo pairing)
    {
        var record = FindRecord(pairing);
        if (record is null)
        {
            return;
        }

        record.LastStatus = "连接失败";
        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        RebuildActiveState();
    }

    public async Task MarkDisconnectedAsync()
    {
        var active = ActiveProfile;
        if (active is null)
        {
            return;
        }

        active.Record.LastStatus = "已断开";
        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        RebuildActiveState();
    }

    private async Task AddAndConnectAsync()
    {
        var text = PairingInput;
        if (string.IsNullOrWhiteSpace(text))
        {
            HasError = true;
            Message = "请先粘贴 dshremote://pair?... 配对 URI。";
            return;
        }

        if (!DshPairingInfo.TryParse(text, out var pairing) || pairing is null)
        {
            HasError = true;
            Message = "配对 URI 无法识别，请检查后重试。";
            return;
        }

        PairingInput = string.Empty;
        await AddPairingAsync(pairing, connect: true).ConfigureAwait(true);
    }

    private async Task ScanQrAsync()
    {
        try
        {
            var scanned = await PlatformServices.ScanQrAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(scanned))
            {
                HasError = true;
                Message = PlatformServices.IsAndroid
                    ? "扫码被取消，或当前设备未提供扫码能力。"
                    : "桌面壳不支持扫码，请粘贴配对 URI 或从 pairing.json 加载。";
                return;
            }

            if (!DshPairingInfo.TryParse(scanned, out var pairing) || pairing is null)
            {
                HasError = true;
                Message = "扫到的内容不是有效的配对 URI。";
                return;
            }

            await AddPairingAsync(pairing, connect: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HasError = true;
            Message = "扫码失败: " + ex.Message;
        }
    }

    private async Task LoadFromFileAndAddAsync()
    {
        if (!AppHost.TryLoadPairingFromFile(out var pairing, out var sourcePath, out var error) || pairing is null)
        {
            HasError = true;
            Message = error ?? "读取配对文件失败。";
            return;
        }

        Message = "已从 " + Path.GetFileName(sourcePath ?? "pairing.json") + " 载入配对。";
        await AddPairingAsync(pairing, connect: true).ConfigureAwait(true);
    }

    private async Task AddPairingAsync(DshPairingInfo pairing, bool connect)
    {
        var record = FindRecord(pairing);
        if (record is null)
        {
            record = CreateRecord(pairing);
            _document.Profiles.Add(record);
        }
        else
        {
            ApplyPairingMetadata(record, pairing);
        }

        _activeId = record.Id;
        _document.ActiveId = _activeId;
        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        RebuildProfiles();
        HasError = false;
        Message = connect ? "已添加并开始连接: " + record.Host + ":" + record.Port : "已添加桌面。";

        if (connect && ConnectRequested is not null)
        {
            IsBusy = true;
            try
            {
                await ConnectRequested(pairing).ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    private async void OnConnectProfile(DesktopProfileViewModel profile)
    {
        _activeId = profile.Id;
        _document.ActiveId = _activeId;
        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        RebuildActiveState();
        RaiseActive();

        if (ConnectRequested is not null)
        {
            await ConnectRequested(profile.ToPairing()).ConfigureAwait(true);
        }
    }

    private async void OnRenameProfile(DesktopProfileViewModel profile, string newName)
    {
        profile.Record.Name = newName;
        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        profile.ApplyRecord(profile.Record);
        RebuildActiveState();
        RaiseActive();
    }

    private async void OnDeleteProfile(DesktopProfileViewModel profile)
    {
        _document.Profiles.Remove(profile.Record);
        if (string.Equals(_activeId, profile.Id, StringComparison.Ordinal))
        {
            _activeId = _document.Profiles.FirstOrDefault()?.Id ?? string.Empty;
            _document.ActiveId = _activeId;
        }

        await ProfileStore.SaveAsync(_document).ConfigureAwait(true);
        Profiles.Remove(profile);
        RebuildActiveState();
        Raise(nameof(HasProfiles), nameof(ProfilesSummary), nameof(ActiveProfile));
        Message = "已删除桌面: " + profile.DisplayName;
        HasError = false;
    }

    private void RebuildProfiles()
    {
        Profiles.Clear();
        foreach (var record in _document.Profiles)
        {
            var profile = new DesktopProfileViewModel(record, OnConnectProfile, OnRenameProfile, OnDeleteProfile)
            {
                IsActive = string.Equals(record.Id, _activeId, StringComparison.Ordinal)
            };
            Profiles.Add(profile);
        }

        Raise(nameof(HasProfiles), nameof(ProfilesSummary), nameof(ActiveProfile));
        RaiseActive();
    }

    private void RebuildActiveState()
    {
        foreach (var profile in Profiles)
        {
            profile.IsActive = string.Equals(profile.Id, _activeId, StringComparison.Ordinal);
        }
    }

    private void RaiseActive()
    {
        Raise(
            nameof(ActiveProfile),
            nameof(ActiveProfileName),
            nameof(ActiveProfileAddress),
            nameof(ActiveProfileTokenMasked),
            nameof(ActiveProfileMode),
            nameof(HasActiveProfile),
            nameof(HasActiveAddress),
            nameof(HasActiveMode));
    }

    private DesktopProfileRecord? FindRecord(DshPairingInfo pairing)
        => _document.Profiles.FirstOrDefault(record =>
            string.Equals(record.Host, pairing.Host, StringComparison.OrdinalIgnoreCase) &&
            record.Port == pairing.Port &&
            string.Equals(record.Token, pairing.Token, StringComparison.Ordinal));

    private static DesktopProfileRecord CreateRecord(DshPairingInfo pairing) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..10],
        Name = pairing.Name ?? string.Empty,
        Host = pairing.Host,
        Port = pairing.Port,
        Token = pairing.Token,
        SpkiSha256 = pairing.SpkiSha256,
        Mode = pairing.IsGateway ? "gateway" : "local",
        GatewayOnline = pairing.GatewayOnline,
        LocalFallbackUri = pairing.LocalFallback?.ToPairingUri(),
        Gateway = pairing.IsGateway ? $"{pairing.Host}:{pairing.Port}" : null
    };

    private static void ApplyPairingMetadata(DesktopProfileRecord record, DshPairingInfo pairing)
    {
        record.Name = string.IsNullOrWhiteSpace(pairing.Name) ? record.Name : pairing.Name!;
        record.SpkiSha256 = pairing.SpkiSha256 ?? record.SpkiSha256;
        record.Mode = pairing.IsGateway ? "gateway" : "local";
        record.GatewayOnline = pairing.GatewayOnline;
        record.LocalFallbackUri = pairing.LocalFallback?.ToPairingUri() ?? record.LocalFallbackUri;
        record.Gateway = pairing.IsGateway ? $"{pairing.Host}:{pairing.Port}" : record.Gateway;
    }
}
