using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;

namespace DshRemote.App.Views;

public partial class MainView : UserControl
{
    private TopLevel? _topLevel;

    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplySafeArea();
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.BackRequested += OnBackRequested;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel = null;
        }
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        // Chat page → back to the session list; list/connect pages let the platform close the app.
        if (DataContext is MainViewModel viewModel && viewModel.HandleBackRequest())
        {
            e.Handled = true;
        }
    }

    private void ApplySafeArea()
    {
        // Heads may report notch/status-bar insets through PlatformServices; zero when unsupported.
        var insets = PlatformIntegration.SafeAreaPadding;
        RootGrid.Margin = insets;
    }
}
