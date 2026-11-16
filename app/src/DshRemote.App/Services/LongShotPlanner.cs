using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DshRemote.App.Services;

/// <summary>
/// Pixel guards for one long screenshot. All values are in device pixels of the final PNGs.
/// <para>
/// Defaults keep the whole capture around 24 M px and every single image around 8 M px (≈32 MB decoded
/// RGBA), with edges below a conservative Skia/GPU safe size — a captured session can never OOM or fail
/// to encode on a mid-range phone. Sessions over the total budget are downscaled first, but never below
/// <see cref="MinWidthPixels"/> so text stays readable; whatever still does not fit one image is split
/// into multiple PNGs.
/// </para>
/// </summary>
public sealed record LongShotLimits
{
    /// <summary>Defaults used by the app: 24 M px total, 8 M px / 12 000 px per image, at least 640 px wide.</summary>
    public static LongShotLimits Default { get; } = new();

    /// <summary>Upper bound on the pixels of the whole capture (24 M px ≈ 96 MB decoded RGBA in total).</summary>
    public int MaxTotalPixels { get; init; } = 24_000_000;

    /// <summary>Upper bound on the pixels of a single output PNG (8 M px ≈ 32 MB decoded RGBA).</summary>
    public int MaxPixelsPerImage { get; init; } = 8_000_000;

    /// <summary>Upper bound on either edge of a single output PNG (height is the edge that matters).</summary>
    public int MaxEdgePixels { get; init; } = 12_000;

    /// <summary>Widest downscale allowed: below this width text stops being readable, so the plan splits.</summary>
    public int MinWidthPixels { get; init; } = 640;

    /// <summary>
    /// Safety cap on the number of PNG files one capture may produce. A session so long that it cannot
    /// fit even at the minimum readable width still covers every pixel (no silent truncation) and reports
    /// <see cref="LongShotPlan.ExceededBudget"/>.
    /// </summary>
    public int MaxImages { get; init; } = 24;

    /// <summary>Clamps user/head overrides into a range where the renderer cannot hurt itself.</summary>
    public LongShotLimits Normalize() => new()
    {
        MaxTotalPixels = Math.Clamp(MaxTotalPixels, 1_000_000, 120_000_000),
        MaxPixelsPerImage = Math.Clamp(MaxPixelsPerImage, 250_000, 40_000_000),
        MaxEdgePixels = Math.Clamp(MaxEdgePixels, 1_000, 16_384),
        MinWidthPixels = Math.Clamp(MinWidthPixels, 200, 4_096),
        MaxImages = Math.Clamp(MaxImages, 1, 64)
    };
}

/// <summary>One vertical slice of the long screenshot: pixels [<see cref="OffsetY"/>, OffsetY + PixelHeight).</summary>
public sealed record LongShotSegment(int Index, int OffsetY, int PixelWidth, int PixelHeight)
{
    /// <summary>Human index (1-based) used in progress text and file names.</summary>
    public int Number => Index + 1;
}

/// <summary>Deterministic render/split plan for one long screenshot.</summary>
public sealed record LongShotPlan
{
    /// <summary>Output PNG width in pixels (after any downscale).</summary>
    public required int PixelWidth { get; init; }

    /// <summary>Total content height in output pixels; the segments always cover all of it.</summary>
    public required int TotalPixelHeight { get; init; }

    /// <summary>Rendering scale in output pixels per DIP (RenderTargetBitmap dpi = 96 * this).</summary>
    public required double RenderScale { get; init; }

    /// <summary>True when the requested scale had to be reduced to satisfy the pixel/edge budget.</summary>
    public required bool Downscaled { get; init; }

    /// <summary>Non-overlapping slices, top to bottom; concatenating them reproduces the whole content.</summary>
    public required IReadOnlyList<LongShotSegment> Segments { get; init; }

    /// <summary>True when even the minimum readable width needed more images than the safety cap.</summary>
    public bool ExceededBudget { get; init; }

    public int SegmentCount => Segments.Count;

    public long TotalPixels => (long)PixelWidth * TotalPixelHeight;
}

/// <summary>
/// Computes how a transcript of a given DIP size becomes one or more PNG files: which output width,
/// how many slices, and how tall each slice is. Pure arithmetic — intentionally independent of
/// Avalonia/Android so the rules are unit-testable (see <c>LongShotPlannerTests</c>).
/// </summary>
public static class LongShotPlanner
{
    /// <summary>Absolute floor for the render scale, to avoid absurdly small bitmaps on odd heads.</summary>
    private const double MinRenderScale = 0.25;

    private const double MaxRenderScale = 8.0;

    /// <summary>
    /// Builds the plan for content of <paramref name="contentWidthDip"/> × <paramref name="contentHeightDip"/>
    /// DIP rendered at <paramref name="requestedScale"/> pixels per DIP (the device render scaling).
    /// </summary>
    public static LongShotPlan Create(
        double contentWidthDip,
        double contentHeightDip,
        double requestedScale,
        LongShotLimits? limits = null)
    {
        if (!double.IsFinite(contentWidthDip) || contentWidthDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentWidthDip), contentWidthDip, "内容宽度必须是正数。");
        }

        if (!double.IsFinite(contentHeightDip) || contentHeightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentHeightDip), contentHeightDip, "内容高度必须是正数。");
        }

        if (!double.IsFinite(requestedScale) || requestedScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedScale), requestedScale, "渲染缩放必须是正数。");
        }

        var effective = (limits ?? LongShotLimits.Default).Normalize();
        var scale = Math.Clamp(requestedScale, MinRenderScale, MaxRenderScale);

        var naturalWidth = Math.Max(1, (int)Math.Round(contentWidthDip * scale, MidpointRounding.AwayFromZero));
        var naturalHeight = Math.Max(1, (int)Math.Round(contentHeightDip * scale, MidpointRounding.AwayFromZero));

        // Downscale so the *total* pixel count fits the budget (keep the aspect ratio), then again so no
        // single edge exceeds the safe size. Never upscale, never go below the readable width.
        var widthByTotalPixels = (int)Math.Floor(
            Math.Sqrt((double)effective.MaxTotalPixels * naturalWidth / naturalHeight));
        var width = Math.Min(naturalWidth, Math.Min(effective.MaxEdgePixels, Math.Max(1, widthByTotalPixels)));

        if (width < effective.MinWidthPixels && naturalWidth > width)
        {
            // Readability floor: accept a total above the budget rather than produce unreadable text.
            width = Math.Min(naturalWidth, effective.MinWidthPixels);
        }

        width = Math.Max(1, width);
        var height = width == naturalWidth
            ? naturalHeight
            : Math.Max(1, (int)Math.Round(naturalHeight * (double)width / naturalWidth, MidpointRounding.AwayFromZero));

        var segmentHeight = Math.Min(effective.MaxEdgePixels, effective.MaxPixelsPerImage / width);
        segmentHeight = Math.Min(segmentHeight, height);
        if (segmentHeight < 32)
        {
            throw new InvalidOperationException(
                $"长图尺寸上限过小（宽 {width} px 时每段高度 {segmentHeight} px），无法生成可读长图。");
        }

        var segmentCount = Math.Max(1, (int)(((long)height + segmentHeight - 1) / segmentHeight));
        var segments = new List<LongShotSegment>(segmentCount);
        var offset = 0;
        for (var index = 0; index < segmentCount; index++)
        {
            var remaining = height - offset;
            if (remaining <= 0)
            {
                break;
            }

            var slice = Math.Min(segmentHeight, remaining);
            segments.Add(new LongShotSegment(index, offset, width, slice));
            offset += slice;
        }

        if (segments.Count == 0 || offset != height)
        {
            throw new InvalidOperationException("无法为当前会话生成有效的长图分段计划。");
        }

        // One row of tolerance: the aspect-ratio rounding may overshoot the budget by a fraction of a row.
        var exceededTotal = (long)width * height > effective.MaxTotalPixels + width;
        var exceededCount = segments.Count > effective.MaxImages;

        return new LongShotPlan
        {
            PixelWidth = width,
            TotalPixelHeight = height,
            RenderScale = width / contentWidthDip,
            Downscaled = width < naturalWidth,
            Segments = segments,
            ExceededBudget = exceededTotal || exceededCount
        };
    }
}

/// <summary>
/// File names and titles for saved long screenshots. Pure logic (no IO) so the rules are testable.
/// </summary>
public static class LongShotNaming
{
    /// <summary>Fallback title when the session has no usable title.</summary>
    public const string DefaultTitle = "会话";

    private const int MaxTitleLength = 32;

    /// <summary>
    /// Sanitizes a session title for use in a file name: keeps CJK, letters and digits, replaces path
    /// separators / reserved characters / whitespace with a single dash and caps the length.
    /// </summary>
    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return DefaultTitle;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        var lastWasDash = false;
        foreach (var ch in title.Trim())
        {
            var replace = char.IsControl(ch)
                || char.IsWhiteSpace(ch)
                || Array.IndexOf(invalid, ch) >= 0
                || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
            if (replace)
            {
                if (!lastWasDash && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasDash = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasDash = false;
        }

        var cleaned = builder.ToString().Trim('-', '.', ' ');
        if (cleaned.Length == 0)
        {
            return DefaultTitle;
        }

        // Cut on a code-point boundary so we never split a surrogate pair.
        if (cleaned.Length > MaxTitleLength)
        {
            var cut = MaxTitleLength;
            if (char.IsHighSurrogate(cleaned[cut - 1]) && cut < cleaned.Length)
            {
                cut--;
            }

            cleaned = cleaned[..cut].TrimEnd('-', '.', ' ');
        }

        return cleaned.Length == 0 ? DefaultTitle : cleaned;
    }

    /// <summary>Base name: <c>dshremote-&lt;title&gt;-&lt;yyyyMMdd-HHmmss&gt;</c>.</summary>
    public static string BuildBaseName(string? title, DateTimeOffset timestamp)
    {
        var safeTitle = SanitizeTitle(title);
        return "dshremote-" + safeTitle + "-" + timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Names for the produced files. A single image has no page suffix; split captures get
    /// <c>-p1</c>, <c>-p2</c>, … so the order is obvious in the gallery and in a file browser.
    /// </summary>
    public static IReadOnlyList<string> BuildFileNames(string? title, DateTimeOffset timestamp, int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "至少需要一张长图。");
        }

        var baseName = BuildBaseName(title, timestamp);
        if (count == 1)
        {
            return new[] { baseName + ".png" };
        }

        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = baseName + "-p" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".png";
        }

        return names;
    }

    /// <summary>Short human description used by the in-app result banner.</summary>
    public static string DescribeResult(string? title, int imageCount, int pixelWidth, int totalPixelHeight)
    {
        var size = pixelWidth + "×" + totalPixelHeight.ToString(CultureInfo.InvariantCulture);
        return imageCount == 1
            ? $"长图已生成：{size} px"
            : $"长图已生成：{imageCount} 张，合计 {size} px";
    }
}
