using System.Collections.Specialized;
using Arc.Avalonia.Actions.Controls;
using Arc.Avalonia.Controls;
using Arc.Avalonia.Internal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshRemote.App.Controls;
using DshRemote.App.Platform;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using DshRemote.Core;

namespace DshRemote.App.Views;

public partial class ChatView : UserControl
{
    private SessionViewModel? _session;
    private TopLevel? _topLevel;
    private IInputPane? _inputPane;
    private DispatcherTimer? _imeCoalesceTimer;
    private double _pendingImeHeight;
    private double _imeHeight;
    private double _olderAnchorOffset;
    private double _olderAnchorExtent;
    private bool _olderLoadInProgress;
    private bool _suppressAutoScroll;
    private bool _suppressAutoExpandFocus;
    private bool _followTail = true;
    private bool _autoScrollPosted;
    private double _lastTrackedExtent = -1;

    // ---- Copy (message row / selected text) ----------------------------------------------------
    private SelectableTextBlock? _selectedBlock;
    private string _selectedText = string.Empty;
    private DispatcherTimer? _copyToastTimer;

    // ---- Long screenshot state -----------------------------------------------------------------
    private bool _longShotBusy;
    private CancellationTokenSource? _longShotCancellation;
    private LongShotCaptureResult? _longShotResult;
    private IReadOnlyList<PlatformSavedImage> _longShotSaved = Array.Empty<PlatformSavedImage>();

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        // Avalonia's SelectableTextBlock only copies via the keyboard: on a phone a selection has no copy
        // affordance at all. Watch releases in the transcript and surface the selection bar for the block
        // that owns the selection (Markdown blocks, reasoning, tool arguments/results all use it).
        MessagesScroll.AddHandler(
            InputElement.PointerReleasedEvent,
            OnTranscriptPointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // Tunnel so Enter is intercepted before TextBox inserts a newline (AcceptsReturn=true).
        // Soft keyboards (Android/iOS) are exempt: their newline key must insert a line break, not send.
        // The IME only keeps its keyboard open for that because both composer TextBoxes declare
        // TextInputOptions.Multiline (Avalonia then publishes TYPE_TEXT_FLAG_MULTI_LINE +
        // IME_FLAG_NO_ENTER_ACTION); without it the IME performs an "action" (done/send) and closes.
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        PromptBox.GotFocus += (_, _) =>
        {
            if (!_suppressAutoExpandFocus)
            {
                _session?.OpenComposerFullscreen();
            }
        };
        FullscreenPromptBox.AddHandler(KeyDownEvent, OnFullscreenKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachTopLevel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DiagnosticsLog.Trace("chat: detach");
        MessagesScroll.LayoutUpdated -= OnMessagesLayoutUpdated;
        if (_session is not null)
        {
            _session.Messages.CollectionChanged -= OnTranscriptChanged;
            _session.TranscriptUpdated -= OnTranscriptUpdated;
            _session.OlderPageInserted -= OnOlderPageInserted;
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.ImagePicker = null;
            _session = null;
        }

        if (_inputPane is not null)
        {
            _inputPane.StateChanged -= OnInputPaneStateChanged;
            _inputPane = null;
        }

        if (_topLevel is not null)
        {
            _topLevel = null;
        }

        _imeCoalesceTimer?.Stop();
        _imeCoalesceTimer = null;
        _copyToastTimer?.Stop();
        _copyToastTimer = null;
        HideSelectionCopyBar();
        if (CopyToast is not null)
        {
            CopyToast.IsVisible = false;
        }

        PlatformServices.LayoutInsetsChanged -= OnPlatformLayoutInsetsChanged;
    }

    private void Attach()
    {
        if (_session is not null)
        {
            _session.Messages.CollectionChanged -= OnTranscriptChanged;
            _session.TranscriptUpdated -= OnTranscriptUpdated;
            _session.OlderPageInserted -= OnOlderPageInserted;
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.ImagePicker = null;
        }

        _session = DataContext as SessionViewModel;
        if (_session is not null)
        {
            DiagnosticsLog.Trace($"chat: attach session={_session.SessionId}");
            _session.Messages.CollectionChanged += OnTranscriptChanged;
            _session.TranscriptUpdated += OnTranscriptUpdated;
            _session.OlderPageInserted += OnOlderPageInserted;
            _session.PropertyChanged += OnSessionPropertyChanged;
            _session.ImagePicker = PickImageAsync;
        }

        // Markdown bodies grow as the PuraMarkdown.UI engine returns frames; follow the tail when the user is
        // already at the bottom so a long final answer does not finish rendering below the fold.
        MessagesScroll.LayoutUpdated -= OnMessagesLayoutUpdated;
        MessagesScroll.LayoutUpdated += OnMessagesLayoutUpdated;

        // Session actions live behind the title dropdown; the long screenshot is the only entry today.
        SessionMenu.Items = new List<ArcMenuItem>
        {
            new("截取长图", () => _ = RunLongShotAsync(), "image")
        };

        ScrollToEndSoon();
        HideSelectionCopyBar();
    }

    private void AttachTopLevel()
    {
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel?.InputPane is { } pane)
        {
            _inputPane = pane;
            pane.StateChanged += OnInputPaneStateChanged;
            RequestImeHeight(pane.OccludedRect.Height);
        }

        // The Android head also reports the keyboard height through PlatformIntegration (for heads where
        // Avalonia's IInputPane is not wired). Listen to its change notification instead of polling: a
        // timer that re-applies the bottom margin fights the platform IME animation and can lock the UI
        // thread on OEM devices.
        PlatformServices.LayoutInsetsChanged -= OnPlatformLayoutInsetsChanged;
        PlatformServices.LayoutInsetsChanged += OnPlatformLayoutInsetsChanged;
        RequestImeHeight(PlatformServices.ImeHeight);
    }

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        RequestImeHeight(e.EndRect.Height);
    }

    private void OnPlatformLayoutInsetsChanged()
    {
        RequestImeHeight(PlatformServices.ImeHeight);
    }

    /// <summary>
    /// Coalesces IME-height notifications into a single layout update. The platform pane and the head's
    /// inset callback can both fire several times per keyboard animation; applying each directly re-enters
    /// measure/arrange while the IME window is still moving.
    /// </summary>
    private void RequestImeHeight(double height)
    {
        _pendingImeHeight = Math.Max(0, height);
        if (_imeCoalesceTimer is null)
        {
            _imeCoalesceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _imeCoalesceTimer.Tick += (_, _) => FlushImeHeight();
        }

        _imeCoalesceTimer.Stop();
        _imeCoalesceTimer.Start();
    }

    private void FlushImeHeight()
    {
        _imeCoalesceTimer?.Stop();
        var height = _pendingImeHeight;
        if (Math.Abs(height - _imeHeight) < 1)
        {
            return;
        }

        var previousHeight = _imeHeight;
        _imeHeight = height;
        if (ChatRoot is not null)
        {
            ChatRoot.Margin = new Thickness(0, 0, 0, _imeHeight);
        }

        if (previousHeight <= 0 && _imeHeight > 0)
        {
            DiagnosticsLog.Trace($"chat: ime shown height={_imeHeight:F0}");
        }
        else if (previousHeight > 0 && _imeHeight <= 0)
        {
            DiagnosticsLog.Trace("chat: ime hidden");
        }
    }

    /// <summary>Opens the cascading model/reasoning-effort picker; disabled while a turn runs.</summary>
    private void OnModelChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SessionViewModel session)
        {
            return;
        }

        e.Handled = true;
        if (session.CanOpenModelPicker)
        {
            DiagnosticsLog.Trace("chat: model chip pressed");
            session.OpenModelPickerCommand.Execute(null);
        }
    }

    /// <summary>Closes the in-page model picker when the dimmed backdrop is tapped.</summary>
    private void OnModelPickerBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is SessionViewModel session)
        {
            session.CloseModelPickerCommand.Execute(null);
        }
    }

    private void OnImageViewerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SessionViewModel session)
        {
            session.CloseImageCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Copy: one action per message row, plus selected-text copy -------------------------------

    /// <summary>
    /// Shows the selection bar when a pointer release leaves a non-empty selection in a transcript text
    /// block. Avalonia's <see cref="SelectableTextBlock"/> has no built-in context menu and the phone has no
    /// copy shortcut, so without this a selection could never reach the clipboard.
    /// </summary>
    private void OnTranscriptPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Right-click is the desktop context-menu gesture; it must not hijack a selection.
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            return;
        }

        var block = FindSelectableTextBlock(e.Source as Visual);
        var text = block?.SelectedText;
        if (string.IsNullOrEmpty(text))
        {
            HideSelectionCopyBar();
            return;
        }

        _selectedBlock = block;
        _selectedText = text;
        SelectionCopyText.Text = "已选择 " + text.Length + " 字";
        SelectionCopyBar.IsVisible = true;
    }

    private static SelectableTextBlock? FindSelectableTextBlock(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is SelectableTextBlock block)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>Copies the whole row (message body, tool result/arguments, or compaction summary).</summary>
    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChatMessageViewModel message)
        {
            return;
        }

        var text = message.CopyText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var copied = await CopyTextAsync(text).ConfigureAwait(true);
        ShowCopyToast(copied ? "已复制" : "复制失败：剪贴板不可用");
    }

    private async void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        // Capture the text before the button takes focus: losing focus clears the block's selection.
        var text = _selectedText;
        if (string.IsNullOrEmpty(text))
        {
            HideSelectionCopyBar();
            return;
        }

        var copied = await CopyTextAsync(text).ConfigureAwait(true);
        ClearSelection();
        HideSelectionCopyBar();
        ShowCopyToast(copied ? "已复制 " + text.Length + " 字" : "复制失败：剪贴板不可用");
    }

    private void OnDismissSelectionClick(object? sender, RoutedEventArgs e)
    {
        ClearSelection();
        HideSelectionCopyBar();
    }

    private async Task<bool> CopyTextAsync(string text)
    {
        var clipboard = (_topLevel ?? TopLevel.GetTopLevel(this))?.Clipboard;
        if (clipboard is null)
        {
            DiagnosticsLog.Trace("copy: no clipboard");
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            DiagnosticsLog.Trace($"copy: {text.Length} chars");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Trace("copy: failed " + ex.Message);
            return false;
        }
    }

    private void ClearSelection()
    {
        try
        {
            _selectedBlock?.ClearSelection();
        }
        catch
        {
            // The row may already be recycled/detached; there is nothing left to clear.
        }

        _selectedBlock = null;
        _selectedText = string.Empty;
    }

    private void HideSelectionCopyBar()
    {
        if (SelectionCopyBar is not null)
        {
            SelectionCopyBar.IsVisible = false;
        }

        _selectedBlock = null;
        _selectedText = string.Empty;
    }

    private void ShowCopyToast(string message)
    {
        if (CopyToast is null || CopyToastText is null)
        {
            return;
        }

        CopyToastText.Text = message;
        CopyToast.IsVisible = true;
        _copyToastTimer ??= CreateCopyToastTimer();
        _copyToastTimer.Stop();
        _copyToastTimer.Start();
    }

    private DispatcherTimer CreateCopyToastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (CopyToast is not null)
            {
                CopyToast.IsVisible = false;
            }
        };
        return timer;
    }

    /// <summary>
    /// Image picker for the composer: the platform head may register one (Android photo picker); the
    /// desktop shell falls back to Avalonia's StorageProvider file dialog.
    /// </summary>
    private async Task<DshImageAttachment?> PickImageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var platformPick = await PlatformServices.PickImageAsync(cancellationToken).ConfigureAwait(true);
            if (platformPick is not null)
            {
                return platformPick;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // Fall through to the desktop file picker.
        }

        var topLevel = _topLevel ?? TopLevel.GetTopLevel(this);
        var storage = topLevel?.StorageProvider;
        if (storage is null || !storage.CanOpen)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        }).ConfigureAwait(true);

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        await using var source = await file.OpenReadAsync().ConfigureAwait(true);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(true);
        return new DshImageAttachment(buffer.ToArray(), GuessMediaType(file.Name), file.Name);
    }

    private static string GuessMediaType(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressAutoScroll)
        {
            ScrollToEndSoon();
        }
    }

    // ---- Full-screen composer -------------------------------------------------------------------

    private void OnFullscreenKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _session?.CollapseComposerFullscreen();
        }
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.IsComposerExpanded) || _session is null)
        {
            return;
        }

        var expanded = _session.IsComposerExpanded;
        Dispatcher.UIThread.Post(
            () =>
            {
                var target = expanded ? FullscreenPromptBox : PromptBox;
                try
                {
                    if (!expanded)
                    {
                        // Returning focus to the inline composer must not immediately re-open the overlay.
                        _suppressAutoExpandFocus = true;
                    }

                    target.Focus();
                    target.CaretIndex = target.Text?.Length ?? 0;
                }
                catch
                {
                    // The overlay may not be attached yet; a later focus pass retries.
                }
                finally
                {
                    Dispatcher.UIThread.Post(() => _suppressAutoExpandFocus = false, DispatcherPriority.Input);
                }
            },
            DispatcherPriority.Background);
    }

    private void OnTranscriptUpdated(object? sender, EventArgs e)
    {
        if (!_suppressAutoScroll)
        {
            ScrollToEndSoon();
        }
    }

    // ---- Lazy history paging: keep the reading position when an older page is prepended -----------

    private void OnLoadOlderClick(object? sender, RoutedEventArgs e) => BeginOlderPageLoad();

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Tail-follow bookkeeping: a user scroll changes the offset without changing the extent, while
        // markdown frames growing the transcript change the extent only. Don't let growth mark the user as
        // "scrolled away".
        if (!_suppressAutoScroll && Math.Abs(e.ExtentDelta.Y) < 0.5)
        {
            var extent = MessagesScroll.Extent.Height;
            var distanceFromEnd = extent - MessagesScroll.Viewport.Height - MessagesScroll.Offset.Y;
            _followTail = distanceFromEnd <= 96;
        }

        // Near the top: load the previous page automatically (the button remains the explicit path).
        if (_suppressAutoScroll || _olderLoadInProgress || MessagesScroll.Offset.Y > 80)
        {
            return;
        }

        BeginOlderPageLoad();
    }

    /// <summary>
    /// Keeps the viewport pinned to the newest content while deferred Markdown frames make the transcript grow
    /// taller (the engine renders large documents in slices, so extent changes arrive over several frames).
    /// </summary>
    private void OnMessagesLayoutUpdated(object? sender, EventArgs e)
    {
        var extent = MessagesScroll.Extent.Height;
        var previous = _lastTrackedExtent;
        _lastTrackedExtent = extent;
        if (previous < 0 || _suppressAutoScroll || _olderLoadInProgress || extent <= previous + 0.5)
        {
            return;
        }

        var distanceFromEnd = extent - MessagesScroll.Viewport.Height - MessagesScroll.Offset.Y;
        if (_followTail && distanceFromEnd > 1)
        {
            ScrollToEndSoon();
        }
    }

    private void BeginOlderPageLoad()
    {
        if (_session is null || _longShotBusy || !_session.HasOlder || _session.IsLoadingOlder || _olderLoadInProgress)
        {
            return;
        }

        _olderLoadInProgress = true;
        _suppressAutoScroll = true;
        _olderAnchorOffset = MessagesScroll.Offset.Y;
        _olderAnchorExtent = MessagesScroll.Extent.Height;
        _session.LoadOlderCommand.Execute(null);
    }

    private void OnOlderPageInserted(object? sender, int inserted)
    {
        // Restore after the layout pass that measures the prepended rows.
        Dispatcher.UIThread.Post(
            () =>
            {
                var delta = MessagesScroll.Extent.Height - _olderAnchorExtent;
                _olderAnchorExtent = MessagesScroll.Extent.Height;
                if (delta > 0)
                {
                    MessagesScroll.Offset = new Vector(MessagesScroll.Offset.X, _olderAnchorOffset + delta);
                }

                _olderAnchorOffset = MessagesScroll.Offset.Y;
                _olderLoadInProgress = false;
                _suppressAutoScroll = false;
            },
            DispatcherPriority.Loaded);
    }

    private void ScrollToEndSoon()
    {
        _followTail = true;
        if (_autoScrollPosted)
        {
            return;
        }

        _autoScrollPosted = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _autoScrollPosted = false;
                try
                {
                    MessagesScroll.ScrollToEnd();
                }
                catch
                {
                    // The viewer may not be attached yet; the next update retries.
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Waits until no Markdown view is queued and the transcript extent stopped changing between layout passes
    /// (the PuraMarkdown.UI engine's deferred frames only grow the extent). Bounded so a pathological document
    /// cannot hang a capture; the caller then captures the best settled state it has.
    /// </summary>
    private async Task WaitForTranscriptQuiescenceAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var stablePasses = 0;
        var previousExtent = -1.0;
        while (stablePasses < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(80).ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            var extent = MessagesScroll.Extent.Height;
            if (MarkdownRenderQueue.IsIdle && Math.Abs(extent - previousExtent) < 0.5)
            {
                stablePasses++;
            }
            else
            {
                stablePasses = 0;
            }

            previousExtent = extent;
        }

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SessionViewModel session)
        {
            return;
        }

        // Android soft keyboards send Enter for their newline key: it must insert a line break and keep
        // the keyboard open (send with the button instead). The composer TextBoxes are declared multiline
        // for the IME (TextInputOptions.Multiline in ChatView.axaml), which is what makes the IME keep the
        // keyboard up after Enter; hardware keyboards keep the desktop rule below.
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return; // Shift+Enter / Ctrl+Enter insert a newline.
        }

        e.Handled = true;
        if (session.CanSend)
        {
            session.SendCommand.Execute(null);
        }
    }

    // ---- Long screenshot ("长图") ---------------------------------------------------------------

    /// <summary>
    /// Renders the whole *loaded* transcript into one or more opaque PNGs. Public so the desktop
    /// automation probe (<c>--longshot-probe</c>) drives exactly the same production path.
    /// </summary>
    public async Task<LongShotCaptureResult> GenerateLongShotAsync(
        LongShotLimits? limits = null,
        IProgress<LongShotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_session is not { } session)
        {
            throw new InvalidOperationException("会话页面尚未就绪，无法生成长图。");
        }

        if (MessagesScroll.Content is not Panel content)
        {
            throw new InvalidOperationException("找不到会话内容容器，无法生成长图。");
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var background = ChatRoot.Background;
        var hidden = HideTransientChrome(content);
        var previousScrollBarVisibility = MessagesScroll.VerticalScrollBarVisibility;
        var previousSuppressAutoScroll = _suppressAutoScroll;

        MessagesScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _suppressAutoScroll = true;
        try
        {
            // Hiding the scrollbar changes the viewport width: let one layout pass settle before measuring.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);

            // Markdown bodies may still be upgrading from plain text (render queue) or receiving deferred
            // engine frames; capture only once the transcript height is stable.
            await WaitForTranscriptQuiescenceAsync().ConfigureAwait(true);

            var result = await LongShotCapture.CaptureAsync(
                content,
                background,
                session.Title,
                scale,
                containsUnloadedHistory: session.HasOlder,
                visibleRowCount: session.Timeline.Count,
                limits,
                progress,
                cancellationToken).ConfigureAwait(true);

            if (!ReferenceEquals(_session, session) || TopLevel.GetTopLevel(this) is null)
            {
                throw new OperationCanceledException("会话页面已关闭，长截图已取消。");
            }

            return result;
        }
        finally
        {
            MessagesScroll.VerticalScrollBarVisibility = previousScrollBarVisibility;
            _suppressAutoScroll = previousSuppressAutoScroll;
            RestoreTransientChrome(hidden);
        }
    }

    // ---- Long screenshot ("长图") ---------------------------------------------------------------

    private async Task RunLongShotAsync()
    {
        if (_longShotBusy || _session is null)
        {
            return;
        }

        _longShotBusy = true;
        HideLongShotResult();
        _longShotCancellation?.Dispose();
        _longShotCancellation = new CancellationTokenSource();
        var cancellationToken = _longShotCancellation.Token;
        ShowLongShotProgress("正在准备长图…");

        try
        {
            var progress = new Progress<LongShotProgress>(update =>
            {
                if (LongShotProgressText is not null)
                {
                    LongShotProgressText.Text = update.Message;
                }
            });

            var result = await GenerateLongShotAsync(progress: progress, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            LongShotProgressText.Text = "正在保存到相册…";
            var platformPages = result.Pages
                .Select(page => new PlatformImagePage(page.FileName, page.PngBytes, page.PixelWidth, page.PixelHeight))
                .ToList();
            var saved = await PlatformServices.SaveImagesAsync(platformPages, cancellationToken).ConfigureAwait(true);

            _longShotResult = result;
            _longShotSaved = saved;
            ShowLongShotResult(BuildLongShotResultText(result, saved));
        }
        catch (OperationCanceledException)
        {
            ShowLongShotError("长截图已取消。");
        }
        catch (Exception ex)
        {
            ShowLongShotError("长截图失败：" + ex.Message);
        }
        finally
        {
            _longShotBusy = false;
            HideLongShotProgress();
        }
    }

    private void OnLongShotCancel(object? sender, RoutedEventArgs e)
    {
        try
        {
            _longShotCancellation?.Cancel();
            if (LongShotProgressText is not null)
            {
                LongShotProgressText.Text = "正在取消…";
            }
        }
        catch
        {
            // Cancellation is best effort; the capture loop also observes _longShotBusy/page lifetime.
        }
    }

    private void OnLongShotDismiss(object? sender, RoutedEventArgs e) => HideLongShotResult();

    private void OnLongShotView(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _longShotResult is null || _longShotResult.Pages.Count == 0)
        {
            return;
        }

        var page = _longShotResult.Pages[0];
        _session.ShowImage(ChatImagePartViewModel.FromLocalBytes(page.PngBytes, "image/png"));
        HideLongShotResult();
    }

    private async void OnLongShotShare(object? sender, RoutedEventArgs e)
    {
        if (_longShotSaved.Count == 0)
        {
            return;
        }

        try
        {
            var shared = await PlatformServices.ShareImagesAsync(_longShotSaved).ConfigureAwait(true);
            if (_session is null || LongShotResultText is null)
            {
                return;
            }

            LongShotResultText.Text = shared
                ? LongShotResultText.Text + "\n已打开系统分享。"
                : LongShotResultText.Text + "\n当前平台没有系统分享入口，文件已保存在上面的位置。";
        }
        catch (Exception ex)
        {
            if (LongShotResultText is not null)
            {
                LongShotResultText.Text = LongShotResultText.Text + "\n分享失败：" + ex.Message;
            }
        }
    }

    private void ShowLongShotProgress(string message)
    {
        if (LongShotProgressText is not null)
        {
            LongShotProgressText.Text = message;
        }

        if (LongShotProgressOverlay is not null)
        {
            LongShotProgressOverlay.IsVisible = true;
        }
    }

    private void HideLongShotProgress()
    {
        if (LongShotProgressOverlay is not null)
        {
            LongShotProgressOverlay.IsVisible = false;
        }
    }

    private void ShowLongShotResult(string text)
    {
        if (LongShotResultText is not null)
        {
            LongShotResultText.Text = text;
        }

        if (LongShotResultBanner is not null)
        {
            LongShotResultBanner.IsVisible = true;
        }
    }

    private void ShowLongShotError(string message)
    {
        if (LongShotResultText is not null)
        {
            LongShotResultText.Text = message;
        }

        _longShotResult = null;
        _longShotSaved = Array.Empty<PlatformSavedImage>();
        if (LongShotResultBanner is not null)
        {
            LongShotResultBanner.IsVisible = true;
        }
    }

    private void HideLongShotResult()
    {
        if (LongShotResultBanner is not null)
        {
            LongShotResultBanner.IsVisible = false;
        }
    }

    private static string BuildLongShotResultText(
        LongShotCaptureResult result,
        IReadOnlyList<PlatformSavedImage> saved)
    {
        var text = LongShotNaming.DescribeResult(null, saved.Count, result.PixelWidth, result.TotalPixelHeight);
        text += $"（共 {result.VisibleRowCount} 条时间线）";
        if (saved.Count > 0 && !string.IsNullOrWhiteSpace(saved[0].Location))
        {
            text += saved.Count == 1
                ? "\n位置：" + saved[0].Location
                : "\n位置：" + saved[0].Location + " 等 " + saved.Count + " 张";
        }

        if (result.Downscaled)
        {
            text += "\n已按像素上限降采样（文字仍可读）。";
        }

        if (result.ExceededBudget)
        {
            text += "\n会话非常长，已分成 " + saved.Count + " 张保存。";
        }

        if (result.ContainsUnloadedHistory)
        {
            text += "\n注意：只包含当前已加载的内容；更早的历史请先点「加载更早的历史」再截。";
        }

        return text;
    }

    /// <summary>
    /// Hides transcript chrome that is not conversation content (the "load older" button and the
    /// loading spinners) while capturing, so the long image only contains messages.
    /// </summary>
    private static List<Control> HideTransientChrome(Panel content)
    {
        var hidden = new List<Control>();
        foreach (var child in content.Children)
        {
            if (child is not Control control || !control.IsVisible)
            {
                continue;
            }

            var isTransient = control is ArcButton
                || (control is StackPanel panel && panel.Children.Any(candidate => candidate is ArcSpinner));
            if (!isTransient)
            {
                continue;
            }

            control.IsVisible = false;
            hidden.Add(control);
        }

        return hidden;
    }

    private static void RestoreTransientChrome(IReadOnlyList<Control> hidden)
    {
        foreach (var control in hidden)
        {
            control.IsVisible = true;
        }
    }
}
