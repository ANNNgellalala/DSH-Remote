using System.Diagnostics;
using Avalonia;
using DshRemote.App.Services;

namespace DshRemote.App.Platform;

/// <summary>
/// Process-wide platform injection point. The shared Avalonia project never references an Android/desktop
/// SDK; each head fills these slots before (or while) the shared app starts, and the shared layer treats
/// every slot as optional (null means "this platform does not support that capability").
/// </summary>
/// <remarks>
/// Android wiring (<c>AndroidPlatformHost.Initialize</c>) registers:
/// <see cref="SecretStore"/> (Keystore AES/GCM), <see cref="StartForegroundConnection"/> /
/// <see cref="StopForegroundConnection"/> (dataSync foreground service) and
/// <see cref="NotificationBridge.Implementation"/> (approval/question/turn-finished notifications).
/// Deep links are dispatched by the Android activity through <see cref="DispatchDeepLink"/>;
/// if the shared layer has not attached <see cref="OnDeepLink"/> yet the URI is queued and replayed on
/// attach, so a cold-start deep link is never lost.
/// </remarks>
public static class PlatformServices
{
    private static readonly object Sync = new();
    private static readonly List<string> PendingDeepLinks = new();
    private static readonly List<string> PendingSessionOpens = new();
    private static Action<string, string>? _onDeepLink;
    private static Action<string>? _onSessionOpenRequested;

    /// <summary>Current platform name: <c>android</c>, <c>desktop</c> or <c>generic</c> (default).</summary>
    public static string PlatformName { get; set; } = "generic";

    /// <summary>True when the head registered the Android implementations.</summary>
    public static bool IsAndroid => string.Equals(PlatformName, "android", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True while the app has a resumed (visible) activity. The shared layer uses it to decide whether an
    /// approval/question/turn-finished event should raise a system notification (background only).
    /// Android sets it from <c>MainActivity.OnResume</c>/<c>OnPause</c>; other platforms leave the default.
    /// </summary>
    public static bool AppForeground { get; set; }

    /// <summary>
    /// System bars + display-cutout insets in device-independent pixels, set by the Android activity from
    /// <c>WindowInsetsCompat</c>. The shared shell applies it as page padding on edge-to-edge devices.
    /// </summary>
    public static Thickness SafeAreaInsets { get; set; }

    /// <summary>Alias of <see cref="SafeAreaInsets"/> (some call sites reflect for this name).</summary>
    public static Thickness SafeAreaPadding
    {
        get => SafeAreaInsets;
        set => SafeAreaInsets = value;
    }

    /// <summary>
    /// On-screen keyboard height in device-independent pixels (0 when the IME is hidden). Updated by the
    /// Android activity whenever the layout changes; the shared composer adds it as bottom padding.
    /// </summary>
    public static double ImeHeight { get; set; }

    /// <summary>
    /// Back navigation hook. Return <see langword="true"/> to consume the back gesture (e.g. leave the chat
    /// page back to the session list), <see langword="false"/> to let the activity default apply.
    /// </summary>
    public static Func<bool>? OnBackPressed { get; set; }

    /// <summary>Alias of <see cref="OnBackPressed"/> (kept for heads/call sites using the handler name).</summary>
    public static Func<bool>? BackHandler
    {
        get => OnBackPressed;
        set => OnBackPressed = value;
    }

    /// <summary>
    /// Platform image picker used by the composer's attach button. Android registers a photo-picker launcher;
    /// desktop heads may leave it null and the shared UI falls back to Avalonia's storage provider.
    /// Returns null when the user cancels.
    /// </summary>
    public static Func<CancellationToken, Task<DshRemote.Core.DshImageAttachment?>>? ImagePicker { get; set; }

    /// <summary>Invokes the platform image picker; null when this platform has none.</summary>
    public static Task<DshRemote.Core.DshImageAttachment?> PickImageAsync(CancellationToken cancellationToken = default)
        => ImagePicker is { } picker
            ? picker(cancellationToken)
            : Task.FromResult<DshRemote.Core.DshImageAttachment?>(null);

    /// <summary>
    /// Platform QR scanner used by the desktop-management page. Returns the decoded text (usually a
    /// <c>dshremote://pair?...</c> URI) or null when the user cancelled / decoding failed.
    /// </summary>
    public static Func<CancellationToken, Task<string?>>? QrScanner { get; set; }

    /// <summary>Invokes the platform QR scanner; null when this platform has none.</summary>
    public static Task<string?> ScanQrAsync(CancellationToken cancellationToken = default)
        => QrScanner is { } scanner
            ? scanner(cancellationToken)
            : Task.FromResult<string?>(null);

    // ---- Long screenshots (session page "长图") -----------------------------------------------------

    /// <summary>
    /// Persists long-screenshot PNGs to a user-visible place. Android registers a MediaStore writer
    /// (<c>Pictures/DSHRemote</c>, also copied to the app-external dir for adb); desktop heads leave it
    /// null and <see cref="SaveImagesAsync"/> falls back to the user's Pictures folder.
    /// </summary>
    public static Func<IReadOnlyList<PlatformImagePage>, CancellationToken, Task<IReadOnlyList<PlatformSavedImage>>>? ImageSaver
    {
        get;
        set;
    }

    /// <summary>
    /// Opens the platform share sheet for already-saved long screenshots (Android: Sharesheet via
    /// FileProvider). Null means "no system share sheet" and the shared layer falls back to revealing the
    /// files in the desktop file manager.
    /// </summary>
    public static Func<IReadOnlyList<PlatformSavedImage>, CancellationToken, Task<bool>>? ImageSharer { get; set; }

    /// <summary>True when this platform registered a real gallery writer (Android).</summary>
    public static bool HasPlatformImageSaver => ImageSaver is not null;

    /// <summary>True when this platform registered a real share sheet (Android).</summary>
    public static bool HasPlatformImageSharer => ImageSharer is not null;

    /// <summary>
    /// Saves the pages through the platform writer, or through the shared Pictures fallback when no head
    /// registered one. Exceptions are surfaced to the caller so the chat page can show a readable error.
    /// </summary>
    public static async Task<IReadOnlyList<PlatformSavedImage>> SaveImagesAsync(
        IReadOnlyList<PlatformImagePage> pages,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
        {
            return Array.Empty<PlatformSavedImage>();
        }

        if (ImageSaver is { } saver)
        {
            return await saver(pages, cancellationToken).ConfigureAwait(true);
        }

        return await SaveImagesToPicturesFolderAsync(pages, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the platform share sheet, or reveals the files in Explorer when the head has none.
    /// Returns false only when there is nothing to share.
    /// </summary>
    public static async Task<bool> ShareImagesAsync(
        IReadOnlyList<PlatformSavedImage> images,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
        {
            return false;
        }

        if (ImageSharer is { } sharer)
        {
            return await sharer(images, cancellationToken).ConfigureAwait(true);
        }

        return RevealInFileManager(images);
    }

    /// <summary>
    /// Shared fallback (desktop shells): writes the PNGs under &lt;Pictures&gt;\DSHRemote. Kept out of the
    /// dialog-free save path so the desktop shell behaves like the phone (files appear in the gallery).
    /// </summary>
    private static async Task<IReadOnlyList<PlatformSavedImage>> SaveImagesToPicturesFolderAsync(
        IReadOnlyList<PlatformImagePage> pages,
        CancellationToken cancellationToken)
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Path.Combine(AppHost.StateDirectory, "pictures");
        }

        var directory = Path.Combine(pictures, "DSHRemote");
        return await Task.Run(
            () =>
            {
                Directory.CreateDirectory(directory);
                var saved = new List<PlatformSavedImage>(pages.Count);
                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(directory, page.FileName);
                    File.WriteAllBytes(path, page.PngBytes);
                    saved.Add(new PlatformSavedImage(page.FileName, path, path, page.PixelWidth, page.PixelHeight));
                }

                return (IReadOnlyList<PlatformSavedImage>)saved;
            },
            cancellationToken).ConfigureAwait(true);
    }

    private static bool RevealInFileManager(IReadOnlyList<PlatformSavedImage> images)
    {
        var path = images.FirstOrDefault(image => !string.IsNullOrWhiteSpace(image.LocalPath))?.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Platform] reveal long screenshot failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Raised after <see cref="SafeAreaInsets"/> or <see cref="ImeHeight"/> changed (on the Android UI
    /// thread). Heads without insets never raise it.
    /// </summary>
    public static event Action? LayoutInsetsChanged;

    /// <summary>Updates both inset values and notifies listeners (called by the Android activity).</summary>
    public static void SetLayoutInsets(Thickness safeAreaInsets, double imeHeight)
    {
        var ime = double.IsFinite(imeHeight) && imeHeight > 0 ? imeHeight : 0;
        var changed = !SafeAreaInsets.Equals(safeAreaInsets) || Math.Abs(ImeHeight - ime) > 0.5;
        SafeAreaInsets = safeAreaInsets;
        ImeHeight = ime;
        if (!changed)
        {
            return;
        }

        try
        {
            LayoutInsetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Platform] LayoutInsetsChanged handler threw: {ex}");
        }
    }

    /// <summary>
    /// Asks the shared layer to handle a back gesture. Returns true when consumed; when no handler is wired
    /// the head keeps its default back behavior.
    /// </summary>
    public static bool HandleBack()
    {
        var handler = OnBackPressed;
        if (handler is null)
        {
            return false;
        }

        try
        {
            return handler();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Platform] BackHandler threw: {ex}");
            return false;
        }
    }

    /// <summary>Secret storage (Android: Keystore-backed). Null means use the shared file-store fallback.</summary>
    public static ISecretStore? SecretStore { get; set; }

    /// <summary>
    /// Called with a deep-link URI (e.g. <c>dshremote://pair?v=1&amp;host=...&amp;token=...</c>) and its
    /// source (<c>activity-create</c> / <c>new-intent</c>). Assigning a handler replays URIs that arrived
    /// before wiring (cold start ordering); keep the handler cheap and marshal to the UI thread yourself.
    /// </summary>
    public static Action<string, string>? OnDeepLink
    {
        get
        {
            lock (Sync)
            {
                return _onDeepLink;
            }
        }

        set
        {
            List<string> replay;
            lock (Sync)
            {
                _onDeepLink = value;
                replay = new List<string>(PendingDeepLinks);
                PendingDeepLinks.Clear();
            }

            if (value is null)
            {
                return;
            }

            foreach (var uri in replay)
            {
                InvokeDeepLink(value, uri, "queued-at-startup");
            }
        }
    }

    /// <summary>
    /// Queues (or immediately delivers) a deep link. Android calls this for the launch intent and for
    /// <c>OnNewIntent</c>; a queue lets a cold start survive until Avalonia has created the view model.
    /// </summary>
    public static void DispatchDeepLink(string? uri, string source = "android")
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        Action<string, string>? handler;
        lock (Sync)
        {
            handler = _onDeepLink;
            if (handler is null)
            {
                PendingDeepLinks.Add(uri!);
            }
        }

        if (handler is not null)
        {
            InvokeDeepLink(handler, uri!, source);
        }
        else
        {
            Debug.WriteLine($"[Platform] deep link queued until handler attaches ({source}): {Redact(uri!)}");
        }
    }

    /// <summary>Deep links received before <see cref="OnDeepLink"/> was assigned (oldest first).</summary>
    public static IReadOnlyList<string> DrainPendingDeepLinks()
    {
        lock (Sync)
        {
            var copy = new List<string>(PendingDeepLinks);
            PendingDeepLinks.Clear();
            return copy;
        }
    }

    /// <summary>
    /// Called with a session id when the user taps an approval/question/turn-finished notification.
    /// Assigning a handler replays taps that arrived before wiring.
    /// </summary>
    public static Action<string>? OnSessionOpenRequested
    {
        get
        {
            lock (Sync)
            {
                return _onSessionOpenRequested;
            }
        }

        set
        {
            List<string> replay;
            lock (Sync)
            {
                _onSessionOpenRequested = value;
                replay = new List<string>(PendingSessionOpens);
                PendingSessionOpens.Clear();
            }

            if (value is null)
            {
                return;
            }

            foreach (var sessionId in replay)
            {
                InvokeSessionOpen(value, sessionId);
            }
        }
    }

    /// <summary>Asks the shared layer to open a session (notification tap; no-op when unwired).</summary>
    public static void DispatchSessionOpen(string? sessionId, string source = "notification")
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        Action<string>? handler;
        lock (Sync)
        {
            handler = _onSessionOpenRequested;
            if (handler is null)
            {
                PendingSessionOpens.Add(sessionId!);
            }
        }

        if (handler is not null)
        {
            InvokeSessionOpen(handler, sessionId!);
        }
        else
        {
            Debug.WriteLine($"[Platform] session-open queued until handler attaches ({source}): {sessionId}");
        }
    }

    /// <summary>Starts the foreground ("keep the process alive") service. Null when unsupported.</summary>
    public static Action? StartForegroundConnection { get; set; }

    /// <summary>Stops the foreground service. Null when unsupported.</summary>
    public static Action? StopForegroundConnection { get; set; }

    /// <summary>True when this platform can run the connection under a foreground service.</summary>
    public static bool IsForegroundConnectionSupported => StartForegroundConnection is not null;

    /// <summary>
    /// Last state pushed by the shared layer via <see cref="SetConnectionActive"/>. Used by the Android
    /// activity so backing out of the UI does not tear down a healthy connection.
    /// </summary>
    public static bool ConnectionActive { get; private set; }

    /// <summary>
    /// Convenience used by the shared view model when the connection state changes: starts or stops the
    /// foreground service. Never throws; returns false when the platform has no implementation.
    /// </summary>
    public static bool SetConnectionActive(bool active)
    {
        ConnectionActive = active;
        var action = active ? StartForegroundConnection : StopForegroundConnection;
        if (action is null)
        {
            return false;
        }

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Platform] foreground connection {(active ? "start" : "stop")} failed: {ex}");
            return false;
        }
    }

    /// <summary>Masks <c>token</c> (and <c>fp</c>) values so deep links can be logged safely.</summary>
    public static string Redact(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return string.Empty;
        }

        try
        {
            var result = uri;
            foreach (var name in new[] { "token", "fp", "t" })
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    "(?<=[?&])" + name + "=[^&]*",
                    name + "=***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return result;
        }
        catch
        {
            return "(unparseable uri)";
        }
    }

    private static void InvokeDeepLink(Action<string, string> handler, string uri, string source)
    {
        try
        {
            handler(uri, source);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Platform] OnDeepLink handler threw: {ex}");
        }
    }

    private static void InvokeSessionOpen(Action<string> handler, string sessionId)
    {
        try
        {
            handler(sessionId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Platform] OnSessionOpenRequested handler threw: {ex}");
        }
    }
}
