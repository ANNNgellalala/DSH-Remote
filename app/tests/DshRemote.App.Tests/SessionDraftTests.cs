using System.Diagnostics;
using System.Reflection;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for requirement "keep the unsent composer input of a workspace's new session".
/// Opening a chat builds a new <see cref="SessionViewModel"/>, so the draft must live in the shell-owned
/// <see cref="SessionDraftStore"/>; tapping 「＋ 新建会话」 again must reopen the still-blank session instead
/// of creating another empty one.
/// </summary>
[Collection(AppHostStateCollection.Name)]
public sealed class SessionDraftTests : IDisposable
{
    private const string DshRemoteWorkspaceId = "ws-demo-dshremote";
    private const string ArcWorkspaceId = "ws-demo-arc";
    private const string OtherSessionId = "sess-demo-d4e5f6";

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "dshremote-app-tests-" + Guid.NewGuid().ToString("N"));

    public SessionDraftTests()
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
    public void Store_KeepsTextPerSessionAndClearsOnBlank()
    {
        var store = new SessionDraftStore();
        store.Set("session-a", "hello");
        store.Set("session-b", "world");

        Assert.Equal("hello", store.Get("session-a"));
        Assert.Equal("world", store.Get("session-b"));
        Assert.Equal(2, store.Count);

        store.Set("session-a", "   ");
        Assert.Equal(string.Empty, store.Get("session-a"));
        Assert.Equal(1, store.Count);

        store.Clear("session-b");
        Assert.Equal(string.Empty, store.Get("session-b"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Store_RoundTripsDraftsThroughTheCache()
    {
        var root = Path.Combine(_stateDirectory, "cache");
        var cache = new SessionCacheStore(root, "desktop-key");
        var store = new SessionDraftStore();
        store.Attach(cache);
        store.Set("session-a", "换行\n草稿");
        store.Flush();

        var reopened = new SessionDraftStore();
        reopened.Attach(new SessionCacheStore(root, "desktop-key"));

        Assert.Equal("换行\n草稿", reopened.Get("session-a"));
    }

    [Fact]
    public void Store_DoesNotLeakDraftsBetweenDesktops()
    {
        // Both saved desktops share the cache root; the documents must be namespaced by desktop key.
        var root = Path.Combine(_stateDirectory, "cache");
        var store = new SessionDraftStore();
        store.Attach(new SessionCacheStore(root, "desktop-one"));
        store.Set("session-a", "one");
        store.Flush();

        store.Attach(new SessionCacheStore(root, "desktop-two"));
        Assert.Equal(string.Empty, store.Get("session-a"));

        store.Set("session-a", "two");
        store.Flush();

        var back = new SessionDraftStore();
        back.Attach(new SessionCacheStore(root, "desktop-one"));
        Assert.Equal("one", back.Get("session-a"));
    }

    [Fact]
    public async Task SwitchingChats_RestoresTheUnsentDraft()
    {
        await using var vm = await CreateConnectedAsync();

        await OpenAsync(vm, DshRemoteWorkspaceId, "sess-demo-a1b2c3");
        Assert.NotNull(vm.CurrentSession);
        vm.CurrentSession!.Draft = "还没发送的草稿";

        await OpenAsync(vm, ArcWorkspaceId, "sess-demo-d4e5f6");
        Assert.Equal(string.Empty, vm.CurrentSession!.Draft);

        await OpenAsync(vm, DshRemoteWorkspaceId, "sess-demo-a1b2c3");
        Assert.Equal("还没发送的草稿", vm.CurrentSession!.Draft);
    }

    [Fact]
    public async Task CreatingANewSessionAgain_ReopensTheBlankSessionWithItsDraft()
    {
        await using var vm = await CreateConnectedAsync();

        var group = vm.WorkspaceChoices.Single(item => item.WorkspaceId == DshRemoteWorkspaceId);
        await InvokeAsync(vm, "CreateSessionInGroupAsync", group);

        var blankSessionId = vm.CurrentSession?.SessionId;
        Assert.False(string.IsNullOrEmpty(blankSessionId));
        vm.CurrentSession!.Draft = "新建会话里打了一半的内容";

        // Switch to another chat and come back through the same 「＋ 新建会话」 action.
        await OpenAsync(vm, ArcWorkspaceId, OtherSessionId);
        var refreshedGroup = vm.WorkspaceChoices.Single(item => item.WorkspaceId == DshRemoteWorkspaceId);
        await InvokeAsync(vm, "CreateSessionInGroupAsync", refreshedGroup);

        Assert.Equal(blankSessionId, vm.CurrentSession?.SessionId);
        Assert.Equal("新建会话里打了一半的内容", vm.CurrentSession!.Draft);
        Assert.Single(refreshedGroup.Sessions, session => session.IsBlank);
    }

    [Fact]
    public async Task AfterSendingInTheBlankSession_NewSessionCreatesAFreshOne()
    {
        await using var vm = await CreateConnectedAsync();

        var group = vm.WorkspaceChoices.Single(item => item.WorkspaceId == DshRemoteWorkspaceId);
        await InvokeAsync(vm, "CreateSessionInGroupAsync", group);

        var firstBlankId = vm.CurrentSession?.SessionId;
        Assert.False(string.IsNullOrEmpty(firstBlankId));

        vm.CurrentSession!.Draft = "第一条消息";
        await SendAsync(vm.CurrentSession);
        Assert.Equal(string.Empty, vm.CurrentSession!.Draft);

        // The demo host keeps reporting the old snapshot as blank; the shell's local "already used"
        // marker must still make 「＋ 新建会话」 create a genuinely new session.
        await InvokeAsync(vm, "RefreshSessionsAsync");
        var refreshedGroup = vm.WorkspaceChoices.Single(item => item.WorkspaceId == DshRemoteWorkspaceId);
        await InvokeAsync(vm, "CreateSessionInGroupAsync", refreshedGroup);

        Assert.NotNull(vm.CurrentSession);
        Assert.NotEqual(firstBlankId, vm.CurrentSession!.SessionId);
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

    private static Task OpenAsync(MainViewModel vm, string workspaceId, string sessionId)
    {
        vm.SelectedWorkspaceGroup = vm.WorkspaceChoices.Single(group => group.WorkspaceId == workspaceId);
        var item = vm.Sessions.Single(session => session.SessionId == sessionId);
        return InvokeAsync(vm, "OpenSessionAsync", item);
    }

    /// <summary>Drives the session's private send path and awaits it (the command is fire-and-forget).</summary>
    private static Task SendAsync(SessionViewModel session)
    {
        var method = typeof(SessionViewModel).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"{nameof(SessionViewModel)}.SendAsync was not found");
        return (Task)(method.Invoke(session, null)
                      ?? throw new InvalidOperationException("SendAsync returned null"));
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
