using System.Diagnostics;
using DshRemote.App.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Arc.Avalonia.Markdown;

namespace DshRemote.App.Controls;

/// <summary>
/// Message body: cheap plain text while streaming, Arc's <see cref="ArcMarkdownView"/> once the message is final.
/// <para>
/// Since the Arc.Avalonia refactor (PuraMarkdown 0.2) Arc delegates Markdown parsing/rendering to the
/// <c>PuraMarkdown.UI</c> engine, so the host deliberately keeps no Markdown logic of its own: one
/// <see cref="ArcMarkdownView"/> renders the whole message. The engine renders each top-level block as its own
/// control inside a stacking host (blocks cannot overlap, so the old blank-line block splitting workaround is
/// gone), frames long documents through its render scheduler instead of blocking the UI thread, and owns link
/// rendering: inline links are text runs in the normal line box, and bare <c>http(s)</c> URLs are autolinked by
/// Arc's pipeline even after CJK punctuation (so the old host-side <c>&lt;…&gt;</c> wrapping workaround is gone).
/// </para>
/// <para>
/// What remains host-side is presentation policy only: plain text while streaming, a size clamp for very long
/// messages, paced view construction (<see cref="MarkdownRenderQueue"/>) so opening a transcript with many
/// final answers does not build every Arc view in one layout pass, and disposing a replaced engine view so its
/// pending framed render does not keep running against a detached tree.
/// </para>
/// </summary>
public sealed class MarkdownBodyView : ContentControl
{
    private const int MaxMarkdownChars = 60000;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarkdownBodyView, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<bool> IsStreamingProperty =
        AvaloniaProperty.Register<MarkdownBodyView, bool>(nameof(IsStreaming));

    /// <summary>True when the row should render Markdown (final assistant messages).</summary>
    public static readonly StyledProperty<bool> UseMarkdownProperty =
        AvaloniaProperty.Register<MarkdownBodyView, bool>(nameof(UseMarkdown));

    public static readonly StyledProperty<bool> MutedProperty =
        AvaloniaProperty.Register<MarkdownBodyView, bool>(nameof(Muted));

    private readonly SelectableTextBlock _plain;
    private ArcMarkdownView? _markdownView;
    private bool _hasMarkdown;
    private bool _showMarkdown;
    private string _rendered = string.Empty;

    static MarkdownBodyView()
    {
        ClipToBoundsProperty.OverrideDefaultValue<MarkdownBodyView>(true);
        TextProperty.Changed.AddClassHandler<MarkdownBodyView>((view, _) => view.UpdateContent());
        UseMarkdownProperty.Changed.AddClassHandler<MarkdownBodyView>((view, _) => view.SyncMode());
        MutedProperty.Changed.AddClassHandler<MarkdownBodyView>((view, _) => view.ApplyMuted());
        IsStreamingProperty.Changed.AddClassHandler<MarkdownBodyView>((view, _) => view.SyncMode());
    }

    public MarkdownBodyView()
    {
        ClipToBounds = true;
        _plain = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _plain.Classes.Add("arc-body");
        Content = _plain;
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsStreaming
    {
        get => GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public bool UseMarkdown
    {
        get => GetValue(UseMarkdownProperty);
        set => SetValue(UseMarkdownProperty, value);
    }

    public bool Muted
    {
        get => GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_showMarkdown && !_hasMarkdown)
        {
            // The queue drops views that are not attached yet; make sure an attach (page switch, transcript
            // rebuild) puts the pending upgrade back in line.
            MarkdownRenderQueue.Enqueue(this);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MarkdownRenderQueue.Remove(this);
    }

    private void SyncMode()
    {
        var wantMarkdown = UseMarkdown && !IsStreaming && !string.IsNullOrWhiteSpace(Text);
        if (wantMarkdown == _showMarkdown)
        {
            return;
        }

        _showMarkdown = wantMarkdown;
        if (wantMarkdown)
        {
            // Show cheap plain text immediately and let the shared queue build the Arc Markdown surface on a
            // later tick: opening a transcript with many long answers would otherwise block layout for
            // seconds (observed as an ANR / "app froze then closed" on the phone). The engine frames large
            // documents itself, but documents at or below its 4 KB immediate threshold still render
            // synchronously inside the Arc view, so paced construction is still worth keeping.
            ShowPlain();
            MarkdownRenderQueue.Enqueue(this);
        }
        else
        {
            MarkdownRenderQueue.Remove(this);
            ShowPlain();
        }
    }

    /// <summary>Builds the Arc Markdown surface now. Returns false when the view no longer wants it.</summary>
    internal bool RenderMarkdownNow()
    {
        if (!_showMarkdown || VisualRoot is null)
        {
            return false;
        }

        ShowMarkdown();
        return true;
    }

    private void ShowMarkdown()
    {
        var started = Stopwatch.GetTimestamp();
        var text = Clamp(Text);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowPlain();
            return;
        }

        _hasMarkdown = true;
        if (_markdownView is null)
        {
            _markdownView = CreateMarkdownView();
            Content = _markdownView;
        }

        if (_rendered != text)
        {
            _rendered = text;
            _markdownView.Markdown = text;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed > 120)
        {
            DiagnosticsLog.Trace($"perf: markdown build len={text.Length} ms={elapsed:F0}");
        }
    }

    private static ArcMarkdownView CreateMarkdownView()
    {
        var view = new ArcMarkdownView
        {
            Options = new ArcMarkdownOptions { EnableTextSelection = true },
            ClipToBounds = true
        };

        // Surface Arc/PuraMarkdown warnings (link open failures, degraded blocks, slow frames) in the app
        // diagnostics log instead of losing them.
        view.Warning += (_, e) => DiagnosticsLog.Trace($"markdown: {e.Code} {e.Message}");
        return view;
    }

    private void ShowPlain()
    {
        _hasMarkdown = false;
        _rendered = string.Empty;
        if (_markdownView is not null)
        {
            // Dispose the engine view so a pending framed render job does not keep running for a tree that
            // is no longer on screen (PuraMarkdownView.Dispose stops the streaming session it owns).
            _markdownView.Dispose();
            _markdownView = null;
        }

        Content = _plain;
        UpdatePlainText();
    }

    private void UpdateContent()
    {
        if (!_showMarkdown && UseMarkdown && !IsStreaming && !string.IsNullOrWhiteSpace(Text))
        {
            // Text can arrive after UseMarkdown/IsStreaming settled; enter markdown mode from here as well.
            SyncMode();
            if (_showMarkdown)
            {
                return;
            }
        }

        if (_showMarkdown)
        {
            if (!_hasMarkdown)
            {
                // Still waiting in the markdown queue: keep the cheap plain text in sync with the model.
                UpdatePlainText();
                return;
            }

            if (_rendered != Clamp(Text))
            {
                ShowMarkdown();
            }

            return;
        }

        UpdatePlainText();
    }

    private void UpdatePlainText()
    {
        var text = Text ?? string.Empty;
        _plain.Text = IsStreaming && text.Length > 0 ? text + " ▍" : text;
    }

    private void ApplyMuted()
    {
        _plain.Classes.Set("timeline-text", Muted);
        _plain.Classes.Set("arc-body", !Muted);
    }

    /// <summary>Bounds the Markdown handed to Arc (very long messages are truncated, matching the old behavior).</summary>
    internal static string Clamp(string? text)
    {
        text ??= string.Empty;
        return text.Length > MaxMarkdownChars ? text[..MaxMarkdownChars] : text;
    }
}
