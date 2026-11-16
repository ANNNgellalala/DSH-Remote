using DshRemote.App.ViewModels;
using DshRemote.Core;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// The phone editor for desktop-side auto-approval as a cascading tree:
/// 全部工作区 → 工作区 → 会话. Checking a parent selects every child; clearing a
/// parent clears every child; a partially selected workspace saves only its
/// checked session ids (full workspaces also cover future sessions).
/// </summary>
public sealed class AutoApproveViewModelTests
{
    [Fact]
    public async Task CascadeRaisesIsSelectedForEveryAffectedCheckbox()
    {
        var client = new FakeAutoApproveClient();
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);

        var repo = vm.Workspaces.Single(workspace => workspace.WorkspaceId == "ws-1");
        var workspaceEvents = new List<string?>();
        var sessionEvents = new List<string?>();
        repo.PropertyChanged += (_, args) => workspaceEvents.Add(args.PropertyName);
        foreach (var session in repo.Sessions)
        {
            session.PropertyChanged += (_, args) => sessionEvents.Add(args.PropertyName);
        }

        vm.AllSelected = true;

        Assert.Contains("IsSelected", workspaceEvents);
        Assert.Contains("IsSelected", sessionEvents);
        Assert.All(repo.Sessions, session => Assert.True(session.IsSelected));

        vm.AllSelected = false;
        Assert.All(repo.Sessions, session => Assert.False(session.IsSelected));
    }

    [Fact]
    public async Task Attach_LoadsFullWorkspaceRulesIntoTheTree()
    {
        var client = new FakeAutoApproveClient
        {
            Config = new DshAutoApproveConfig(
                true,
                false,
                new[] { new DshAutoApproveWorkspace("ws-1", "D:\\Code\\repo", "Repo") },
                Array.Empty<DshAutoApproveSession>(),
                Revision: 4,
                Stats: new DshAutoApproveStats(3, 0, DateTimeOffset.Parse("2026-11-14T06:01:00Z")),
                RelayVersion: "0.7.0")
        };
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);

        Assert.True(vm.IsAvailable);
        Assert.True(vm.Enabled);
        var repo = vm.Workspaces.Single(workspace => workspace.WorkspaceId == "ws-1");
        Assert.True(repo.IsSelected);
        Assert.All(repo.Sessions, session => Assert.True(session.IsSelected));
        Assert.False(vm.Workspaces.Single(workspace => workspace.WorkspaceId == "ws-2").IsSelected);
        Assert.Null(vm.AllSelected);
        Assert.Contains("1 个工作区", vm.SummaryText, StringComparison.Ordinal);
        Assert.Contains("已自动放行 3 次", vm.StatsText, StringComparison.Ordinal);
        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task RootSelection_SelectsAndClearsEveryWorkspaceAndSession()
    {
        var client = new FakeAutoApproveClient();
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);
        vm.Enabled = true;

        vm.AllSelected = true;
        Assert.True(vm.Workspaces.All(workspace => workspace.IsSelected == true));
        Assert.All(vm.Workspaces, workspace => Assert.All(workspace.Sessions, session => Assert.True(session.IsSelected)));

        await vm.SaveNowAsync();
        var saved = Assert.Single(client.Saved);
        Assert.True(saved.All);
        Assert.Empty(saved.Workspaces);
        Assert.Empty(saved.Sessions);

        vm.AllSelected = false;
        Assert.All(vm.Workspaces, workspace => Assert.False(workspace.IsSelected == true));
        Assert.All(vm.Workspaces, workspace => Assert.All(workspace.Sessions, session => Assert.False(session.IsSelected)));
    }

    [Fact]
    public async Task WorkspaceSelection_CascadesToSessionsAndSavesAFullWorkspace()
    {
        var client = new FakeAutoApproveClient();
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);
        vm.Enabled = true;

        var repo = vm.Workspaces.Single(workspace => workspace.WorkspaceId == "ws-1");
        repo.IsSelected = true;
        Assert.All(repo.Sessions, session => Assert.True(session.IsSelected));
        Assert.Null(vm.AllSelected);

        await vm.SaveNowAsync();
        var saved = Assert.Single(client.Saved);
        Assert.False(saved.All);
        Assert.Equal("ws-1", Assert.Single(saved.Workspaces).WorkspaceId);
        Assert.Empty(saved.Sessions);

        repo.IsSelected = false;
        Assert.All(repo.Sessions, session => Assert.False(session.IsSelected));
    }

    [Fact]
    public async Task UncheckingOneSessionTurnsTheWorkspacePartialAndSavesOnlyThatSessionSet()
    {
        var client = new FakeAutoApproveClient
        {
            Config = new DshAutoApproveConfig(
                true,
                false,
                new[] { new DshAutoApproveWorkspace("ws-1", "D:\\Code\\repo", "Repo") },
                Array.Empty<DshAutoApproveSession>())
        };
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);

        var repo = vm.Workspaces.Single(workspace => workspace.WorkspaceId == "ws-1");
        repo.Sessions.Single(session => session.SessionId == "session-1").IsSelected = false;

        Assert.Null(repo.IsSelected);
        Assert.Null(vm.AllSelected);

        await vm.SaveNowAsync();
        var saved = Assert.Single(client.Saved);
        Assert.False(saved.All);
        Assert.Empty(saved.Workspaces);
        Assert.Equal("session-2", Assert.Single(saved.Sessions).SessionId);
    }

    [Fact]
    public async Task PartialSessionRules_RehydrateIntoAnIndeterminateWorkspace()
    {
        var client = new FakeAutoApproveClient
        {
            Config = new DshAutoApproveConfig(
                true,
                false,
                Array.Empty<DshAutoApproveWorkspace>(),
                new[] { new DshAutoApproveSession("session-1", "One") })
        };
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);

        var repo = vm.Workspaces.Single(workspace => workspace.WorkspaceId == "ws-1");
        Assert.Null(repo.IsSelected);
        Assert.True(repo.Sessions.Single(session => session.SessionId == "session-1").IsSelected);
        Assert.False(repo.Sessions.Single(session => session.SessionId == "session-2").IsSelected);
        Assert.Null(vm.AllSelected);
        Assert.Contains("1 个会话", vm.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingEveryCurrentWorkspaceShowsTheRootCheckedWithoutTurningOnTheAllPolicy()
    {
        var client = new FakeAutoApproveClient();
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);
        vm.Enabled = true;

        foreach (var workspace in vm.Workspaces)
        {
            workspace.IsSelected = true;
        }

        Assert.True(vm.AllSelected);
        await vm.SaveNowAsync();
        var saved = Assert.Single(client.Saved);
        Assert.False(saved.All);
        Assert.Equal(2, saved.Workspaces.Count);
    }

    [Fact]
    public async Task SaveFailure_SurfacesAnErrorAndKeepsThePreviousDesktopDocument()
    {
        var client = new FakeAutoApproveClient
        {
            SaveError = new DshRemoteException(DshRemoteException.Codes.ConnectFailed, "relay 0.7.0+ required")
        };
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);

        vm.Enabled = true;
        await vm.SaveNowAsync();

        Assert.True(vm.HasError);
        Assert.Contains("relay 0.7.0+ required", vm.ErrorText, StringComparison.Ordinal);
        Assert.Empty(client.Saved);
    }

    [Fact]
    public async Task Detach_ClearsTheTree()
    {
        var client = new FakeAutoApproveClient
        {
            Config = new DshAutoApproveConfig(true, true, Array.Empty<DshAutoApproveWorkspace>(), Array.Empty<DshAutoApproveSession>())
        };
        var vm = new AutoApproveViewModel();
        vm.Attach(client, Catalog());
        await (vm.PendingRefresh ?? Task.CompletedTask);
        Assert.True(vm.AllSelected);

        vm.Detach();

        Assert.False(vm.IsAvailable);
        Assert.False(vm.Enabled);
        Assert.Empty(vm.Workspaces);
        Assert.Contains("未连接", vm.SummaryText, StringComparison.Ordinal);
    }

    private static IReadOnlyList<WorkspaceGroupViewModel> Catalog()
    {
        var repo = new WorkspaceGroupViewModel("ws-1", "Repo", "D:\\Code\\repo");
        repo.Sessions.Add(new SessionListViewModel(
            new SessionSummary("session-1", "One", "D:\\Code\\repo", null, false, true, false, 1, 1),
            new RelayCommand(() => { })));
        repo.Sessions.Add(new SessionListViewModel(
            new SessionSummary("session-2", "Two", "D:\\Code\\repo", null, false, true, false, 2, 2),
            new RelayCommand(() => { })));

        var other = new WorkspaceGroupViewModel("ws-2", "Other", "D:\\Code\\other");
        other.Sessions.Add(new SessionListViewModel(
            new SessionSummary("session-3", "Three", "D:\\Code\\other", null, false, true, false, 3, 3),
            new RelayCommand(() => { })));

        return new[] { repo, other };
    }

    private sealed class FakeAutoApproveClient : IAutoApproveClient
    {
        public DshAutoApproveConfig Config { get; set; } = new(
            false,
            false,
            Array.Empty<DshAutoApproveWorkspace>(),
            Array.Empty<DshAutoApproveSession>());

        public List<DshAutoApproveConfig> Saved { get; } = new();

        public Exception? SaveError { get; set; }

        public Task<DshAutoApproveConfig> GetAutoApproveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Config);

        public Task<DshAutoApproveConfig> SaveAutoApproveAsync(
            DshAutoApproveConfig config,
            CancellationToken cancellationToken = default)
        {
            if (SaveError is not null)
            {
                throw SaveError;
            }

            Saved.Add(config);
            return Task.FromResult(config);
        }
    }
}
