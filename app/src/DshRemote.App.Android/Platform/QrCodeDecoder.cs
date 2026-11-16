using Android.Graphics;
using ZXing;
using ZXing.Common;

namespace DshRemote.App.Android.Platform;

/// <summary>
/// Pure-managed QR decoding: Android <see cref="BitmapFactory"/> produces ARGB pixels which are wrapped in a
/// ZXing luminance source. ZXing.Net is fully managed, so no CameraX/ML Kit native bindings are involved.
/// </summary>
internal static class QrCodeDecoder
{
    private const string Tag = "DshRemoteQr";

    /// <summary>Upper bound for the decoded bitmap edge; QR codes stay readable and heap usage stays small.</summary>
    public const int MaxDecodeDimension = 2048;

    /// <summary>Decodes a QR code from encoded image bytes (PNG/JPEG/...). Returns null when not found.</summary>
    public static string? DecodeImageBytes(byte[]? imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return null;
        }

        Bitmap? bitmap = null;
        try
        {
            var (boundWidth, boundHeight) = ReadBounds(imageBytes);
            bitmap = DecodeBitmap(imageBytes, boundWidth, boundHeight);
            if (bitmap is null)
            {
                global::Android.Util.Log.Warn(Tag, "[QrScan] bitmap decode failed");
                return null;
            }

            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var pixels = new int[width * height];
            bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);

            var text = DecodePixels(pixels, width, height);
            if (text is null)
            {
                global::Android.Util.Log.Info(
                    Tag,
                    $"[QrScan] no QR code found (dims={width}x{height} bytes={imageBytes.Length})");
            }

            return text;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] decode failed: " + ex.Message);
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

    /// <summary>
    /// Decodes a QR code from a grayscale luminance plane (camera YUV_420_888 Y plane). Used by the in-app
    /// camera scanner for continuous frame decoding without converting every frame to a Bitmap.
    /// </summary>
    public static string? DecodeLuminance(byte[]? luminance, int width, int height)
    {
        if (luminance is null || width <= 0 || height <= 0 || luminance.Length < width * height)
        {
            return null;
        }

        try
        {
            var reader = CreateReader(thorough: false);
            var source = new PlanarYUVLuminanceSource(
                luminance,
                width,
                height,
                0,
                0,
                width,
                height,
                false);
            var result = reader.Decode(source);
            return string.IsNullOrEmpty(result?.Text) ? null : result!.Text;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] luminance decode failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Decodes ARGB pixels (row-major, stride = width) as produced by <c>Bitmap.GetPixels</c>.</summary>
    public static string? DecodePixels(int[] pixels, int width, int height)
    {
        if (pixels is null || pixels.Length < width * height || width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Fast path first (no rotation attempts, no inverted scan): clean screenshots/camera photos decode
            // in one pass; only fall back to the thorough scan when that fails.
            var text = DecodeWithPixels(CreateReader(thorough: false), pixels, width, height);
            if (!string.IsNullOrEmpty(text))
            {
                global::Android.Util.Log.Info(
                    Tag,
                    $"[QrScan] decoded in fast pass ({stopwatch.ElapsedMilliseconds}ms, {width}x{height})");
                return text;
            }

            var thorough = CreateReader(thorough: true);
            text = DecodeWithPixels(thorough, pixels, width, height);
            if (!string.IsNullOrEmpty(text))
            {
                global::Android.Util.Log.Info(
                    Tag,
                    $"[QrScan] decoded in thorough pass ({stopwatch.ElapsedMilliseconds}ms, {width}x{height})");
                return text;
            }

            // Rotation insurance: try the 180-degree flip as well (cheap, and covers EXIF-less exotic captures).
            var flipped = new int[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                flipped[i] = pixels[pixels.Length - 1 - i];
            }

            text = DecodeWithPixels(thorough, flipped, width, height);
            global::Android.Util.Log.Info(
                Tag,
                $"[QrScan] decode finished ({stopwatch.ElapsedMilliseconds}ms, {width}x{height}, found={(text is not null)})");
            return text;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "[QrScan] zxing decode failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Self-check for the camera luminance path: decodes the image, converts it to a grayscale Y plane and
    /// decodes that with <see cref="DecodeLuminance"/> (the exact path used for Camera2 frames). Used by the
    /// scanner diagnostic to prove YUV-style decoding works with a real QR fixture.
    /// </summary>
    public static bool VerifyLuminancePath(byte[]? imageBytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return false;
        }

        Bitmap? bitmap = null;
        try
        {
            var (boundWidth, boundHeight) = ReadBounds(imageBytes);
            bitmap = DecodeBitmap(imageBytes, boundWidth, boundHeight);
            if (bitmap is null)
            {
                return false;
            }

            width = bitmap.Width;
            height = bitmap.Height;
            var pixels = new int[width * height];
            bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
            var gray = new byte[width * height];
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                var alpha = (pixel >> 24) & 0xFF;
                var red = (pixel >> 16) & 0xFF;
                var green = (pixel >> 8) & 0xFF;
                var blue = pixel & 0xFF;
                if (alpha < 0xFF)
                {
                    // Composite semi-transparent pixels over white, matching how a camera sees a screen/print.
                    red = (red * alpha + 255 * (255 - alpha)) / 255;
                    green = (green * alpha + 255 * (255 - alpha)) / 255;
                    blue = (blue * alpha + 255 * (255 - alpha)) / 255;
                }

                gray[i] = (byte)((red * 299 + green * 587 + blue * 114) / 1000);
            }

            return !string.IsNullOrEmpty(DecodeLuminance(gray, width, height));
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bitmap is not null && !bitmap.IsRecycled)
            {
                bitmap.Recycle();
            }
        }
    }

    private static BarcodeReaderGeneric CreateReader(bool thorough) => new()
    {
        AutoRotate = thorough,
        Options = new DecodingOptions
        {
            TryHarder = thorough,
            TryInverted = thorough,
            PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            CharacterSet = "UTF-8"
        }
    };

    private static string? DecodeWithPixels(BarcodeReaderGeneric reader, int[] pixels, int width, int height)
    {
        // ZXing.Net's portable luminance source takes raw ARGB32 bytes; Bitmap.GetPixels gives us ARGB ints.
        var raw = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            var offset = i * 4;
            raw[offset] = (byte)(pixel >> 24);
            raw[offset + 1] = (byte)(pixel >> 16);
            raw[offset + 2] = (byte)(pixel >> 8);
            raw[offset + 3] = (byte)pixel;
        }

        var result = reader.Decode(raw, width, height, RGBLuminanceSource.BitmapFormat.ARGB32);
        return string.IsNullOrEmpty(result?.Text) ? null : result!.Text;
    }

    private static (int Width, int Height) ReadBounds(byte[] imageBytes)
    {
        try
        {
            var options = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, options);
            return (options.OutWidth, options.OutHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static Bitmap? DecodeBitmap(byte[] imageBytes, int width, int height)
    {
        var max = Math.Max(width, height);
        var sample = 1;
        while (max > 0 && max / sample > MaxDecodeDimension)
        {
            sample *= 2;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var options = new BitmapFactory.Options
                {
                    InSampleSize = sample,
                    InPreferredConfig = Bitmap.Config.Argb8888
                };
                var bitmap = BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length, options);
                if (bitmap is not null)
                {
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, $"[QrScan] bitmap attempt {attempt} failed: {ex.Message}");
            }

            sample *= 2;
        }

        return null;
    }
}

