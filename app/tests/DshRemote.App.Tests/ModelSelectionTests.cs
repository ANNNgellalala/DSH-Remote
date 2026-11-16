using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using DshRemote.Core;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// State-machine tests for the per-session model / reasoning-effort picker: header chip text, lazy catalog
/// load, the provider → model → effort cascade, latest-wins submission, failure rollback with Chinese
/// messages, degraded (unsupported) desktops, and durable <c>model/selection</c> sync.
/// </summary>
public sealed class ModelSelectionTests
{
    private static readonly DshModelSelection FlashHigh =
        new("deepseek-official", "deepseek-v4.1-flash", "high");

    [Fact]
    public async Task ChipText_FallsBackToRawIds_UntilCatalogLoads()
    {
        var client = SupportedClient();
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        // No catalog yet: raw model id + raw effort id.
        Assert.Equal("deepseek-v4.1-flash · high", session.ModelChipText);

        await session.EnsureModelCatalogAsync();

        Assert.Equal("V4.1 Flash · 高", session.ModelChipText);
    }

    [Fact]
    public async Task ChipText_NoServerSelection_UsesCatalogDefaultWithoutWritingBack()
    {
        var client = SupportedClient();
        await using var session = CreateSession(client, Summary(selection: null, model: null));

        Assert.Equal("选择模型", session.ModelChipText);

        await session.EnsureModelCatalogAsync();

        // Display-only fallback to the catalog default: no selectModel call is made.
        Assert.Equal("V4.1 Flash · 高", session.ModelChipText);
        Assert.Empty(client.Selections);
    }

    [Fact]
    public async Task MissingModelClient_DisablesPicker()
    {
        var client = new ScriptedClient();
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        Assert.False(session.IsModelPickerAvailable);
        Assert.False(session.CanOpenModelPicker);
        Assert.Equal("当前桌面 DSH 不支持切换模型", session.ModelChipHint);

        session.IsModelPickerOpen = true;
        Assert.False(session.IsModelPickerOpen);
    }

    [Fact]
    public async Task DemoClient_ExposesCatalogAndPersistsSelection()
    {
        await using var demo = new DemoDshClient();

        var catalog = await demo.GetModelCatalogAsync();
        Assert.Equal(DshModelCatalogSupport.Supported, catalog.Support);
        Assert.NotNull(catalog.Catalog);
        Assert.Equal(2, catalog.Catalog!.Groups.Count);
        Assert.Equal("deepseek-official", catalog.Catalog.Groups[0].Id);

        // No effort given: the provider default is materialized into the returned selection.
        var flash = await demo.SelectModelAsync("sess-demo-a1b2c3", "deepseek-official", "deepseek-v4.1-flash");
        Assert.Equal("high", flash.ReasoningEffort);

        var sessions = await demo.ListSessionsAsync();
        var updated = sessions.Single(session => session.SessionId == "sess-demo-a1b2c3");
        Assert.Equal("deepseek-v4.1-flash", updated.Model);
        Assert.NotNull(updated.ModelSelection);
        Assert.Equal("high", updated.ModelSelection!.ReasoningEffort);

        // A model without reasoning commits with no effort.
        var coder = await demo.SelectModelAsync("sess-demo-a1b2c3", "opencode-go", "qwen3-coder");
        Assert.Null(coder.ReasoningEffort);

        // Passing an effort to a non-reasoning model is rejected like the real host does.
        var invalid = await Assert.ThrowsAsync<DshRemoteException>(
            () => demo.SelectModelAsync("sess-demo-a1b2c3", "opencode-go", "qwen3-coder", "high"));
        Assert.Equal("session/model-unavailable", invalid.Code);
    }

    [Fact]
    public async Task UnsupportedCatalog_DisablesEntry()
    {
        var client = new ScriptedModelClient
        {
            CatalogResult = new DshModelCatalogResult(null, DshModelCatalogSupport.Unsupported, "method unavailable")
        };
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        // Optimistic before the probe.
        Assert.True(session.IsModelPickerAvailable);

        await session.EnsureModelCatalogAsync();

        Assert.False(session.IsModelPickerAvailable);
        Assert.False(session.CanOpenModelPicker);
        Assert.Equal("当前桌面 DSH 不支持切换模型", session.ModelChipHint);
        session.IsModelPickerOpen = true;
        Assert.False(session.IsModelPickerOpen);
    }

    [Fact]
    public async Task Running_DisablesAndClosesPicker()
    {
        var client = SupportedClient();
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.OpenModelPickerAsync();
        Assert.True(session.IsModelPickerOpen);

        session.IsRunning = true;

        Assert.False(session.CanOpenModelPicker);
        Assert.Equal("运行中不可切换模型", session.ModelChipHint);
        Assert.False(session.IsModelPickerOpen);

        session.IsRunning = false;

        Assert.True(session.CanOpenModelPicker);
        Assert.Equal(string.Empty, session.ModelChipHint);
    }

    [Fact]
    public async Task Cascade_ProviderModelEffort_AndBackNavigation()
    {
        var client = SupportedClient();
        var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.OpenModelPickerAsync();

        Assert.Equal(string.Empty, session.ModelChipHint);
        Assert.Equal(0, session.ModelPickerLevel);
        Assert.True(session.IsModelProviderLevel);
        Assert.False(session.CanGoBackModelPicker);
        Assert.Equal("选择模型", session.ModelPickerTitle);
        Assert.Equal(2, session.ModelProviders.Count);
        Assert.Equal("DeepSeek 官方", session.ModelProviders[0].Name);
        Assert.Equal("2 个模型", session.ModelProviders[0].Detail);
        Assert.True(session.ModelProviders[0].HasDetail);

        session.SelectProviderRow(session.ModelProviders[0]);

        Assert.Equal(1, session.ModelPickerLevel);
        Assert.True(session.IsModelLevel);
        Assert.True(session.CanGoBackModelPicker);
        Assert.Equal("DeepSeek 官方", session.ModelPickerTitle);
        Assert.Equal(2, session.ModelModels.Count);

        var flash = session.ModelModels.Single(model => model.Id == "deepseek-v4.1-flash");
        Assert.True(flash.HasReasoning);
        Assert.True(flash.IsDrillable);
        Assert.True(flash.IsSelected);
        Assert.True(flash.HasDetail);
        Assert.Equal("快速模型，适合日常改码与问答。", flash.Detail);
        Assert.False(session.ModelModels.Single(model => model.Id == "deepseek-v4.1-pro").IsDrillable);

        await session.SelectDescriptorRowAsync(flash);

        Assert.Equal(2, session.ModelPickerLevel);
        Assert.True(session.IsModelEffortLevel);
        Assert.Equal("选择推理等级", session.ModelPickerTitle);
        Assert.Equal(new[] { "低", "中", "高" }, session.ModelEfforts.Select(effort => effort.Name).ToArray());
        Assert.Equal("高", session.ModelEfforts.Single(effort => effort.IsSelected).Name);
        Assert.All(session.ModelEfforts, effort => Assert.False(effort.IsDefault));

        session.ModelPickerBackCommand.Execute(null);
        Assert.Equal(1, session.ModelPickerLevel);

        session.ModelPickerBackCommand.Execute(null);
        Assert.Equal(0, session.ModelPickerLevel);
        Assert.False(session.CanGoBackModelPicker);

        // Back at the root is a no-op.
        session.ModelPickerBackCommand.Execute(null);
        Assert.Equal(0, session.ModelPickerLevel);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SelectingEffort_CommitsClosesAndUpdatesChip()
    {
        var client = SupportedClient();
        var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.OpenModelPickerAsync();
        session.SelectProviderRow(session.ModelProviders[0]);
        await session.SelectDescriptorRowAsync(session.ModelModels[0]);

        var medium = session.ModelEfforts.Single(effort => effort.Id == "medium");
        await session.SelectEffortRowAsync(medium);

        var call = Assert.Single(client.Selections);
        Assert.Equal(("deepseek-official", "deepseek-v4.1-flash", (string?)"medium"), call);
        Assert.Equal("V4.1 Flash · 中", session.ModelChipText);
        Assert.False(session.IsModelPickerOpen);
        Assert.False(session.IsModelSelecting);
        Assert.False(session.HasModelCatalogError);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SelectingModelWithoutReasoning_CommitsDirectly()
    {
        var client = SupportedClient();
        var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.OpenModelPickerAsync();
        session.SelectProviderRow(session.ModelProviders[0]);

        var pro = session.ModelModels.Single(model => model.Id == "deepseek-v4.1-pro");
        await session.SelectDescriptorRowAsync(pro);

        var call = Assert.Single(client.Selections);
        Assert.Equal("deepseek-official", call.Provider);
        Assert.Equal("deepseek-v4.1-pro", call.Model);
        Assert.Null(call.Effort);
        Assert.Equal("V4.1 Pro", session.ModelChipText);
        Assert.False(session.IsModelPickerOpen);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task SelectFailure_KeepsOldSelectionAndShowsChineseError()
    {
        var client = SupportedClient();
        client.SelectHandler = (_, _, _) => throw new DshRemoteException(
            "session/model-unavailable",
            "Select an available model before sending a message.");
        var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.OpenModelPickerAsync();
        session.SelectProviderRow(session.ModelProviders.Single(provider => provider.Id == "opencode-go"));
        await session.SelectDescriptorRowAsync(session.ModelModels[0]);

        Assert.Equal("V4.1 Flash · 高", session.ModelChipText);
        Assert.True(session.HasModelCatalogError);
        Assert.Contains("该模型当前不可用", session.ModelCatalogError);
        Assert.True(session.IsModelPickerOpen);
        Assert.False(session.IsModelSelecting);

        await session.DisposeAsync();
    }

    [Theory]
    [InlineData("session/writer-held", "当前会话被其他 DSH 进程占用")]
    [InlineData("gateway/endpoint-unavailable", "当前桌面 DSH 版本不支持切换模型")]
    [InlineData("gateway/transport", "连接已断开")]
    public async Task SelectionErrors_MapToChineseMessages(string code, string expected)
    {
        var client = SupportedClient();
        client.SelectHandler = (_, _, _) => throw new DshRemoteException(code, "boom");
        var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.ApplySelectionAsync("opencode-go", "qwen3-coder", null);

        Assert.Contains(expected, session.ModelCatalogError);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task StaleSelectionResponse_DoesNotOverrideNewerSelection()
    {
        var first = new TaskCompletionSource<DshModelSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<DshModelSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = SupportedClient();
        client.SelectHandler = (provider, _, _) =>
            provider == "deepseek-official" ? first.Task : second.Task;
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));
        await session.EnsureModelCatalogAsync();

        var slow = session.ApplySelectionAsync("deepseek-official", "deepseek-v4.1-flash", "low");
        var latest = session.ApplySelectionAsync("opencode-go", "qwen3-coder", null);

        second.SetResult(new DshModelSelection("opencode-go", "qwen3-coder", null));
        await latest;
        Assert.Equal("Qwen3 Coder", session.ModelChipText);

        // The stale first response lands after the newer one and must be discarded.
        first.SetResult(new DshModelSelection("deepseek-official", "deepseek-v4.1-flash", "low"));
        await slow;

        Assert.Equal("Qwen3 Coder", session.ModelChipText);
        Assert.False(session.IsModelSelecting);
    }

    [Fact]
    public async Task ModelSelectionEvent_UpdatesChip()
    {
        var client = SupportedClient();
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));
        await session.EnsureModelCatalogAsync();

        session.ApplyModelSelectionEvent(Event(
            "model/selection",
            "{\"provider\":\"opencode-go\",\"model\":\"qwen3-coder\",\"reasoningEffort\":null}"));

        Assert.Equal("Qwen3 Coder", session.ModelChipText);
        Assert.False(session.HasModelCatalogError);
    }

    [Fact]
    public async Task BaselineCalibration_FromFollowSnapshot_UpdatesChip()
    {
        var client = SupportedClient();
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));
        await session.EnsureModelCatalogAsync();

        session.ApplyModelSelectionBaseline(new DshModelSelection("opencode-go", "qwen3-coder", null));

        Assert.Equal("Qwen3 Coder", session.ModelChipText);
    }

    [Fact]
    public async Task UnavailableCatalog_ShowsRetryError_AndForceRefreshRecovers()
    {
        var client = new ScriptedModelClient
        {
            CatalogResult = new DshModelCatalogResult(null, DshModelCatalogSupport.Unavailable, "relay timeout")
        };
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.OpenModelPickerAsync();

        Assert.True(session.IsModelPickerAvailable);
        Assert.True(session.HasModelCatalogError);
        Assert.Contains("模型目录加载失败", session.ModelCatalogError);
        Assert.Empty(session.ModelProviders);

        client.CatalogResult = Supported(CreateCatalog());
        await session.EnsureModelCatalogAsync(forceRefresh: true);

        Assert.False(session.HasModelCatalogError);
        Assert.Equal(2, session.ModelProviders.Count);
        Assert.Equal(2, client.CatalogCalls);
    }

    [Fact]
    public async Task CatalogWarnings_ListFailingProviders()
    {
        var client = SupportedClient();
        await using var session = CreateSession(client, Summary(FlashHigh, "deepseek-v4.1-flash"));

        await session.EnsureModelCatalogAsync();

        var warning = Assert.Single(session.ModelPickerWarnings);
        Assert.Equal("DeepSeek 账号 加载失败：未配置 API key", warning);
    }

    [Fact]
    public async Task EffortLevel_WithoutDefaultEffort_OffersDefaultRow()
    {
        var client = SupportedClient();
        var session = CreateSession(
            client,
            Summary(new DshModelSelection("opencode-go", "qwen3-coder-xl", null), "qwen3-coder-xl"));

        await session.OpenModelPickerAsync();
        session.SelectProviderRow(session.ModelProviders.Single(provider => provider.Id == "opencode-go"));

        var xl = session.ModelModels.Single(model => model.Id == "qwen3-coder-xl");
        Assert.True(xl.IsDrillable);
        await session.SelectDescriptorRowAsync(xl);

        Assert.Equal(2, session.ModelEfforts.Count);
        var defaultRow = session.ModelEfforts[0];
        Assert.True(defaultRow.IsDefault);
        Assert.Null(defaultRow.Id);
        Assert.Equal("默认（由提供方决定）", defaultRow.Name);
        Assert.True(defaultRow.IsSelected);
        Assert.Equal("低", session.ModelEfforts[1].Name);

        await session.SelectEffortRowAsync(defaultRow);

        var call = Assert.Single(client.Selections);
        Assert.Null(call.Effort);
        Assert.Equal("Qwen3 Coder XL", session.ModelChipText);

        await session.DisposeAsync();
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------------------------------------

    private static SessionViewModel CreateSession(IDshClient client, SessionSummary summary)
        => new(
            client,
            summary,
            new RelayCommand(() => { }),
            new InteractionHub(),
            new SettingsViewModel());

    private static SessionSummary Summary(DshModelSelection? selection, string? model)
        => new(
            "sess-model",
            "模型选择测试",
            @"D:\Code\VibeCoding\DSHRemote",
            model,
            Running: false,
            AgentAvailable: true,
            Blank: false,
            UpdatedAt: 0,
            LastPromptAt: 0,
            ModelSelection: selection);

    private static ScriptedModelClient SupportedClient()
        => new() { CatalogResult = Supported(CreateCatalog()) };

    private static DshModelCatalogResult Supported(DshModelCatalog catalog)
        => new(catalog, DshModelCatalogSupport.Supported, null);

    private static DshModelCatalog CreateCatalog()
        => new(
            new DshModelSelection("deepseek-official", "deepseek-v4.1-flash", "high"),
            new[] { "deepseek-official", "opencode-go" },
            new[]
            {
                new DshModelGroup(
                    "deepseek-official",
                    "DeepSeek 官方",
                    new[]
                    {
                        new DshModelDescriptor(
                            "deepseek-v4.1-flash",
                            "V4.1 Flash",
                            "快速模型，适合日常改码与问答。",
                            new DshModelReasoning(
                                new[]
                                {
                                    new DshModelEffort("low", "低", "更快，适合简单改动。"),
                                    new DshModelEffort("medium", "中", "速度与质量平衡。"),
                                    new DshModelEffort("high", "高", "更充分的推理，适合复杂任务。")
                                },
                                "high"),
                            new DshModelBadge("推荐", "info")),
                        new DshModelDescriptor(
                            "deepseek-v4.1-pro",
                            "V4.1 Pro",
                            null,
                            null,
                            null)
                    }),
                new DshModelGroup(
                    "opencode-go",
                    "OpenCode Go",
                    new[]
                    {
                        new DshModelDescriptor("qwen3-coder", "Qwen3 Coder", null, null, null),
                        new DshModelDescriptor(
                            "qwen3-coder-xl",
                            "Qwen3 Coder XL",
                            null,
                            new DshModelReasoning(
                                new[] { new DshModelEffort("low", "低", null) },
                                DefaultEffort: null),
                            null)
                    })
            },
            new[] { new DshModelFailure("deepseek-account", "DeepSeek 账号", "未配置 API key") });

    private static SessionEventRecord Event(string type, string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new SessionEventRecord(
            Seq: 1,
            Time: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type: type,
            Data: document.RootElement.Clone(),
            RawJson: dataJson);
    }

    /// <summary>Minimal host (no <see cref="IModelClient"/>): history/state are empty, follow stays open.</summary>
    private class ScriptedClient : IDshClient, ISessionFollowClient, IHistoryPagingClient
    {
        private readonly Channel<SessionEventRecord> _live = Channel.CreateUnbounded<SessionEventRecord>();

        public DshConnectionState State => DshConnectionState.Connected;

        public DshPairingInfo? Pairing => null;

#pragma warning disable CS0067 // connection state changes are not scripted
        public event EventHandler<DshConnectionState>? StateChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(DshPairingInfo pairing, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionSummary>>(Array.Empty<SessionSummary>());

        public Task<SessionHistoryPage> GetHistoryAsync(
            string sessionId,
            long? beforeSeq = null,
            int maxMessages = 80,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPage());

        public Task<SessionHistoryPage> GetLatestHistoryAsync(
            string sessionId,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPage());

        public Task<SessionHistoryPage> GetOlderHistoryAsync(
            string sessionId,
            long beforeSeq,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPage());

        public Task<SessionLiteSnapshot> OpenSessionLiteAsync(
            string sessionId,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLiteSnapshot(EmptyPage(), StateSnapshot()));

        public Task<SessionSnapshot> OpenSessionAsync(
            string sessionId,
            int maxMessages = 80,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionSnapshot(
                Array.Empty<SessionEventRecord>(),
                0,
                false,
                StateSnapshot(),
                ModelSelection: null));

        public Task<SessionRuntimeState> GetStateAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(StateSnapshot());

        public Task<string> SendPromptAsync(
            string sessionId,
            string text,
            bool steer = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult("req-1");

        public Task<string> SendPromptAsync(
            string sessionId,
            string text,
            IReadOnlyList<DshImageAttachment>? images,
            bool steer = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult("req-1");

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<SessionEventRecord> ObserveEventsAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var record in _live.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }

        public IAsyncEnumerable<SessionEventRecord> FollowEventsAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
            => ObserveEventsAsync(sessionId, cancellationToken);

        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateQueueAsync(
            string sessionId,
            string itemId,
            QueueEditAction action,
            string? text = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static SessionHistoryPage EmptyPage()
            => new(Array.Empty<SessionEventRecord>(), 0, false, false, null);

        private static SessionRuntimeState StateSnapshot()
            => new(0, Active: true, Running: false, Array.Empty<QueuedPrompt>(), Array.Empty<string>());
    }

    /// <summary>Scripted catalog + selectModel host used by the picker state-machine tests.</summary>
    private sealed class ScriptedModelClient : ScriptedClient, IModelClient
    {
        private readonly object _gate = new();

        public DshModelCatalogResult CatalogResult { get; set; } =
            new(null, DshModelCatalogSupport.Unavailable, "not configured");

        public Func<string, string, string?, Task<DshModelSelection>>? SelectHandler { get; set; }

        public List<(string Provider, string Model, string? Effort)> Selections { get; } = new();

        public int CatalogCalls { get; private set; }

        public Task<DshModelCatalogResult> GetModelCatalogAsync(
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                CatalogCalls++;
            }

            return Task.FromResult(CatalogResult);
        }

        public Task<DshModelSelection> SelectModelAsync(
            string sessionId,
            string provider,
            string model,
            string? reasoningEffort = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Selections.Add((provider, model, reasoningEffort));
            }

            return SelectHandler is not null
                ? SelectHandler(provider, model, reasoningEffort)
                : Task.FromResult(new DshModelSelection(provider, model, reasoningEffort));
        }
    }
}

