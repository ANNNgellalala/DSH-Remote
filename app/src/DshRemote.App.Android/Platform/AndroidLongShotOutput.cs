using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;
using DshRemote.App.Platform;
using Environment = Android.OS.Environment;
using File = System.IO.File;
using Path = System.IO.Path;
using Stream = System.IO.Stream;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Android persistence and sharing for session long screenshots.
/// <para>
/// Saving uses the MediaStore (Android 10+ scoped storage): the PNGs land in
/// <c>Pictures/DSHRemote</c> without any dangerous storage permission, with the standard
/// <c>IS_PENDING</c> write protocol so no half-written file is ever visible to the gallery. A copy is
/// also written to the app-external files dir (<c>Android/data/&lt;id&gt;/files/longshots</c>) so
/// adb/E2E tooling can pull the exact bytes without content-provider gymnastics.
/// </para>
/// <para>
/// Sharing writes the pages to the app cache (covered by the existing FileProvider paths) and opens the
/// system Sharesheet with <c>ACTION_SEND_MULTIPLE</c>.
/// </para>
/// </summary>
public static class AndroidLongShotOutput
{
    private const string Tag = "DshRemoteLongShot";
    private const string MimeType = "image/png";
    private const string AlbumName = "DSHRemote";

    // MediaStore column names are stable Android API strings; using them directly avoids binding-name
    // differences between .NET Android versions.
    private const string ColumnDisplayName = "_display_name";
    private const string ColumnMimeType = "mime_type";
    private const string ColumnRelativePath = "relative_path";
    private const string ColumnIsPending = "is_pending";

    /// <summary>Folder (relative to the app's external files dir) holding the adb-pullable copies.</summary>
    public const string ExternalCopyFolder = "longshots";

    /// <summary>MediaStore saver registered into <see cref="PlatformServices.ImageSaver"/>.</summary>
    public static async Task<IReadOnlyList<PlatformSavedImage>> SaveAsync(
        IReadOnlyList<PlatformImagePage> pages,
        CancellationToken cancellationToken)
    {
        var context = AndroidPlatformHost.AppContext
            ?? throw new InvalidOperationException("Android 上下文尚未初始化，无法保存长图。");

        return await Task.Run(() =>
        {
            var saved = new List<PlatformSavedImage>(pages.Count);
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                saved.Add(SaveOne(context, page));
            }

            return (IReadOnlyList<PlatformSavedImage>)saved;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static PlatformSavedImage SaveOne(Context context, PlatformImagePage page)
    {
        var localPath = WriteExternalCopy(context, page);
        var location = "相册 Pictures/" + AlbumName + "/" + page.FileName;

        try
        {
            SaveToMediaStore(context, page);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "MediaStore save failed for " + page.FileName + ": " + ex.Message);
            // Scoped storage is unavailable (very old device / restricted OEM): keep the app-external copy
            // and tell the user exactly where it is instead of failing the whole capture.
            if (localPath is not null)
            {
                location = "应用文件目录/" + ExternalCopyFolder + "/" + page.FileName;
            }
            else
            {
                throw new InvalidOperationException("保存长图到相册失败：" + ex.Message, ex);
            }
        }

        return new PlatformSavedImage(page.FileName, location, localPath, page.PixelWidth, page.PixelHeight);
    }

    private static void SaveToMediaStore(Context context, PlatformImagePage page)
    {
        var resolver = context.ContentResolver
            ?? throw new InvalidOperationException("ContentResolver 不可用。");

        var collection = MediaStore.Images.Media.ExternalContentUri
            ?? throw new InvalidOperationException("MediaStore 不可用。");

        using var values = new ContentValues();
        values.Put(ColumnDisplayName, page.FileName);
        values.Put(ColumnMimeType, MimeType);
        values.Put(ColumnRelativePath, Environment.DirectoryPictures + "/" + AlbumName);

        var scopedStorage = Build.VERSION.SdkInt >= BuildVersionCodes.Q;
        if (scopedStorage)
        {
            values.Put(ColumnIsPending, 1);
        }

        var uri = resolver.Insert(collection, values)
            ?? throw new InvalidOperationException("系统相册拒绝创建文件。");

        try
        {
            using (var output = resolver.OpenOutputStream(uri)
                ?? throw new InvalidOperationException("无法打开相册文件写入流。"))
            {
                output.Write(page.PngBytes, 0, page.PngBytes.Length);
                output.Flush();
            }

            if (scopedStorage)
            {
                using var publish = new ContentValues();
                publish.Put(ColumnIsPending, 0);
                resolver.Update(uri, publish, null, null);
            }
        }
        catch
        {
            try
            {
                resolver.Delete(uri, null, null);
            }
            catch
            {
                // Best effort cleanup of the half-written row.
            }

            throw;
        }
    }

    private static string? WriteExternalCopy(Context context, PlatformImagePage page)
    {
        try
        {
            var root = context.GetExternalFilesDir(null);
            if (root is null)
            {
                return null;
            }

            var directory = Path.Combine(root.AbsolutePath, ExternalCopyFolder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, page.FileName);
            File.WriteAllBytes(path, page.PngBytes);
            return path;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "external copy failed for " + page.FileName + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>System Sharesheet for the saved pages (FileProvider URIs from the app cache).</summary>
    public static async Task<bool> ShareAsync(
        IReadOnlyList<PlatformSavedImage> images,
        CancellationToken cancellationToken)
    {
        var context = AndroidPlatformHost.AppContext;
        if (context is null || images.Count == 0)
        {
            return false;
        }

        var uris = new List<IParcelable>(images.Count);
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheRoot = context.CacheDir?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                continue;
            }

            var directory = Path.Combine(cacheRoot, ExternalCopyFolder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, image.FileName);

            var source = image.LocalPath;
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
            {
                File.Copy(source, path, overwrite: true);
            }
            else
            {
                continue;
            }

            var authority = context.PackageName + ".fileprovider";
            var uri = FileProvider.GetUriForFile(context, authority, new Java.IO.File(path));
            uris.Add(uri!);
        }

        if (uris.Count == 0)
        {
            return false;
        }

        var intent = new Intent(uris.Count == 1 ? Intent.ActionSend : Intent.ActionSendMultiple);
        intent.SetType(MimeType);
        intent.PutParcelableArrayListExtra(Intent.ExtraStream, uris);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        var chooser = Intent.CreateChooser(intent, "分享长图") ?? intent;
        chooser.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(chooser);
        return await Task.FromResult(true).ConfigureAwait(false);
    }
}
