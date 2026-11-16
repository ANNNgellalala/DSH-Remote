using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using DshRemote.App.Services;
using DshRemote.Core;

namespace DshRemote.App.ViewModels;

/// <summary>
/// Pairing form state plus its on-disk persistence. The file lives in <see cref="AppHost.StateDirectory"/>
/// (<c>%LocalAppData%\DshRemote</c> on desktop, the app's private <c>FilesDir</c> on Android).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string _pairingUri = string.Empty;
    private string _host = string.Empty;
    private string _port = "8443";
    private string _token = string.Empty;
    private string _spki = string.Empty;
    private string _name = string.Empty;
    private string _status = "未连接";
    private string? _lastWorkspaceId;
    private bool _expandComposerFullscreen;

    // ---- Windows ACL guard card (desktop-side state; relay 0.5.0+) -------------------------------
    private IAclGuardClient? _aclClient;
    private string _aclStatusText = "未连接（连接桌面后可体检）";
    private string _aclDetailText = string.Empty;
    private string _aclResultText = string.Empty;
    private bool _aclBusy;
    private DateTimeOffset? _aclCheckedAt;

    /// <summary>
    /// True while a persisted document is being replayed into the properties. The preference setters
    /// persist on every change, so without this guard a restore would write the values of the document
    /// being restored over the live <c>settings.json</c>. That is exactly what used to reset the settings
    /// page after every restart: the pairing snapshot restored from the secret store was taken when the
    /// desktop connected, so its (possibly old) preferences overwrote the user's newer choices.
    /// </summary>
    private bool _isRestoring;

    /// <summary>Full <c>dshremote://pair?...</c> URI; the fastest path on a phone (paste from the desktop).</summary>
    public string PairingUri
    {
        get => _pairingUri;
        set => SetProperty(ref _pairingUri, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Token
    {
        get => _token;
        set => SetProperty(ref _token, value);
    }

    /// <summary>Optional pinned TLS SPKI SHA-256 (from the pairing file / URI).</summary>
    public string SpkiSha256
    {
        get => _spki;
        set => SetProperty(ref _spki, value);
    }

    /// <summary>Desktop display name from the pairing file, shown as "已配对: NAME".</summary>
    public string DesktopName
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Raise(nameof(HasDesktopName), nameof(PairedWithText));
            }
        }
    }

    public bool HasDesktopName => !string.IsNullOrWhiteSpace(DesktopName);

    public string PairedWithText => HasDesktopName ? "桌面: " + DesktopName : string.Empty;

    /// <summary>Workspace the session list last showed (persisted so the picker reopens there).</summary>
    public string? LastWorkspaceId
    {
        get => _lastWorkspaceId;
        set => SetProperty(ref _lastWorkspaceId, value);
    }

    /// <summary>
    /// Desktop-side auto-approval rules. The range is stored on the desktop relay (so it keeps working
    /// while the phone app is closed); this view-model only edits it through the relay API.
    /// </summary>
    public AutoApproveViewModel AutoApprove { get; } = new();

    /// <summary>When enabled, focusing/typing in the composer opens a full-screen editor layer.</summary>
    public bool ExpandComposerFullscreen
    {
        get => _expandComposerFullscreen;
        set
        {
            if (SetProperty(ref _expandComposerFullscreen, value))
            {
                PersistPreferenceChange();
            }
        }
    }

    /// <summary>Persists user-driven preference changes; restores from disk must not write back.</summary>
    private void PersistPreferenceChange()
    {
        if (_isRestoring)
        {
            return;
        }

        SavePreferences();
    }

    /// <summary>Last connection attempt result shown under the form.</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Product name shown in the settings page's about section.</summary>
    public string ProductName => "DSH Remote";

    /// <summary>
    /// Version shown in the settings page's about section. The shared App library has no product version
    /// constant of its own (the Android head sets the packaged version at build time), so read the entry
    /// assembly's informational version — that is the version of the app the user actually installed.
    /// </summary>
    public string VersionText => ResolveVersionText();

    private static string ResolveVersionText()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;
            var informational = assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // "1.2.5+<commit>" -> "1.2.5"
                var metadata = informational.IndexOf('+');
                return metadata > 0 ? informational[..metadata] : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>Parses whatever the user typed into a <see cref="DshPairingInfo"/>.</summary>
    public bool TryBuildPairing(out DshPairingInfo? pairing, out string? error)
    {
        pairing = null;
        error = null;

        if (!string.IsNullOrWhiteSpace(PairingUri))
        {
            if (DshPairingInfo.TryParse(PairingUri, out pairing, separateToken: Token))
            {
                pairing = ApplyExtraFields(pairing);
                return true;
            }

            error = "配对 URI 无法识别。期望形如 dshremote://pair?v=1&host=192.0.2.20&port=8443&token=… 或 https://host:port/?token=…";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Token))
        {
            error = "请粘贴配对 URI，或至少填写 host 与 token。";
            return false;
        }

        if (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535)
        {
            error = "端口必须是 1–65535 之间的数字。";
            return false;
        }

        pairing = new DshPairingInfo(
            Host.Trim(),
            port,
            Token.Trim(),
            NullIfBlank(SpkiSha256),
            NullIfBlank(DesktopName));
        return true;
    }

    /// <summary>Fills every form field from a known pairing (URI paste, pairing file, saved settings).</summary>
    public void ApplyPairing(DshPairingInfo pairing)
    {
        PairingUri = pairing.ToPairingUri();
        Host = pairing.Host;
        Port = pairing.Port.ToString(CultureInfo.InvariantCulture);
        Token = pairing.Token;
        SpkiSha256 = pairing.SpkiSha256 ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pairing.Name))
        {
            DesktopName = pairing.Name!;
        }
    }

    private DshPairingInfo? ApplyExtraFields(DshPairingInfo? parsed)
    {
        if (parsed is null)
        {
            return null;
        }

        return parsed with
        {
            SpkiSha256 = parsed.SpkiSha256 ?? NullIfBlank(SpkiSha256),
            Name = parsed.Name ?? NullIfBlank(DesktopName)
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>If the pasted URI parses, fill the manual fields so the user can see/edit what was recognised.</summary>
    public void SyncManualFieldsFromUri()
    {
        if (string.IsNullOrWhiteSpace(PairingUri) ||
            !DshPairingInfo.TryParse(PairingUri, out var pairing, separateToken: Token) ||
            pairing is null)
        {
            return;
        }

        Host = pairing.Host;
        Port = pairing.Port.ToString(CultureInfo.InvariantCulture);
        Token = pairing.Token;
        if (!string.IsNullOrWhiteSpace(pairing.SpkiSha256))
        {
            SpkiSha256 = pairing.SpkiSha256;
        }

        if (!string.IsNullOrWhiteSpace(pairing.Name))
        {
            DesktopName = pairing.Name!;
        }
    }

    public void Load()
    {
        _isRestoring = true;
        try
        {
            var path = Path.Combine(AppHost.StateDirectory, FileName);
            if (!File.Exists(path))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonOptions);
            if (data is null)
            {
                return;
            }

            PairingUri = data.PairingUri ?? string.Empty;
            Host = data.Host ?? string.Empty;
            Port = (data.Port is > 0 ? data.Port : 8443).ToString(CultureInfo.InvariantCulture);
            Token = data.Token ?? string.Empty;
            SpkiSha256 = data.SpkiSha256 ?? string.Empty;
            DesktopName = data.Name ?? string.Empty;
            Status = string.IsNullOrWhiteSpace(PairingUri) && string.IsNullOrWhiteSpace(Host)
                ? "未连接"
                : "上次配对已保存，点击「连接」继续。";
            ExpandComposerFullscreen = data.ExpandComposerFullscreen;

            if (!string.IsNullOrWhiteSpace(PairingUri))
            {
                SyncManualFieldsFromUri();
            }
        }
        catch (Exception ex)
        {
            Status = "读取本地设置失败: " + ex.Message;
        }
        finally
        {
            _isRestoring = false;
        }
    }

    public void Save(DshPairingInfo pairing)
    {
        try
        {
            var directory = AppHost.StateDirectory;
            Directory.CreateDirectory(directory);
            var data = ToFile(pairing);
            data.LastWorkspaceId = LastWorkspaceId;
            data.ExpandComposerFullscreen = ExpandComposerFullscreen;
            var path = Path.Combine(directory, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, path, overwrite: true);

            ApplyPairing(pairing);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] save failed: {ex}");
        }
    }

    /// <summary>
    /// Re-applies preference fields from settings.json. The head may register its state directory after the
    /// view-model is constructed (Android), so the first <see cref="Load"/> can miss the file; calling this
    /// once platform services are up restores preferences such as the full-screen composer setting.
    /// </summary>
    public void ReloadPreferences()
    {
        _isRestoring = true;
        try
        {
            var path = Path.Combine(AppHost.StateDirectory, FileName);
            if (!File.Exists(path))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonOptions);
            if (data is null)
            {
                return;
            }

            ExpandComposerFullscreen = data.ExpandComposerFullscreen;
            if (!string.IsNullOrWhiteSpace(data.LastWorkspaceId))
            {
                LastWorkspaceId = data.LastWorkspaceId;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] reload preferences failed: {ex.Message}");
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>Persists UI preferences that are not part of the pairing document (workspace, full-screen).</summary>
    public void SavePreferences()
    {
        try
        {
            var directory = AppHost.StateDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            SettingsFile data;
            if (File.Exists(path))
            {
                try
                {
                    data = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonOptions) ?? new SettingsFile();
                }
                catch
                {
                    data = new SettingsFile();
                }
            }
            else
            {
                data = new SettingsFile();
            }

            data.LastWorkspaceId = LastWorkspaceId;
            data.ExpandComposerFullscreen = ExpandComposerFullscreen;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] save preferences failed: {ex}");
        }
    }

    /// <summary>
    /// Serializes the current pairing for a platform secret store (Android Keystore). Only the connection
    /// fields belong in this snapshot: it is written when a connection succeeds, so anything else would be
    /// replayed stale on the next cold start (preferences live in <c>settings.json</c> and are owned by it).
    /// </summary>
    public string? ToSecretJson()
    {
        try
        {
            if (!TryBuildPairing(out var pairing, out _) || pairing is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(ToFile(pairing), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Restores pairing state from a previously stored secret JSON document.</summary>
    public bool LoadFromSecretJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        _isRestoring = true;
        try
        {
            var data = JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions);
            if (data is null)
            {
                return false;
            }

            ApplyStoredPairing(data);
            Status = "已从安全存储恢复配对，点击「连接」继续。";
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] secure load failed: {ex.Message}");
            return false;
        }
        finally
        {
            _isRestoring = false;
        }
    }

    private static SettingsFile ToFile(DshPairingInfo pairing) => new()
    {
        PairingUri = pairing.ToPairingUri(),
        Host = pairing.Host,
        Port = pairing.Port,
        Token = pairing.Token,
        SpkiSha256 = pairing.SpkiSha256,
        Name = pairing.Name,
        SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    /// <summary>
    /// Applies only the connection fields of a stored document. Preferences (full-screen composer) and the
    /// last workspace are deliberately not taken from here: the secret-store snapshot is older than the live
    /// settings file, and restoring its values used to reset the settings page on every cold start.
    /// <see cref="ReloadPreferences"/> is the authoritative source for them.
    /// </summary>
    private void ApplyStoredPairing(SettingsFile data)
    {
        PairingUri = data.PairingUri ?? string.Empty;
        Host = data.Host ?? string.Empty;
        Port = (data.Port is > 0 ? data.Port : 8443).ToString(CultureInfo.InvariantCulture);
        Token = data.Token ?? string.Empty;
        SpkiSha256 = data.SpkiSha256 ?? string.Empty;
        DesktopName = data.Name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(PairingUri))
        {
            SyncManualFieldsFromUri();
        }
    }

    public void Clear()
    {
        try
        {
            var path = Path.Combine(AppHost.StateDirectory, FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] clear failed: {ex}");
        }

        PairingUri = string.Empty;
        Host = string.Empty;
        Token = string.Empty;
        SpkiSha256 = string.Empty;
        DesktopName = string.Empty;
        Port = "8443";
        Status = "已清除本机配对信息。";
    }

    public SettingsViewModel()
    {
        RefreshAclCommand = new AsyncRelayCommand(RefreshAclAsync, () => AclAvailable);
        CheckAclCommand = new AsyncRelayCommand(RunAclCheckAsync, () => AclAvailable && !AclBusy);
    }

    // ---- Windows ACL guard card -----------------------------------------------------------------

    /// <summary>True once a connected client can answer the relay's ACL guard API.</summary>
    public bool AclAvailable => _aclClient is not null;

    /// <summary>One-line summary (workspace counts, or why the card cannot report).</summary>
    public string AclStatusText
    {
        get => _aclStatusText;
        private set => SetProperty(ref _aclStatusText, value);
    }

    /// <summary>First few workspace roots with their admission state, when any are known.</summary>
    public string AclDetailText
    {
        get => _aclDetailText;
        private set
        {
            if (SetProperty(ref _aclDetailText, value))
            {
                Raise(nameof(HasAclDetail));
            }
        }
    }

    public bool HasAclDetail => !string.IsNullOrWhiteSpace(_aclDetailText);

    /// <summary>Outcome of the last "一键体检并修复", or empty before the first run.</summary>
    public string AclResultText
    {
        get => _aclResultText;
        private set
        {
            if (SetProperty(ref _aclResultText, value))
            {
                Raise(nameof(HasAclResult));
            }
        }
    }

    public bool HasAclResult => !string.IsNullOrWhiteSpace(_aclResultText);

    public bool AclBusy
    {
        get => _aclBusy;
        private set
        {
            if (SetProperty(ref _aclBusy, value))
            {
                NotifyAclCommands();
            }
        }
    }

    /// <summary>"尚未体检" / "上次体检 MM-dd HH:mm".</summary>
    public string AclCheckedText => _aclCheckedAt is { } at
        ? "上次体检 " + at.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
        : "尚未体检";

    public ICommand RefreshAclCommand { get; }

    public ICommand CheckAclCommand { get; }

    /// <summary>Bind the guard API as soon as a client connects (called by <c>MainViewModel</c>).</summary>
    public void AttachAclClient(IAclGuardClient client)
    {
        _aclClient = client;
        Raise(nameof(AclAvailable));
        NotifyAclCommands();
        _ = RefreshAclAsync();
    }

    /// <summary>Drop the guard API when the client is disposed/replaced.</summary>
    public void DetachAclClient()
    {
        _aclClient = null;
        AclStatusText = "未连接（连接桌面后可体检）";
        AclDetailText = string.Empty;
        AclResultText = string.Empty;
        _aclCheckedAt = null;
        AclBusy = false;
        Raise(nameof(AclAvailable), nameof(AclCheckedText));
        NotifyAclCommands();
    }

    /// <summary>Read the guard's status snapshot (no probing, cheap).</summary>
    public async Task RefreshAclAsync()
    {
        var client = _aclClient;
        if (client is null)
        {
            return;
        }

        try
        {
            var status = await client.GetAclStatusAsync().ConfigureAwait(true);
            ApplyAclStatus(status);
        }
        catch (Exception ex)
        {
            AclStatusText = "读取失败: " + DescribeAclFailure(ex);
            AclDetailText = string.Empty;
        }

        Raise(nameof(AclCheckedText));
    }

    /// <summary>Run the desktop guard over the known workspace roots and repair what is missing.</summary>
    public async Task RunAclCheckAsync()
    {
        var client = _aclClient;
        if (client is null || AclBusy)
        {
            return;
        }

        AclBusy = true;
        try
        {
            var result = await client.CheckAclAsync(repair: true).ConfigureAwait(true);
            var ready = result.Roots.Count(root => root.Ready);
            var repaired = result.Roots.Count(root => root.Repaired);
            var failed = result.Roots.Count(root => !root.Ready);
            AclResultText = result.Roots.Count == 0
                ? "体检完成：桌面还没有已知工作区（打开一个会话后会自动登记）。"
                : $"体检完成：{result.Roots.Count} 个工作区，可写 {ready}，本次修复 {repaired}，仍需处理 {failed}。";
            _aclCheckedAt = result.CheckedAt ?? DateTimeOffset.Now;
            Raise(nameof(AclCheckedText));
            await RefreshAclAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AclResultText = "体检失败: " + DescribeAclFailure(ex);
        }
        finally
        {
            AclBusy = false;
        }
    }

    private void ApplyAclStatus(DshAclGuardStatus status)
    {
        _aclCheckedAt = status.CheckedAt ?? _aclCheckedAt;
        if (!status.Enabled)
        {
            AclStatusText = "桌面未启用（非 Windows，或已在 relay 配置中关闭）";
            AclDetailText = string.Empty;
            return;
        }

        if (status.Roots.Count == 0)
        {
            AclStatusText = "暂无工作区（打开一个会话后自动登记）";
            AclDetailText = string.Empty;
            return;
        }

        var ready = status.Roots.Count(root => string.Equals(root.State, "ready", StringComparison.OrdinalIgnoreCase));
        var failed = status.Roots.Count(root => string.Equals(root.State, "failed", StringComparison.OrdinalIgnoreCase));
        var pending = status.Roots.Count - ready - failed;
        AclStatusText = $"共 {status.Roots.Count} 个工作区 · 正常 {ready} · 待体检 {pending} · 异常 {failed}";

        var roots = status.Roots
            .Take(3)
            .Select(root => $"{Shorten(root.Root)}（{StateLabel(root.State)}）");
        var more = status.Roots.Count > 3 ? $" 等 {status.Roots.Count} 个" : string.Empty;
        AclDetailText = string.Join("；", roots) + more;
    }

    private static string StateLabel(string state) => state.ToLowerInvariant() switch
    {
        "ready" => "正常",
        "stale" => "待体检",
        "failed" => "异常",
        _ => "未知"
    };

    private static string Shorten(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return "?";
        }

        // Keep the drive and the last two segments; the card must stay readable on a phone.
        var parts = root.TrimEnd('\\', '/').Split('\\', '/');
        return parts.Length <= 3 ? root : $"…\\{parts[^2]}\\{parts[^1]}";
    }

    private static string DescribeAclFailure(Exception ex) => ex switch
    {
        DshRemoteException { Code: DshRemoteException.Codes.Transport } => "桌面未连接",
        DshRemoteException { Code: DshRemoteException.Codes.Unauthorized } => "配对口令已失效，请重新配对",
        DshRemoteException { Code: DshRemoteException.Codes.Forbidden } => "桌面拒绝了该请求",
        DshRemoteException { Code: DshRemoteException.Codes.ConnectFailed } => ex.Message,
        _ => ex.Message
    };

    private void NotifyAclCommands()
    {
        if (RefreshAclCommand is AsyncRelayCommand refresh)
        {
            refresh.NotifyCanExecuteChanged();
        }

        if (CheckAclCommand is AsyncRelayCommand check)
        {
            check.NotifyCanExecuteChanged();
        }
    }

    private sealed class SettingsFile
    {
        public string? PairingUri { get; set; }

        public string? Host { get; set; }

        public int Port { get; set; }

        public string? Token { get; set; }

        public string? SpkiSha256 { get; set; }

        public string? Name { get; set; }

        public string? LastWorkspaceId { get; set; }

        public bool ExpandComposerFullscreen { get; set; }

        public long SavedAt { get; set; }
    }
}
