using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Arc.Avalonia.Theme;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using DshRemote.App.Views;
using DshRemote.Core;

namespace DshRemote.App;

public class App : Application
{
    private MainViewModel? _viewModel;
    private DispatcherTimer? _diagnosticsHeartbeat;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Crash/hang diagnostics ship with the app: the next report can carry a breadcrumb trail and a
        // managed stack instead of "the app froze". The log is adb-pullable on Android.
        DiagnosticsLog.Install();
        _diagnosticsHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagnosticsHeartbeat.Tick += (_, _) => DiagnosticsLog.Heartbeat();
        _diagnosticsHeartbeat.Start();
        DiagnosticsLog.Heartbeat();
        DiagnosticsLog.Trace("app: framework initialization completed");

        // Dark scheme + Violet accent, matching the Arc probe and the sibling projects.
        ArcThemeManager.Apply(this, ArcColorScheme.Dark, ArcAccent.Violet);

        // The real official-protocol client lives in DshRemote.Core. The demo client is only used when
        // the user explicitly asks for it (or when the real client cannot be constructed).
        AppHost.ClientFactory ??= static () => new DshClient();

        _viewModel = new MainViewModel();

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = _viewModel };
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = _viewModel };
            desktop.Exit += (_, _) => DisposeViewModel();
        }

        // Restore pairing from the platform secret store and attach deep-link / notification handlers.
        _ = _viewModel.InitializePlatformAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private void DisposeViewModel()
    {
        _diagnosticsHeartbeat?.Stop();
        _diagnosticsHeartbeat = null;
        DiagnosticsLog.Trace("app: disposing view model");
        // Best effort: the process is exiting, so never block the UI thread on a network goodbye.
        _ = _viewModel?.DisposeAsync();
        _viewModel = null;
    }
}
