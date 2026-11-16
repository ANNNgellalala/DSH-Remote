using Android.Content;
using DshRemote.App.Platform;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Android implementation of <see cref="PlatformServices.QrScanner"/>: the activity launches the system camera
/// or gallery picker, then the resulting bitmap is decoded locally with ZXing.Net. The debug file entry point
/// (<see cref="DebugImagePath"/>) lets emulator E2E scripts decode a fixture without opening any UI.
/// </summary>
public static class AndroidQrScanner
{
    /// <summary>adb diagnostics: <c>--es dshremote.qrImagePath /sdcard/.../qr.png</c> decodes that file.</summary>
    public const string DebugImagePathExtra = "dshremote.qrImagePath";

    /// <summary>adb diagnostics: optional expected text; the self-test logs <c>match=True/False</c>.</summary>
    public const string DebugExpectExtra = "dshremote.qrExpect";

    /// <summary>adb diagnostics: open the real system chooser (camera + gallery) instead of a file.</summary>
    public const string ScanUiExtra = "dshremote.qrScanUi";

    private const string Tag = "DshRemoteQr";

    private static readonly object Sync = new();
    private static WeakReference<MainActivity>? _activity;
    private static string? _debugImagePath;

    /// <summary>Debug-only image path that bypasses the camera/gallery chooser (emulator automation).</summary>
    public static string? DebugImagePath
    {
        get
        {
            lock (Sync)
            {
                return _debugImagePath;
            }
        }

        set
        {
            lock (Sync)
            {
                _debugImagePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            global::Android.Util.Log.Info(
                Tag,
                $"[QrScan] debug image path {(value is null ? "cleared" : "set: " + value)}");
        }
    }

    /// <summary>Remembers the current activity so the shared layer can start the scanner without a hard ref.</summary>
    public static void Attach(MainActivity activity)
    {
        lock (Sync)
        {
            _activity = new WeakReference<MainActivity>(activity);
        }
    }

    /// <summary>Entry point registered into <see cref="PlatformServices.QrScanner"/>.</summary>
    public static Task<string?> ScanAsync(CancellationToken cancellationToken)
    {
        var debugPath = DebugImagePath;
        if (!string.IsNullOrWhiteSpace(debugPath))
        {
            return Task.Run(() => DecodeFile(debugPath!), cancellationToken);
        }

        MainActivity? activity = null;
        lock (Sync)
        {
            _activity?.TryGetTarget(out activity);
        }

        if (activity is null)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] no activity attached; scanner unavailable");
            return Task.FromResult<string?>(null);
        }

        return activity.ScanQrFromSystemAsync(cancellationToken);
    }

    /// <summary>Decodes an image file (used by the debug entry point).</summary>
    internal static string? DecodeFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                global::Android.Util.Log.Warn(Tag, $"[QrScan] debug image does not exist: {path}");
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            global::Android.Util.Log.Info(Tag, $"[QrScan] debug image read bytes={bytes.Length} path={path}");
            var text = QrCodeDecoder.DecodeImageBytes(bytes);
            global::Android.Util.Log.Info(Tag, $"[QrScan] debug decode {Describe(text)}");
            return text;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] debug decode failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Reads a content URI (gallery pick / camera output) and decodes the QR code.</summary>
    internal static async Task<string?> DecodeUriAsync(
        Context context,
        global::Android.Net.Uri uri,
        CancellationToken cancellationToken)
    {
        var resolver = context.ContentResolver
                       ?? throw new InvalidOperationException("ContentResolver unavailable");
        using var stream = resolver.OpenInputStream(uri)
                           ?? throw new IOException("cannot open image stream for " + uri);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        global::Android.Util.Log.Info(Tag, $"[QrScan] picked image bytes={bytes.Length}");
        var text = await Task.Run(() => QrCodeDecoder.DecodeImageBytes(bytes), cancellationToken)
            .ConfigureAwait(false);
        global::Android.Util.Log.Info(Tag, $"[QrScan] picked decode {Describe(text)}");
        return text;
    }

    /// <summary>
    /// Log-safe description of a decoded payload: only the length, a redacted prefix and whether CJK text was
    /// decoded (a v2 pairing URI carries a human desktop name). Never logs the token/fingerprint values.
    /// </summary>
    public static string Describe(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "text=none";
        }

        var prefixEnd = text.IndexOf('&');
        var prefix = prefixEnd > 0 ? text.Substring(0, prefixEnd) : text;
        if (prefix.Length > 32)
        {
            prefix = prefix.Substring(0, 32);
        }

        var redacted = PlatformServices.Redact(prefix);
        if (redacted.Length > 32)
        {
            redacted = redacted.Substring(0, 32);
        }

        return $"text=ok length={text.Length} prefix={redacted} cjk={ContainsCjk(text)}";
    }

    public static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text!)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF')
            {
                return true;
            }
        }

        return false;
    }
}
