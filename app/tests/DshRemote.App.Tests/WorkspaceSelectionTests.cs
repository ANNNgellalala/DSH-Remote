using System.Diagnostics;
using System.Reflection;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for the session-list workspace picker: archiving a session must not move the user to
/// another workspace, and the selected workspace must survive a list rebuild.
/// </summary>
/// <remarks>
/// The tests drive <see cref="MainViewModel"/> against the built-in demo client (it mirrors the reported
/// setup: a workspace whose only visible session is archived, next to a more recently updated workspace)
/// and call the private reload/archive handlers by reflection so each step can be awaited deterministically.
/// </remarks>
[Collection(AppHostStateCollection.Name)]
public sealed class WorkspaceSelectionTests : IDisposable
{
    private const string DshRemoteWorkspaceId = "ws-demo-dshremote";
    private const string DshRemoteVisibleSessionId = "sess-demo-a1b2c3";
    private const string ArcWorkspaceId = "ws-demo-arc";
    private const string UngroupedVisibleSessionId = "sess-demo-0f1e2d";

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "dshremote-app-tests-" + Guid.NewGuid().ToString("N"));

    public WorkspaceSelectionTests()
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
    public async Task ArchivingLastSessionOfCurrentWorkspace_StaysInThatWorkspace()
    {
        await using var vm = await CreateConnectedAsync();

        var workspace = vm.WorkspaceChoices.Single(group => group.WorkspaceId == DshRemoteWorkspaceId);
        vm.SelectedWorkspaceGroup = workspace;
        Assert.Equal(DshRemoteWorkspaceId, vm.Settings.LastWorkspaceId);

        await ArchiveAsync(vm, DshRemoteVisibleSessionId);

        // Before the fix the emptied workspace vanished from the picker and the list jumped to the most
        // recently updated workspace (Arc.Avalonia in the demo data).
        Assert.NotNull(vm.SelectedWorkspaceGroup);
        Assert.Equal(DshRemoteWorkspaceId, vm.SelectedWorkspaceGroup!.WorkspaceId);
        Assert.Equal("DSH Remote", vm.SelectedWorkspaceName);
        Assert.Empty(vm.SelectedWorkspaceGroup.Sessions);
        Assert.Equal(DshRemoteWorkspaceId, vm.Settings.LastWorkspaceId);
        Assert.DoesNotContain(vm.Sessions, session => session.SessionId == DshRemoteVisibleSessionId);
        Assert.Contains(vm.WorkspaceChoices, group => group.WorkspaceId == ArcWorkspaceId);
    }

    [Fact]
    public async Task ArchivingOnlySessionInUngroupedBucket_StaysOnUngrouped()
    {
        await using var vm = await CreateConnectedAsync();

        var ungrouped = vm.WorkspaceChoices.Single(group => group.IsUngrouped);
        Assert.Contains(ungrouped.Sessions, session => session.SessionId == UngroupedVisibleSessionId);
        vm.SelectedWorkspaceGroup = ungrouped;

        await ArchiveAsync(vm, UngroupedVisibleSessionId);

        Assert.NotNull(vm.SelectedWorkspaceGroup);
        Assert.True(vm.SelectedWorkspaceGroup!.IsUngrouped);
        Assert.Empty(vm.SelectedWorkspaceGroup.Sessions);
    }

    [Fact]
    public async Task TransientNullSelection_DoesNotForgetTheWorkspace()
    {
        await using var vm = await CreateConnectedAsync();

        var workspace = vm.WorkspaceChoices.Single(group => group.WorkspaceId == ArcWorkspaceId);
        vm.SelectedWorkspaceGroup = workspace;
        Assert.Equal(ArcWorkspaceId, vm.Settings.LastWorkspaceId);

        // A placeholder deselect (the picker resetting its selection while the list is being rebuilt)
        // must neither clear the current workspace nor erase the persisted preference.
        vm.SelectedWorkspaceGroup = null;

        Assert.Same(workspace, vm.SelectedWorkspaceGroup);
        Assert.Equal(ArcWorkspaceId, vm.Settings.LastWorkspaceId);
    }

    [Fact]
    public async Task RefreshingTheList_KeepsTheSelectedWorkspace()
    {
        await using var vm = await CreateConnectedAsync();

        var workspace = vm.WorkspaceChoices.Single(group => group.WorkspaceId == ArcWorkspaceId);
        vm.SelectedWorkspaceGroup = workspace;

        await RefreshAsync(vm);

        Assert.Equal(ArcWorkspaceId, vm.SelectedWorkspaceGroup?.WorkspaceId);
        Assert.Equal(ArcWorkspaceId, vm.Settings.LastWorkspaceId);
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

    private static Task RefreshAsync(MainViewModel vm) => InvokeAsync(vm, "RefreshSessionsAsync");

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
