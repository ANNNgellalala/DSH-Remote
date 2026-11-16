using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshRemote.App.Views;

namespace DshRemote.App.Services;

/// <summary>
/// Desktop-shell automation probe for the long screenshot renderer (requirement 4/5): after the demo
/// chat is on screen it runs the exact production capture path and writes the PNG(s) plus a small text
/// report to a caller-chosen location, then the head exits with 0. This gives CI/agents a file they can
/// inspect with an image reader instead of driving the UI by hand.
/// </summary>
public static class LongShotProbe
{
    /// <summary>Suffix of the machine-readable sidecar written next to the PNG(s).</summary>
    public const string ReportSuffix = ".report.txt";

    /// <summary>Exit code when the chat page or its transcript is not available.</summary>
    public const int ExitNoChatPage = 2;

    /// <summary>Exit code when rendering/writing failed.</summary>
    public const int ExitRenderFailed = 3;

    /// <summary>
    /// Renders the visible demo session to <paramref name="outputPath"/>. A single page uses the given
    /// file name; split captures become <c>&lt;stem&gt;-p1.png</c>, <c>-p2.png</c>, … next to it.
    /// Returns a process exit code (0 = success).
    /// </summary>
    public static async Task<int> RunAsync(string outputPath, LongShotLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("[longshot-probe] no output path given");
            return ExitRenderFailed;
        }

        try
        {
            var chat = FindChatView();
            if (chat is null)
            {
                Console.Error.WriteLine("[longshot-probe] chat page is not on screen");
                return ExitNoChatPage;
            }

            // Give markdown/images/first layout a moment to settle — the probe must not capture a
            // half-arranged transcript.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            var result = await chat.GenerateLongShotAsync(limits, progress: null, CancellationToken.None);

            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }

            Directory.CreateDirectory(directory);
            var stem = Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "longshot";
            }

            var extension = Path.GetExtension(fullPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var written = new List<string>(result.Pages.Count);
            foreach (var page in result.Pages)
            {
                var name = result.Pages.Count == 1
                    ? stem + extension
                    : $"{stem}-p{page.Index + 1}{extension}";
                var path = Path.Combine(directory, name);
                await File.WriteAllBytesAsync(path, page.PngBytes);
                written.Add(path);
            }

            var reportPath = Path.Combine(directory, stem + ReportSuffix);
            await File.WriteAllTextAsync(reportPath, BuildReport(result, written), Encoding.UTF8);

            Console.WriteLine(
                $"[longshot-probe] OK pages={result.Pages.Count} size={result.PixelWidth}x{result.TotalPixelHeight} " +
                $"downscaled={result.Downscaled} rows={result.VisibleRowCount} files={string.Join(";", written)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[longshot-probe] FAILED: " + ex);
            return ExitRenderFailed;
        }
    }

    private static ChatView? FindChatView()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return desktop.MainWindow?.GetVisualDescendants().OfType<ChatView>().FirstOrDefault();
    }

    private static string BuildReport(LongShotCaptureResult result, IReadOnlyList<string> files)
    {
        var builder = new StringBuilder();
        builder.AppendLine("dshremote longshot probe");
        builder.AppendLine("time: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        builder.AppendLine("pages: " + result.Pages.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("pixel_width: " + result.PixelWidth.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("pixel_height: " + result.TotalPixelHeight.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("render_scale: " + result.Plan.RenderScale.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("downscaled: " + (result.Downscaled ? "true" : "false"));
        builder.AppendLine("exceeded_budget: " + (result.ExceededBudget ? "true" : "false"));
        builder.AppendLine("unloaded_history: " + (result.ContainsUnloadedHistory ? "true" : "false"));
        builder.AppendLine("visible_rows: " + result.VisibleRowCount.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < result.Pages.Count; index++)
        {
            var page = result.Pages[index];
            builder.AppendLine(
                "page " + (index + 1).ToString(CultureInfo.InvariantCulture) + ": " +
                page.PixelWidth.ToString(CultureInfo.InvariantCulture) + "x" +
                page.PixelHeight.ToString(CultureInfo.InvariantCulture) + " " +
                page.PngBytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes " + files[index]);
        }

        return builder.ToString();
    }
}
