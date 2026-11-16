using Avalonia.Threading;

namespace DshRemote.App.Controls;

/// <summary>
/// Serializes Arc Markdown view construction so a transcript full of final answers cannot block the UI thread
/// for several seconds while it is being opened. Each queued view keeps rendering cheap plain text until its
/// turn comes; one view is upgraded per timer tick.
/// <para>
/// The PuraMarkdown.UI engine behind Arc already frames documents longer than 4 KB, but Arc renders documents
/// at or below that immediate threshold synchronously while <c>Markdown</c> is assigned, so without this queue
/// one layout pass could still build dozens of Arc views (and parse/render each of them) back to back. The
/// queue therefore paces host-side view construction; the engine paces the actual rendering inside each view.
/// </para>
/// </summary>
internal static class MarkdownRenderQueue
{
    private static readonly Queue<MarkdownBodyView> Pending = new();
    private static DispatcherTimer? _timer;

    /// <summary>True when no queued view is waiting for its Markdown upgrade.</summary>
    public static bool IsIdle => Pending.Count == 0;

    /// <summary>Number of views still waiting for their Markdown upgrade.</summary>
    public static int PendingCount => Pending.Count;

    public static void Enqueue(MarkdownBodyView view)
    {
        if (Pending.Contains(view))
        {
            return;
        }

        Pending.Enqueue(view);
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _timer.Tick += (_, _) => Pump();
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public static void Remove(MarkdownBodyView view)
    {
        if (Pending.Count == 0 || !Pending.Contains(view))
        {
            return;
        }

        var kept = Pending.Where(candidate => !ReferenceEquals(candidate, view)).ToArray();
        Pending.Clear();
        foreach (var candidate in kept)
        {
            Pending.Enqueue(candidate);
        }
    }

    private static void Pump()
    {
        while (Pending.Count > 0)
        {
            var view = Pending.Dequeue();
            try
            {
                if (view.RenderMarkdownNow())
                {
                    return;
                }
            }
            catch
            {
                // A failed markdown render must not stop the queue or the app.
            }
        }

        _timer?.Stop();
    }
}
