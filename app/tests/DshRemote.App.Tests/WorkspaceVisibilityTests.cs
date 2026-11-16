using System.Diagnostics;
using System.Reflection;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for requirement "show every current workspace, even when all of its sessions are
/// archived". The old grouping dropped a workspace from the picker as soon as it had no visible session,
/// which made it impossible to select that workspace and create a new session there.
/// </summary>
[Collection(AppHostStateCollection.Name)]
public sealed class WorkspaceVisibilityTests : IDisposable
{
    private const string DshRemoteWorkspaceId = "ws-demo-dshremote";
    private const string ArcWorkspaceId = "ws-demo-arc";
    private const string BillsWorkspaceId = "ws-demo-bills";
    private const string ArcOnlySessionId = "sess-demo-d4e5f6";

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "dshremote-app-tests-" + Guid.NewGuid().ToString("N"));

    public WorkspaceVisibilityTests()
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
    public async Task ArchivingTheOnlySession_KeepsEveryWorkspaceInThePicker()
    {
        await using var vm = await CreateConnectedAsync();

        // Stay on another workspace so the archived one is not kept merely because it is the current one.
        vm.SelectedWorkspaceGroup = vm.WorkspaceChoices.Single(group => group.WorkspaceId == DshRemoteWorkspaceId);
        await ArchiveAsync(vm, ArcOnlySessionId);

        // Before the fix the now-empty workspace vanished from the picker entirely.
        var workspaceGroups = vm.WorkspaceChoices.Where(group => !group.IsUngrouped).ToList();
        Assert.Equal(3, workspaceGroups.Count);
        var arc = workspaceGroups.Single(group => group.WorkspaceId == ArcWorkspaceId);
        Assert.Empty(arc.Sessions);
        Assert.True(arc.CanCreateSession);
    }

    [Fact]
    public async Task ArchivingTheLastSessionOfANonSelectedWorkspace_KeepsItSelectable()
    {
        await using var vm = await CreateConnectedAsync();

        // Sit on another workspace so the archived one is not kept merely because it is the current one.
        var bills = vm.WorkspaceChoices.Single(group => group.WorkspaceId == BillsWorkspaceId);
        vm.SelectedWorkspaceGroup = bills;

        await ArchiveAsync(vm, ArcOnlySessionId);

        // Before the fix the now-empty workspace vanished from the picker entirely.
        var arc = vm.WorkspaceChoices.SingleOrDefault(group => group.WorkspaceId == ArcWorkspaceId);
        Assert.NotNull(arc);
        Assert.Empty(arc!.Sessions);
        Assert.True(arc.CanCreateSession);

        vm.SelectedWorkspaceGroup = arc;
        Assert.Equal(ArcWorkspaceId, vm.SelectedWorkspaceGroup?.WorkspaceId);
        Assert.True(vm.IsSessionsEmpty);
        Assert.Equal("暂无会话", vm.SessionsSummary);
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
