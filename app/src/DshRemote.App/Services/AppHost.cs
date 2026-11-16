using DshRemote.Core;

namespace DshRemote.App.Services;

/// <summary>
/// Process-wide seams the platform heads (desktop / Android) fill in before the Avalonia app starts.
/// The shared library never references a platform SDK; heads inject the pieces that need one.
/// </summary>
public static class AppHost
{
    /// <summary>
    /// Explicit factory for the protocol client. The app wires <c>DshRemote.Core.DshClient</c> in
    /// <c>App.OnFrameworkInitializationCompleted</c>; a head project can override it or point it at a
    /// different implementation. When null, <see cref="CreateClient"/> probes Core by reflection and
    /// finally falls back to the demo client.
    /// </summary>
    public static Func<IDshClient>? ClientFactory { get; set; }

    /// <summary>Platform-specific writable directory for settings (Android: <c>FilesDir</c>).</summary>
    public static Func<string?>? StateDirectoryProvider { get; set; }


    /// <summary>
    /// Candidate files a "load pairing from file" button may read, in priority order. Heads inject this:
    /// desktop points at <c>%USERPROFILE%\.dsh\remote\pairing.json</c>; Android points at the app's
    /// external files dir (adb-pushable, no storage permission needed).
    /// </summary>
    public static Func<IReadOnlyList<string>>? PairingFilePathsProvider { get; set; }

    /// <summary>True when the current platform can load pairing info from a file.</summary>
    public static bool CanLoadPairingFromFile => PairingFilePathsProvider is not null;

    /// <summary>
    /// Optional UI smoke-run hook for the desktop shell (e.g. <c>--demo-chat-send</c>), so screenshots
    /// and manual checks do not need simulated mouse input. Ignored when null.
    /// </summary>
    public static string? StartupAction { get; set; }

    /// <summary>
    /// Optional pairing URI to auto-connect with on startup (desktop smoke runs / E2E). Only used by the
    /// desktop head; when set the connect page is skipped and the app connects immediately.
    /// </summary>
    public static string? StartupPairingUri { get; set; }

    /// <summary>Desktop smoke hook: dispatch a <c>dshremote://pair?...</c> deep link after platform wiring.</summary>
    public static string? StartupDeepLink { get; set; }

    /// <summary>Optional session index for the desktop smoke hook (0 = first row).</summary>
    public static int StartupOpenIndex { get; set; }

    /// <summary>
    /// Desktop automation probe (<c>--longshot-probe=&lt;png path&gt;</c>): when set, the head opens the demo
    /// chat, renders the long screenshot through the production path, writes the PNG(s) plus a
    /// <c>.report.txt</c> there, and exits with 0. Used by CI/agents to verify the renderer visually.
    /// </summary>
    public static string? LongShotProbeOutputPath { get; set; }

    /// <summary>Optional pixel/edge budget override for the probe (tiny demos exercise multi-image splits).</summary>
    public static LongShotLimits? LongShotProbeLimits { get; set; }

    public static string StateDirectory
    {
        get
        {
            var injected = StateDirectoryProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(injected))
            {
                return injected;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Path.GetTempPath();
            }

            return Path.Combine(local, "DshRemote");
        }
    }

    /// <summary>True when a real <see cref="IDshClient"/> is available (otherwise the demo client is used).</summary>
    public static bool IsRealClientAvailable => ClientFactory is not null || RealClientType is not null;

    private static Type? RealClientType
    {
        get
        {
            try
            {
                return typeof(DshPairingInfo).Assembly.GetType("DshRemote.Core.DshClient", throwOnError: false);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Creates the protocol client. <paramref name="demo"/> forces the in-app fake (used by the
    /// "preview with demo data" affordance and by UI work before the real client is linked).
    /// </summary>
    public static IDshClient CreateClient(bool demo = false)
    {
        if (demo)
        {
            return new DemoDshClient();
        }

        if (ClientFactory is not null)
        {
            return ClientFactory();
        }

        // TODO(integration): once DshRemote.Core ships DshClient this resolves automatically. If its
        // constructor needs dependencies, set AppHost.ClientFactory in Desktop Program / Android
        // Application instead of touching this fallback.
        var type = RealClientType;
        if (type is not null && typeof(IDshClient).IsAssignableFrom(type))
        {
            try
            {
                if (Activator.CreateInstance(type) is IDshClient client)
                {
                    return client;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppHost] could not construct {type.FullName}: {ex.Message}");
            }
        }

        return new DemoDshClient();
    }

    /// <summary>
    /// Loads pairing info from the first readable candidate file. Accepts the relay's JSON
    /// (<c>host/port/token/fp/pairingUri/name</c>) or a plain <c>pairing.txt</c> holding the pairing URI.
    /// Read-only; never modifies the file or <c>~/.dsh</c>.
    /// </summary>
    public static bool TryLoadPairingFromFile(out DshPairingInfo? pairing, out string? sourcePath, out string? error)
    {
        pairing = null;
        sourcePath = null;
        error = null;

        var provider = PairingFilePathsProvider;
        if (provider is null)
        {
            error = "当前平台未提供配对文件入口，请手动粘贴配对 URI。";
            return false;
        }

        IReadOnlyList<string> candidates;
        try
        {
            candidates = provider();
        }
        catch (Exception ex)
        {
            error = "无法定位配对文件目录: " + ex.Message;
            return false;
        }

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "未找到配对文件。推送示例: adb push %USERPROFILE%\\.dsh\\remote\\pairing.json " +
                    "/sdcard/Android/data/dev.dshremote.app/files/pairing.json";
            return false;
        }

        try
        {
            sourcePath = path;
            var content = File.ReadAllText(path).Trim();
            if (content.Length == 0)
            {
                error = "配对文件是空的: " + path;
                return false;
            }

            // JSON documents go through Core's schema-aware reader; plain text files hold just the URI.
            if (content.TrimStart().StartsWith('{'))
            {
                if (DshPairingFile.TryParse(content, out pairing, out var parseError) && pairing is not null)
                {
                    return true;
                }

                error = "配对文件解析失败: " + (parseError ?? "未知格式");
                return false;
            }

            if (DshPairingInfo.TryParse(content, out pairing) && pairing is not null)
            {
                return true;
            }

            error = "文件内容既不是 JSON 也不是可识别的配对 URI: " + path;
            return false;
        }
        catch (Exception ex)
        {
            error = "读取配对文件失败: " + ex.Message;
            return false;
        }
    }
}
