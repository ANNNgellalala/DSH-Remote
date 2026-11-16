using Android.Graphics;
using Android.Media;
using DshRemote.Core;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Normalises a picked image for the chat composer: bounds the longest edge to 1600 px and the encoded size to
/// ~2 MB while preserving transparency (PNG) or transcoding opaque photos to JPEG q85. Sampling and a retry
/// loop keep a large camera photo from exhausting the process heap on low-end phones.
/// </summary>
internal static class ImageCompressor
{
    public const int MaxDimension = 1600;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int JpegQuality = 85;

    private const string Tag = "DshRemoteImage";

    /// <summary>
    /// Returns the attachment ready for upload. Returns <see langword="null"/> when the bytes are not a
    /// decodable image. Bytes are passed through untouched when they already satisfy both limits.
    /// </summary>
    public static DshImageAttachment? Process(byte[]? raw, string? mime, string? name)
    {
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        var (sourceWidth, sourceHeight) = ReadBounds(raw);
        var mediaType = NormalizeMime(mime, name);

        if (sourceWidth > 0 && sourceHeight > 0 &&
            raw.Length <= MaxBytes &&
            Math.Max(sourceWidth, sourceHeight) <= MaxDimension)
        {
            global::Android.Util.Log.Info(
                Tag,
                $"[ImagePicker] passthrough bytes={raw.Length} dims={sourceWidth}x{sourceHeight} mime={mediaType}");
            return new DshImageAttachment(raw, mediaType, name);
        }

        Bitmap? bitmap = null;
        try
        {
            bitmap = Decode(raw, sourceWidth, sourceHeight);
            if (bitmap is null)
            {
                global::Android.Util.Log.Warn(Tag, "[ImagePicker] decode returned null");
                return null;
            }

            bitmap = ApplyExifRotation(bitmap, raw);
            bitmap = ScaleToFit(bitmap, MaxDimension);
            var hasAlpha = bitmap.HasAlpha;
            var encoded = CompressWithCap(bitmap, hasAlpha);
            if (encoded is null || encoded.Length == 0)
            {
                return null;
            }

            var outputMime = hasAlpha ? "image/png" : "image/jpeg";
            global::Android.Util.Log.Info(
                Tag,
                $"[ImagePicker] compressed {sourceWidth}x{sourceHeight}/{raw.Length}B -> " +
                $"{bitmap.Width}x{bitmap.Height}/{encoded.Length}B mime={outputMime}");
            return new DshImageAttachment(encoded, outputMime, name);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[ImagePicker] compression failed: " + ex.Message);
            return null;
        }
        finally
        {
            if (bitmap is not null && !bitmap.IsRecycled)
            {
                bitmap.Recycle();
            }
        }
    }

    /// <summary>Decoded pixel size, or (0,0) when the bytes are not an image (used by self-tests/logging).</summary>
    public static (int Width, int Height) ReadBounds(byte[] raw)
    {
        try
        {
            var options = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(raw, 0, raw.Length, options);
            return (options.OutWidth, options.OutHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static Bitmap? Decode(byte[] raw, int width, int height)
    {
        var max = Math.Max(width, height);
        var sample = 1;
        // Decode at no more than 2x the target so the scaled result stays crisp but memory stays bounded.
        while (max > 0 && max / sample > MaxDimension * 2)
        {
            sample *= 2;
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var options = new BitmapFactory.Options
                {
                    InSampleSize = sample,
                    InPreferredConfig = Bitmap.Config.Argb8888
                };
                var bitmap = BitmapFactory.DecodeByteArray(raw, 0, raw.Length, options);
                if (bitmap is not null)
                {
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, $"[ImagePicker] decode attempt {attempt} failed: {ex.Message}");
            }

            sample *= 2;
        }

        return null;
    }

    private static Bitmap ScaleToFit(Bitmap source, int maxDimension)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxDimension || longest <= 0)
        {
            return source;
        }

        var scale = (double)maxDimension / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var scaled = Bitmap.CreateScaledBitmap(source, width, height, true);
        if (scaled is null || ReferenceEquals(scaled, source))
        {
            return source;
        }

        source.Recycle();
        return scaled;
    }

    private static Bitmap ApplyExifRotation(Bitmap source, byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw);
            using var exif = new ExifInterface(stream);
            var orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            var degrees = orientation switch
            {
                3 => 180,
                6 => 90,
                8 => 270,
                _ => 0
            };

            if (degrees == 0)
            {
                return source;
            }

            using var matrix = new Matrix();
            matrix.PostRotate(degrees);
            var rotated = Bitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true);
            if (rotated is null || ReferenceEquals(rotated, source))
            {
                return source;
            }

            source.Recycle();
            return rotated;
        }
        catch
        {
            // EXIF is best effort: a missing/odd tag must never fail the attachment.
            return source;
        }
    }

    private static byte[]? CompressWithCap(Bitmap source, bool hasAlpha)
    {
        var format = hasAlpha ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!;
        var qualities = hasAlpha ? new[] { 100 } : new[] { JpegQuality, 75, 65 };
        foreach (var quality in qualities)
        {
            var bytes = Compress(source, format, quality);
            if (bytes is not null && bytes.Length <= MaxBytes)
            {
                return bytes;
            }
        }

        // Still too large (noisy PNG or a very detailed photo): shrink step by step and retry.
        var current = source;
        var ownsCurrent = false;
        try
        {
            var dimension = Math.Max(current.Width, current.Height);
            for (var i = 0; i < 8 && dimension > 240; i++)
            {
                dimension = (int)Math.Max(240, Math.Round(dimension * 0.8));
                var next = ScaleToFit(current, dimension);
                if (ReferenceEquals(next, current))
                {
                    break;
                }

                if (ownsCurrent)
                {
                    current.Recycle();
                }

                current = next;
                ownsCurrent = true;
                var bytes = Compress(current, format, hasAlpha ? 100 : JpegQuality);
                if (bytes is not null && bytes.Length <= MaxBytes)
                {
                    return bytes;
                }
            }

            return Compress(current, format, hasAlpha ? 100 : 60);
        }
        finally
        {
            if (ownsCurrent && !current.IsRecycled)
            {
                current.Recycle();
            }
        }
    }

    private static byte[]? Compress(Bitmap bitmap, Bitmap.CompressFormat format, int quality)
    {
        try
        {
            using var stream = new MemoryStream();
            return bitmap.Compress(format, quality, stream) ? stream.ToArray() : null;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[ImagePicker] encode failed: " + ex.Message);
            return null;
        }
    }

    public static string NormalizeMime(string? mime, string? name)
    {
        if (!string.IsNullOrWhiteSpace(mime) && mime!.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return mime!;
        }

        return (System.IO.Path.GetExtension(name ?? string.Empty).ToLowerInvariant()) switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".heic" or ".heif" => "image/heic",
            _ => "image/jpeg"
        };
    }
}

