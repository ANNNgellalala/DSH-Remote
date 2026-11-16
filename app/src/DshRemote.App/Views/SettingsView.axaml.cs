using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DshRemote.App.Services;

namespace DshRemote.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDiagnosticsPath();
    }

    private void RefreshDiagnosticsPath()
    {
        if (DiagnosticsPathText is not null)
        {
            DiagnosticsPathText.Text = DiagnosticsLog.LogPath is { Length: > 0 } path
                ? "日志文件：" + path
                : "日志文件：当前平台没有可写目录（桌面壳可在 ~/.dsh/remote 下查看）";
        }
    }

    private async void OnCopyDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = DiagnosticsLog.ReadAll();
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            DiagnosticsLog.Trace("settings: diagnostics copied to clipboard");
        }
        catch
        {
            // Clipboard is best effort (some Android builds refuse while the app is not focused).
        }
    }

    private void OnClearDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Clear();
        RefreshDiagnosticsPath();
    }
}
