using System.Diagnostics;
using System.Reflection;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for the workspace picker's running indicator: a workspace shows the green dot when at
/// least one of its visible sessions is running, and the dot clears once that session is no longer running.
/// </summary>
/// <remarks>
/// The dot itself is a XAML binding to <see cref="WorkspaceGroupViewModel.HasRunningSessions"/>; these tests
/// pin the projection so the picker cannot silently start showing (or stop showing) the marker for the
/// wrong workspaces. The demo data mirrors the reported setup: one running session in the DSH Remote
/// workspace, next to workspaces whose sessions are all idle.
/// </remarks>
[Collection(AppHostStateCollection.Name)]
public sealed class WorkspaceRunningIndicatorTests : IDisposable
{
    private const string DshRemoteWorkspaceId = "ws-demo-dshremote";
    private const string RunningSessionId = "sess-demo-a1b2c3";
    private const string ArcWorkspaceId = "ws-demo-arc";
    private const string BillsWorkspaceId = "ws-demo-bills";

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "dshremote-app-tests-" + Guid.NewGuid().ToString("N"));

    public WorkspaceRunningIndicatorTests()
    {
        Directory.CreateDirectory(_stateDirectory);
        AppHost.StateDirectoryProvider = () => _stateDirectory;
    }

    public void Dispose()
    {
        AppHost.StateDirectoryProvider = null;
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch
        {
            // best effort: the test result is what matters.
        }
    }

    [Fact]
    public async Task WorkspaceWithARunningSession_ShowsTheIndicator()
    {
        await using var vm = await CreateConnectedAsync();

        var dshRemote = vm.WorkspaceChoices.Single(group => group.WorkspaceId == DshRemoteWorkspaceId);
        Assert.Contains(dshRemote.Sessions, session => session.SessionId == RunningSessionId);
        Assert.True(dshRemote.HasRunningSessions);

        Assert.False(vm.WorkspaceChoices.Single(group => group.WorkspaceId == ArcWorkspaceId).HasRunningSessions);
        Assert.False(vm.WorkspaceChoices.Single(group => group.WorkspaceId == BillsWorkspaceId).HasRunningSessions);
        Assert.False(vm.WorkspaceChoices.Single(group => group.IsUngrouped).HasRunningSessions);
    }

    [Fact]
    public async Task IndicatorClears_AfterTheLastRunningSessionStops()
    {
        await using var vm = await CreateConnectedAsync();

        var dshRemote = vm.WorkspaceChoices.Single(group => group.WorkspaceId == DshRemoteWorkspaceId);
        Assert.True(dshRemote.HasRunningSessions);

        // Archiving the running session stops it (the demo client mirrors the host's stop-on-archive).
        await ArchiveAsync(vm, RunningSessionId);

        dshRemote = vm.WorkspaceChoices.Single(group => group.WorkspaceId == DshRemoteWorkspaceId);
        Assert.False(dshRemote.HasSessions);
        Assert.False(dshRemote.HasRunningSessions);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static async Task<MainViewModel> CreateConnectedAsync()
    {
        var vm = new MainViewModel();
        vm.DemoPreviewCommand.Execute(null);
        await WaitUntilAsync(
            () => vm.IsConnected && vm.WorkspaceChoices.Count > 0 && !vm.IsListingSessions,
            "demo connection + workspace list");
        return vm;
    }

    private static Task ArchiveAsync(MainViewModel vm, string sessionId)
    {
        var item = vm.Sessions.Single(session => session.SessionId == sessionId);
        return InvokeAsync(vm, "ArchiveSessionAsync", item);
    }

    private static Task InvokeAsync(MainViewModel vm, string methodName, params object?[] arguments)
    {
        var method = typeof(MainViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"{typeof(MainViewModel).Name}.{methodName} was not found");
        return (Task)(method.Invoke(vm, arguments)
                      ?? throw new InvalidOperationException($"{methodName} returned null"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description, int timeoutMs = 15_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Timed out waiting for: {description}");
            }

            await Task.Delay(20);
        }
    }
}
