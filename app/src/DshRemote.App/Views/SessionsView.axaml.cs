using Avalonia;
using Avalonia.Controls;
using DshRemote.App.ViewModels;

namespace DshRemote.App.Views;

public partial class SessionsView : UserControl
{
    private const double NarrowWidthThreshold = 460;

    private MainViewModel? _viewModel;

    public SessionsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsive();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_viewModel is not null)
        {
            _viewModel.WorkspaceSelectionChanged -= OnWorkspaceSelectionChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.WorkspaceSelectionChanged += OnWorkspaceSelectionChanged;
        }

        UpdateResponsive();
    }

    private void OnWorkspaceSelectionChanged(object? sender, EventArgs e)
    {
        // Choosing a workspace resets the list to the top.
        SessionScroll.Offset = new Vector(0, 0);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is not null)
        {
            _viewModel.WorkspaceSelectionChanged -= OnWorkspaceSelectionChanged;
            _viewModel = null;
        }
    }

    private void UpdateResponsive()
    {
        // Narrow phones drop the model column so the title/cwd keep their space.
        var narrow = Bounds.Width > 0 && Bounds.Width < NarrowWidthThreshold;
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ShowSessionModel = !narrow;
        }
    }
}
