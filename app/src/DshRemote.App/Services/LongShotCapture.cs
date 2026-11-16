using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DshRemote.App.Services;

/// <summary>Progress stages reported while a long screenshot is produced.</summary>
public enum LongShotStage
{
    Preparing,
    Rendering,
    Encoding,
    Saving
}

/// <summary>One progress tick: what the overlay shows ("正在生成第 2/4 张…").</summary>
public sealed record LongShotProgress(LongShotStage Stage, int Completed, int Total, string Message);

/// <summary>One encoded long-screenshot page, ready to persist or preview.</summary>
public sealed record LongShotPage(string FileName, byte[] PngBytes, int PixelWidth, int PixelHeight, int Index);

/// <summary>Result of rendering the whole loaded transcript into one or more PNGs.</summary>
public sealed class LongShotCaptureResult
{
    public required IReadOnlyList<LongShotPage> Pages { get; init; }

    public required LongShotPlan Plan { get; init; }

    /// <summary>True when the session still has older pages that are not part of this capture.</summary>
    public required bool ContainsUnloadedHistory { get; init; }

    /// <summary>Number of transcript rows that were on screen (informational, used by the probe report).</summary>
    public required int VisibleRowCount { get; init; }

    public int PixelWidth => Plan.PixelWidth;

    public int TotalPixelHeight => Plan.TotalPixelHeight;

    public bool Downscaled => Plan.Downscaled;

    public bool ExceededBudget => Plan.ExceededBudget;
}

/// <summary>
/// Renders the transcript content control (the StackPanel inside the chat <c>ScrollViewer</c>) into one or
/// more opaque PNGs.
/// <para>
/// Why not the system screenshot feature: DSH Remote is an Avalonia/Skia single-surface app, so Android's
/// scroll-capture cannot scroll and stitch its transcript. This renderer walks the *virtual* content
/// (the ItemsControl is not virtualized, so the whole loaded transcript is laid out) and paints it into
/// offscreen <see cref="RenderTargetBitmap"/>s, slicing it to stay inside the pixel/edge budget.
/// </para>
/// <para>
/// Everything here runs on the UI thread (Avalonia visuals are not thread-safe) but yields between
/// slices, so the page keeps processing input and never trips an ANR. The whole capture is bounded by
/// <see cref="LongShotPlan"/>: at most 8 M px / 12 000 px per image, at least 640 px wide.
/// </para>
/// </summary>
public static class LongShotCapture
{
    /// <summary>Absolute minimum for the render scale handed to the planner.</summary>
    private const double MinScale = 0.25;

    /// <summary>Absolute maximum for the render scale handed to the planner.</summary>
    private const double MaxScale = 8.0;

    /// <summary>
    /// Captures <paramref name="content"/> (a <see cref="Panel"/>: the transcript StackPanel) into PNG
    /// pages. <paramref name="background"/> is painted behind the content so the PNG has no transparent
    /// pixels; <paramref name="renderScale"/> is the device scale (pixels per DIP).
    /// </summary>
    public static async Task<LongShotCaptureResult> CaptureAsync(
        Panel content,
        IBrush? background,
        string? fileNameTitle,
        double renderScale,
        bool containsUnloadedHistory,
        int visibleRowCount,
        LongShotLimits? limits = null,
        IProgress<LongShotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        progress?.Report(new LongShotProgress(LongShotStage.Preparing, 0, 0, "正在准备长图…"));
        await Task.Yield();

        var widthDip = content.Bounds.Width;
        var heightDip = content.Bounds.Height;
        if (!double.IsFinite(widthDip) || widthDip <= 0 || !double.IsFinite(heightDip) || heightDip <= 0)
        {
            throw new InvalidOperationException("会话内容尚未完成排版，无法生成长图。请稍后重试。");
        }

        var scale = Math.Clamp(renderScale, MinScale, MaxScale);
        var plan = LongShotPlanner.Create(widthDip, heightDip, scale, limits);
        var fileNames = LongShotNaming.BuildFileNames(fileNameTitle, DateTimeOffset.Now, plan.SegmentCount);

        // The PNG must be opaque: paint the page background behind the transcript rows. Panel renders its
        // own Background, so temporarily giving the transcript the page colour is enough (and invisible to
        // the user, since the page already draws exactly that colour behind it).
        var originalBackground = content.Background;
        var originalTransform = content.RenderTransform;
        var pages = new List<LongShotPage>(plan.SegmentCount);

        try
        {
            content.Background = background ?? Brushes.Black;

            for (var index = 0; index < plan.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = plan.Segments[index];
                progress?.Report(new LongShotProgress(
                    LongShotStage.Rendering,
                    index,
                    plan.SegmentCount,
                    plan.SegmentCount == 1
                        ? "正在生成长图…"
                        : $"正在生成第 {index + 1}/{plan.SegmentCount} 张…"));

                using var bitmap = RenderSegment(content, segment, plan.RenderScale);

                // Encode on the UI thread so the bitmap stays single-threaded, then hand the bytes out.
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LongShotProgress(
                    LongShotStage.Encoding,
                    index,
                    plan.SegmentCount,
                    plan.SegmentCount == 1 ? "正在编码 PNG…" : $"正在编码第 {index + 1}/{plan.SegmentCount} 张…"));

                using var stream = new MemoryStream(EstimatePngSize(plan.PixelWidth, segment.PixelHeight));
                bitmap.Save(stream, new PngBitmapEncoderOptions
                {
                    CompressionLevel = System.IO.Compression.CompressionLevel.Fastest
                });
                pages.Add(new LongShotPage(
                    fileNames[index],
                    stream.ToArray(),
                    plan.PixelWidth,
                    segment.PixelHeight,
                    index));

                // Let the dispatcher breathe between slices: layout/input keep flowing, no ANR.
                await Task.Yield();
            }
        }
        finally
        {
            content.RenderTransform = originalTransform;
            content.Background = originalBackground;
        }

        return new LongShotCaptureResult
        {
            Pages = pages,
            Plan = plan,
            ContainsUnloadedHistory = containsUnloadedHistory,
            VisibleRowCount = visibleRowCount
        };
    }

    /// <summary>
    /// Renders one slice. The content is translated up by the slice offset in DIP and captured with a DPI
    /// matching <paramref name="renderScale"/>, so text stays crisp at the device's pixel density.
    /// </summary>
    private static RenderTargetBitmap RenderSegment(Panel content, LongShotSegment segment, double renderScale)
    {
        var bitmap = new RenderTargetBitmap(
            new PixelSize(segment.PixelWidth, segment.PixelHeight),
            new Vector(96 * renderScale, 96 * renderScale));

        try
        {
            var offsetDip = segment.OffsetY / renderScale;
            content.RenderTransform = offsetDip > 0.01 ? new TranslateTransform(0, -offsetDip) : null;
            bitmap.Render(content);
        }
        finally
        {
            content.RenderTransform = null;
        }

        return bitmap;
    }

    private static int EstimatePngSize(int width, int height)
    {
        // Typical transcript screenshots compress to ~10-20 % of raw RGBA; reserve that to avoid
        // MemoryStream doubling its buffer on 8 M px pages.
        var raw = (long)width * height * 4;
        return (int)Math.Clamp(raw / 5, 64 * 1024, 64 * 1024 * 1024);
    }
}
