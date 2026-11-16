using Android.Content;
using Android.Provider;
using DshRemote.App.Platform;
using DshRemote.Core;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Android implementation of <see cref="PlatformServices.ImagePicker"/>. The activity owns the
/// <c>ActivityResultLauncher</c> (photo picker / GetContent); this class keeps the weak activity reference, the
/// debug-file entry point used by the emulator E2E scripts, and the ContentResolver/file readers.
/// </summary>
public static class AndroidImagePicker
{
    /// <summary>adb diagnostics: <c>--es dshremote.imagePath /sdcard/.../image.png</c> bypasses the picker UI.</summary>
    public const string DebugImagePathExtra = "dshremote.imagePath";

    private const string Tag = "DshRemoteImage";

    private static readonly object Sync = new();
    private static WeakReference<MainActivity>? _activity;
    private static string? _debugImagePath;

    /// <summary>
    /// Debug-only file path that bypasses the system picker when set (emulator/mac automation). When the file
    /// is unreadable the picker falls back to the normal system flow instead of failing.
    /// </summary>
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

            global::Android.Util.Log.Info(Tag, $"[ImagePicker] debug path {(value is null ? "cleared" : "set: " + value)}");
        }
    }

    /// <summary>Remembers the current activity so the shared layer can pick without holding a hard reference.</summary>
    public static void Attach(MainActivity activity)
    {
        lock (Sync)
        {
            _activity = new WeakReference<MainActivity>(activity);
        }
    }

    /// <summary>Entry point registered into <see cref="PlatformServices.ImagePicker"/>.</summary>
    public static Task<DshImageAttachment?> PickAsync(CancellationToken cancellationToken)
    {
        var debugPath = DebugImagePath;
        if (!string.IsNullOrWhiteSpace(debugPath))
        {
            return ReadFromFileAsync(debugPath!, cancellationToken);
        }

        MainActivity? activity = null;
        lock (Sync)
        {
            _activity?.TryGetTarget(out activity);
        }

        if (activity is null)
        {
            global::Android.Util.Log.Warn(Tag, "[ImagePicker] no activity attached; picker unavailable");
            return Task.FromResult<DshImageAttachment?>(null);
        }

        return activity.PickImageFromGalleryAsync(cancellationToken);
    }

    /// <summary>Reads a content URI returned by the photo picker / GetContent and normalises it.</summary>
    internal static async Task<DshImageAttachment?> ReadFromUriAsync(
        Context context,
        global::Android.Net.Uri uri,
        CancellationToken cancellationToken)
    {
        var resolver = context.ContentResolver
                       ?? throw new InvalidOperationException("ContentResolver unavailable");
        var mime = SafeGetType(resolver, uri);
        var name = QueryDisplayName(resolver, uri);

        using var stream = resolver.OpenInputStream(uri)
                           ?? throw new IOException("cannot open image stream for " + uri);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var raw = buffer.ToArray();
        global::Android.Util.Log.Info(
            Tag,
            $"[ImagePicker] uri read bytes={raw.Length} mime={mime ?? "-"} name={name ?? "-"}");
        return await Task.Run(() => ImageCompressor.Process(raw, mime, name), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a file path for the adb diagnostics hook. Tries the app-visible path first, then a MediaStore lookup
    /// (for /sdcard/Download pushes; needs media read permission on Android 13+), then gives up.
    /// </summary>
    internal static Task<DshImageAttachment?> ReadFromFileAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var mime = ImageCompressor.NormalizeMime(null, name);
                var raw = TryReadFile(path) ?? TryReadViaMediaStore(path);
                if (raw is null || raw.Length == 0)
                {
                    global::Android.Util.Log.Warn(
                        Tag,
                        $"[ImagePicker] debug path unreadable (scoped storage?): {path}");
                    return (DshImageAttachment?)null;
                }

                global::Android.Util.Log.Info(Tag, $"[ImagePicker] debug file read bytes={raw.Length} path={path}");
                return ImageCompressor.Process(raw, mime, name);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "[ImagePicker] debug read failed: " + ex.Message);
                return null;
            }
        }, cancellationToken);

    private static byte[]? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                global::Android.Util.Log.Warn(Tag, $"[ImagePicker] debug path does not exist: {path}");
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(
                Tag,
                $"[ImagePicker] direct file read failed ({ex.GetType().Name}: {ex.Message}); trying MediaStore");
            return null;
        }
    }

    private static byte[]? TryReadViaMediaStore(string path)
    {
        try
        {
            var context = AndroidPlatformHost.AppContext;
            var resolver = context?.ContentResolver;
            if (resolver is null)
            {
                return null;
            }

            var fileName = System.IO.Path.GetFileName(path);
            var collection = ImageCollectionUri();
            var selection = MediaStore.IMediaColumns.DisplayName + "=?";
            using var cursor = resolver.Query(
                collection,
                new[] { IBaseColumns.Id },
                selection,
                new[] { fileName },
                null);
            if (cursor is null || !cursor.MoveToFirst())
            {
                return null;
            }

            var id = cursor.GetLong(cursor.GetColumnIndexOrThrow(IBaseColumns.Id));
            var uri = global::Android.Content.ContentUris.WithAppendedId(collection, id);
            using var stream = resolver.OpenInputStream(uri);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"[ImagePicker] MediaStore lookup failed: {ex.Message}");
            return null;
        }
    }

    private static global::Android.Net.Uri ImageCollectionUri()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary)
                   ?? MediaStore.Images.Media.ExternalContentUri
                   ?? throw new InvalidOperationException("images collection unavailable");
        }

#pragma warning disable CS0618, CA1422 // ExternalContentUri is the only option before API 29
        return MediaStore.Images.Media.ExternalContentUri
               ?? throw new InvalidOperationException("images collection unavailable");
#pragma warning restore CS0618, CA1422
    }

    private static string? SafeGetType(ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            return resolver.GetType(uri);
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryDisplayName(ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(
                uri,
                new[] { IOpenableColumns.DisplayName },
                null,
                null,
                null);
            if (cursor is null || !cursor.MoveToFirst())
            {
                return null;
            }

            var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
            return index >= 0 ? cursor.GetString(index) : null;
        }
        catch
        {
            return null;
        }
    }
}

