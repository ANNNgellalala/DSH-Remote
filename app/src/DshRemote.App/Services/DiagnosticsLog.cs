using System.Text;
using Avalonia.Threading;

namespace DshRemote.App.Services;

/// <summary>
/// Lightweight on-device diagnostics for crash/hang reports (1.6.2+).
/// <para>
/// Writes a rolling log with recent breadcrumbs, managed unhandled exceptions (UI thread, background
/// tasks, app domain) and a UI-thread heartbeat watchdog. The file lives next to <c>pairing.json</c> in
/// the app-specific external files directory on Android, so it can be pulled without root:
/// <c>adb pull /sdcard/Android/data/dev.dshremote.app/files/dshremote-diagnostics.log</c>.
/// On the desktop head it lives under <c>~/.dsh/remote</c>.
/// </para>
/// <para>Everything is best-effort: a diagnostics failure must never affect the app.</para>
/// </summary>
public static class DiagnosticsLog
{
    private const int BreadcrumbLimit = 240;
    private const long MaxLogBytes = 512 * 1024;
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(6);

    private static readonly object Gate = new();
    private static readonly Queue<string> Breadcrumbs = new(BreadcrumbLimit);
    private static string? _path;
    private static bool _installed;
    private static long _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
    private static bool _hangRecorded;
    private static DateTime _hangSince;

    /// <summary>Absolute path of the rolling diagnostics file (null when no writable directory is known).</summary>
    public static string? LogPath => _path;

    /// <summary>Installs the exception hooks and starts the UI-hang watchdog (idempotent).</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _path = ResolveLogPath();
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Record("APPDOMAIN-UNHANDLED", args.ExceptionObject as Exception);
        }
        catch
        {
            // ignored
        }

        try
        {
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Record("TASK-UNOBSERVED", args.Exception);
                args.SetObserved();
            };
        }
        catch
        {
            // ignored
        }

        try
        {
            Dispatcher.UIThread.UnhandledException += (_, args) => Record("UI-THREAD-UNHANDLED", args.Exception);
        }
        catch
        {
            // ignored
        }

        try
        {
            var watchdog = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "dsh-diagnostics-watchdog"
            };
            watchdog.Start();
        }
        catch
        {
            // ignored
        }

        Trace($"install path={_path ?? "<none>"}");
    }

    /// <summary>Called once per second from the UI thread; a stall trips the hang watchdog.</summary>
    public static void Heartbeat()
    {
        var previousHang = _hangRecorded;
        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
        if (!previousHang)
        {
            return;
        }

        _hangRecorded = false;
        Trace($"UI resumed after ~{Math.Max(1, (int)(DateTime.UtcNow - _hangSince).TotalSeconds)}s stall");
    }

    /// <summary>Records a breadcrumb in memory and appends it to the rolling log.</summary>
    public static void Trace(string message)
    {
        var line = Format(message);
        lock (Gate)
        {
            Breadcrumbs.Enqueue(line);
            while (Breadcrumbs.Count > BreadcrumbLimit)
            {
                Breadcrumbs.Dequeue();
            }
        }

        Append(line);
    }

    /// <summary>Records an exception with the current breadcrumb tail (used by listeners in every head).</summary>
    public static void Record(string source, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + source + " =====");
        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
        }
        else
        {
            builder.AppendLine("<no exception object>");
        }

        builder.AppendLine("--- recent activity ---");
        lock (Gate)
        {
            foreach (var crumb in Breadcrumbs)
            {
                builder.AppendLine(crumb);
            }
        }

        Append(builder.ToString());
    }

    /// <summary>Returns the rolling log content (used by the Settings "copy diagnostics" action).</summary>
    public static string ReadAll()
    {
        try
        {
            return _path is not null && File.Exists(_path)
                ? File.ReadAllText(_path)
                : "(还没有诊断日志)";
        }
        catch (Exception ex)
        {
            return "(读取诊断日志失败: " + ex.Message + ")";
        }
    }

    /// <summary>Deletes the rolling log (used by the Settings "clear diagnostics" action).</summary>
    public static void Clear()
    {
        try
        {
            if (_path is not null)
            {
                File.Delete(_path);
                File.Delete(_path + ".1");
            }
        }
        catch
        {
            // ignored
        }

        Trace("diagnostics cleared");
    }

    private static void WatchdogLoop()
    {
        while (true)
        {
            try
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                if (!PlatformIntegration.IsAppForeground)
                {
                    continue;
                }

                var last = new DateTime(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);
                var stalled = DateTime.UtcNow - last;
                if (stalled < HangThreshold || _hangRecorded)
                {
                    continue;
                }

                _hangRecorded = true;
                _hangSince = last;
                Record("UI-HANG", new TimeoutException(
                    $"UI thread did not respond for ~{Math.Max(1, (int)stalled.TotalSeconds)}s"));
            }
            catch
            {
                // The watchdog must never throw.
            }
        }
    }

    private static string Format(string message) =>
        DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;

    private static void Append(string text)
    {
        try
        {
            var path = _path;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                var backup = path + ".1";
                File.Delete(backup);
                File.Move(path, backup);
            }

            File.AppendAllText(path, text + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // ignored: diagnostics are best effort
        }
    }

    private static string? ResolveLogPath()
    {
        // Prefer the app-specific external directory (adb-pullable without root on Android).
        try
        {
            var candidates = AppHost.PairingFilePathsProvider?.Invoke();
            var first = candidates?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var directory = Path.GetDirectoryName(first);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return Path.Combine(directory, "dshremote-diagnostics.log");
                }
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            var state = AppHost.StateDirectory;
            if (!string.IsNullOrWhiteSpace(state))
            {
                return Path.Combine(state, "dshremote-diagnostics.log");
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
