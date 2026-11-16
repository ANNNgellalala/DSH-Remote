using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using DshRemote.Core;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for the chat transcript ordering and for the reopened-history window.
/// <para>
/// BUG A: a running turn's fold ("正在执行中") must render <em>after</em> the user prompt that started it.
/// The durable DSH log records <c>turn/start</c> <em>before</em> the <c>user/message</c> the turn opened, and
/// the client used to retire the optimistic prompt by removing it and appending the durable row at the end —
/// so the fold (anchored on the turn's first row) ended up above the prompt.
/// </para>
/// <para>
/// BUG B: <c>session/events</c> pages by a message budget, so reopening a session whose current turn is long
/// returns a page that starts in the middle of that turn; the opening prompt and any steer (插话) bubbles sit
/// before the page and were never loaded.
/// </para>
/// </summary>
public sealed class TranscriptTimelineTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task HistoryReplay_RendersPromptBeforeItsRunningTurnFold()
    {
        // Real DSH order: turn/start is appended before the user/message that opened the turn.
        var client = new ScriptedClient { Running = true };
        client.Append("turn/start", TurnStartJson(1));
        client.Append("user/message", UserJson("初始提示", "rpc-1"));
        client.Append("step/start", StepJson(1, 1));
        client.Append("assistant/message", AssistantJson(1, 1, "正在处理"));

        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        Assert.Equal(2, session.Timeline.Count);
        var prompt = Assert.IsType<ChatMessageViewModel>(session.Timeline[0]);
        Assert.True(prompt.IsUser);
        Assert.Equal("初始提示", prompt.TargetText);

        var fold = Assert.IsType<ChatTurnViewModel>(session.Timeline[1]);
        Assert.Equal(1, fold.Turn);

        // Turn banner rows are internal markers: never rendered on their own.
        Assert.DoesNotContain(
            session.Timeline.OfType<ChatMessageViewModel>(),
            message => message.IsTurnBoundary);
    }

    [Fact]
    public async Task LiveEcho_PromotesOptimisticBubbleInPlace_AndKeepsItAboveTheFold()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        session.Draft = "你好";
        await InvokeAsync(session, "SendAsync");

        var bubble = Assert.Single(session.Messages, message => message.IsUser);
        Assert.Null(bubble.Seq);
        var requestId = Assert.IsType<string>(client.LastRequestId);

        // Live stream for the submitted prompt: turn/start arrives before the durable user/message echo.
        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("你好", requestId));

        // The echo must promote the same row (no remove + append), keeping exactly one bubble on screen.
        Assert.Same(bubble, Assert.Single(session.Messages, message => message.IsUser));
        Assert.Equal(2, bubble.Seq);
        Assert.False(bubble.IsOptimistic);
        Assert.Equal(1, session.Messages.Count(message => message.IsUser));

        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "收到"));

        Assert.Equal(2, session.Timeline.Count);
        Assert.Same(bubble, session.Timeline[0]);
        var fold = Assert.IsType<ChatTurnViewModel>(session.Timeline[1]);
        Assert.Equal(1, fold.Turn);
        Assert.Single(fold.Final);
        Assert.Equal("收到", fold.Final[0].TargetText);
    }

    [Fact]
    public async Task Reopen_ExtendsMidTurnWindowBackToTheTurnStart_SoSteersSurvive()
    {
        // One very long running turn: the newest 30-message page starts far inside it, so both the opening
        // prompt and the steer interjection fall outside the page.
        var client = new ScriptedClient { Running = true };
        client.Append("turn/start", TurnStartJson(1));
        client.Append("user/message", UserJson("初始提示", "rpc-initial"));
        for (var step = 1; step <= 25; step++)
        {
            client.Append("step/start", StepJson(1, step));
            client.Append("assistant/message", AssistantJson(1, step, "过程 " + step));
        }

        client.Append("user/message", UserJson("插话指令", "rpc-steer"));
        for (var step = 26; step <= 69; step++)
        {
            client.Append("step/start", StepJson(1, step));
            client.Append("assistant/message", AssistantJson(1, step, "过程 " + step));
        }

        var cacheRoot = Path.Combine(Path.GetTempPath(), "dshremote-timeline-tests", Guid.NewGuid().ToString("N"));
        var cache = new SessionCacheStore(cacheRoot, "timeline-tests");
        try
        {
            await using var session = await OpenAsync(client, cache);
            await session.LoadAsync();

            // The turn-boundary read-ahead is lazy (SessionViewModel.TryExtendTurnBoundaryLazilyAsync) and needs
            // the session cache window to know what is already rendered: the first "load older" completes the
            // newest turn's window back to its turn/start. The repair is unchanged; only its trigger is deferred.
            await InvokeAsync(session, "LoadOlderAsync");

            // Both user rows are back even though the first page started after them.
            var userRows = session.Messages.Where(message => message.IsUser).ToList();
            Assert.Contains(userRows, message => message.TargetText == "初始提示");
            Assert.Contains(userRows, message => message.TargetText == "插话指令");
            Assert.Equal(2, userRows.Count);
            Assert.All(userRows, message => Assert.NotNull(message.Seq));

            // Conversation order is preserved: the opening prompt precedes the fold.
            var promptIndex = session.Timeline.IndexOf(userRows.Single(message => message.TargetText == "初始提示"));
            var foldIndex = session.Timeline.IndexOf(session.Timeline.OfType<ChatTurnViewModel>().Single());
            Assert.True(promptIndex >= 0 && foldIndex > promptIndex);
        }
        finally
        {
            TryDeleteDirectory(cacheRoot);
        }
    }

    [Fact]
    public async Task LiveSteer_PromotesInPlace_AndRendersAfterTheFold()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        // First prompt of the turn, echoed while the turn starts.
        session.Draft = "第一条";
        await InvokeAsync(session, "SendAsync");
        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("第一条", Assert.IsType<string>(client.LastRequestId)));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));

        // Steer (插话) while the turn is running: the echo carries no client rpcId, so the text fallback matches.
        session.IsSteerMode = true;
        session.Draft = "插话内容";
        await InvokeAsync(session, "SendAsync");

        var steer = Assert.Single(session.Messages, message => message.IsUser && message.TargetText == "插话内容");
        Assert.Null(steer.Seq);

        PushLive(session, client, "user/message", UserJson("插话内容", null));

        Assert.Same(steer, Assert.Single(session.Messages, message => message.IsUser && message.TargetText == "插话内容"));
        Assert.Equal(4, steer.Seq);
        Assert.Equal(2, session.Messages.Count(message => message.IsUser));

        var promptIndex = session.Timeline.IndexOf(
            session.Messages.Single(message => message.IsUser && message.TargetText == "第一条"));
        var foldIndex = session.Timeline.IndexOf(session.Timeline.OfType<ChatTurnViewModel>().Single());
        var steerIndex = session.Timeline.IndexOf(steer);
        Assert.True(promptIndex >= 0 && foldIndex > promptIndex);
        Assert.True(steerIndex > foldIndex, "a steer bubble belongs after the running turn's fold");
    }

    [Fact]
    public async Task Reopen_FinishedConversation_KeepsEveryPromptAndTurnAnswer()
    {
        // Two finished turns whose steps make the newest 30-message page much larger than the first-screen row
        // budget. The trimmed part of the page used to be unreachable (the backwards cursor was the raw page
        // start), so reopening lost the first turn's prompt and final answer. Loading older must be lossless.
        var client = new ScriptedClient();
        client.Append("turn/start", TurnStartJson(1));
        client.Append("user/message", UserJson("第一轮提示", "rpc-t1"));
        for (var step = 1; step <= 60; step++)
        {
            client.Append("step/start", StepJson(1, step));
            client.Append("assistant/message", AssistantJson(1, step, "过程一 " + step));
            client.Append("tool/call", ToolCallJson(1, step, "read", "call-1-" + step, "{\"file_path\":\"D:\\\\ws\\\\a.cs\"}"));
            AppendToolResult(client, 1, step, "call-1-" + step, "ok");
            client.Append("step/end", StepJson(1, step));
        }

        client.Append("step/start", StepJson(1, 61));
        client.Append("assistant/message", AssistantJson(1, 61, "第一轮完成"));
        client.Append("step/end", StepJson(1, 61));
        client.Append("turn/end", TurnEndJson(1));

        client.Append("turn/start", TurnStartJson(2));
        client.Append("user/message", UserJson("第二轮提示", "rpc-t2"));
        for (var step = 1; step <= 60; step++)
        {
            client.Append("step/start", StepJson(2, step));
            client.Append("assistant/message", AssistantJson(2, step, "过程二 " + step));
            client.Append("tool/call", ToolCallJson(2, step, "read", "call-2-" + step, "{\"file_path\":\"D:\\\\ws\\\\b.cs\"}"));
            AppendToolResult(client, 2, step, "call-2-" + step, "ok");
            client.Append("step/end", StepJson(2, step));
        }

        client.Append("step/start", StepJson(2, 61));
        client.Append("assistant/message", AssistantJson(2, 61, "第二轮完成"));
        client.Append("step/end", StepJson(2, 61));
        client.Append("turn/end", TurnEndJson(2));

        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        // The initial window may start mid-turn; the remaining history is requested by the view.
        var guard = 0;
        while (session.HasOlder && guard++ < 25)
        {
            await InvokeAsync(session, "LoadOlderAsync");
        }

        Assert.False(session.HasOlder);

        var userRows = session.Messages.Where(message => message.IsUser).ToList();
        Assert.Equal(2, userRows.Count);
        Assert.Contains(userRows, message => message.TargetText == "第一轮提示");
        Assert.Contains(userRows, message => message.TargetText == "第二轮提示");

        // Lossless paging must never render the same durable event twice.
        var seqs = session.Messages.Where(message => message.Seq is not null)
            .Select(message => message.Seq!.Value)
            .ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());

        var folds = session.Timeline.OfType<ChatTurnViewModel>().ToList();
        Assert.Equal(2, folds.Count);
        Assert.Equal("第一轮完成", Assert.Single(folds.Single(fold => fold.Turn == 1).Final).TargetText);
        Assert.Equal("第二轮完成", Assert.Single(folds.Single(fold => fold.Turn == 2).Final).TargetText);

        // Prompts keep their conversation order relative to the folds.
        var firstPrompt = userRows.Single(message => message.TargetText == "第一轮提示");
        var secondPrompt = userRows.Single(message => message.TargetText == "第二轮提示");
        Assert.True(session.Timeline.IndexOf(firstPrompt) < session.Timeline.IndexOf(secondPrompt));
    }

    [Fact]
    public async Task TurnFileCard_ListsPresentedAndModifiedFiles()
    {
        var client = new ScriptedClient();
        client.Append("turn/start", TurnStartJson(1));
        client.Append("user/message", UserJson("打包并交付", "rpc-files"));
        client.Append("step/start", StepJson(1, 1));
        client.Append("assistant/message", AssistantJson(1, 1, "开始处理"));
        client.Append("tool/call", ToolCallJson(1, 1, "edit", "call-edit",
            "{\"file_path\":\"D:\\\\ws\\\\src\\\\a.cs\",\"old_string\":\"one\\ntwo\",\"new_string\":\"one\\ntwo\\nthree\"}"));
        AppendToolResult(client, 1, 1, "call-edit", "edited");
        client.Append("tool/call", ToolCallJson(1, 1, "write", "call-write",
            "{\"file_path\":\"D:\\\\ws\\\\notes.md\",\"content\":\"line1\\nline2\"}"));
        AppendToolResult(client, 1, 1, "call-write", "written");
        client.Append("tool/call", ToolCallJson(1, 1, "present", "call-present",
            "{\"files\":[{\"path\":\"D:\\\\ws\\\\dist\\\\app.apk\",\"description\":\"签名 APK\"}]}"));
        client.Append("deliverables/presented", PresentedJson(1, "call-present",
            "[{\"path\":\"D:\\\\ws\\\\dist\\\\app.apk\",\"description\":\"签名 APK\"}]"));
        client.Append("step/end", StepJson(1, 1));
        client.Append("step/start", StepJson(1, 2));
        client.Append("assistant/message", AssistantJson(1, 2, "完成"));
        client.Append("step/end", StepJson(1, 2));
        client.Append("turn/end", TurnEndJson(1));

        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        var fold = Assert.Single(session.Timeline.OfType<ChatTurnViewModel>());
        Assert.True(fold.HasFiles);
        Assert.Equal(3, fold.Files.Count);

        var produced = Assert.Single(fold.Files, file => file.Path.EndsWith("app.apk", StringComparison.Ordinal));
        Assert.True(produced.IsProduced);
        Assert.Equal("签名 APK", produced.Description);
        Assert.Equal("app.apk", produced.DisplayName);

        var edited = Assert.Single(fold.Files, file => file.Path.EndsWith("a.cs", StringComparison.Ordinal));
        Assert.False(edited.IsProduced);
        Assert.Equal(3, edited.Added);
        Assert.Equal(2, edited.Deleted);

        var written = Assert.Single(fold.Files, file => file.Path.EndsWith("notes.md", StringComparison.Ordinal));
        Assert.Equal(2, written.Added);
        Assert.Equal(0, written.Deleted);

        // The artifact row feeds the card but is never a transcript row of its own.
        Assert.DoesNotContain(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsArtifact);
    }

    [Fact]
    public async Task LiveFileCard_AppearsWhenAnEditArrives_AndKeepsProducedFirst()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        session.Draft = "改文件";
        await InvokeAsync(session, "SendAsync");
        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("改文件", Assert.IsType<string>(client.LastRequestId)));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));

        var fold = Assert.Single(session.Timeline.OfType<ChatTurnViewModel>());
        Assert.False(fold.HasFiles);

        PushLive(session, client, "tool/call", ToolCallJson(1, 1, "edit", "call-live-edit",
            "{\"file_path\":\"D:\\\\ws\\\\live.cs\",\"old_string\":\"a\",\"new_string\":\"a\\nb\"}"));
        Assert.True(fold.HasFiles);
        Assert.Equal(2, Assert.Single(fold.Files).Added);

        PushLive(session, client, "deliverables/presented", PresentedJson(1, "call-live-present",
            "[{\"path\":\"D:\\\\ws\\\\out.zip\",\"description\":\"产物\"}]"));
        Assert.Equal(2, fold.Files.Count);
        Assert.True(fold.Files[0].IsProduced);
        Assert.Equal("产物", fold.Files[0].Description);
    }

    // ---- Session-log surface semantics (compaction / replace) -------------------------------------

    [Fact]
    public async Task ReplacementToolResult_IsModelOnly_AndDoesNotTouchTheAppendCard()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("看一下目录", "rpc-s1"));
        PushLive(session, client, "step/start", StepJson(1, 1));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));
        PushLive(session, client, "tool/call", ToolCallJson(1, 1, "read", "call-append", "{\"file_path\":\"D:\\\\ws\\\\a.cs\"}"));
        PushLive(session, client, "tool/result", ToolResultJson("call-append", "原始完整结果"));

        var fold = Assert.Single(session.Timeline.OfType<ChatTurnViewModel>());
        var tool = Assert.Single(fold.Process, message => message.IsTool);
        Assert.Equal("原始完整结果", tool.ResultText);
        var messagesBefore = session.Messages.Count;
        var timelineBefore = session.Timeline.Count;

        // A compaction tool-result pruning replacement: same callId, new seq, model-only.
        PushLive(
            session,
            client,
            "tool/result",
            ToolResultJson("call-append", "裁剪后的结果"),
            surfaceOp: ReplaceOp(100, 100),
            sourceEventSeqs: new long[] { 100 });

        Assert.Equal(messagesBefore, session.Messages.Count);
        Assert.Equal(timelineBefore, session.Timeline.Count);
        Assert.Equal("原始完整结果", tool.ResultText);
        AssertTopLevelRowsAreUserSystemOrCompaction(session);
    }

    [Fact]
    public async Task SummaryCompaction_RendersOneCard_AndHidesTheCheckpointPrompt()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("继续做迁移", "rpc-c1"));
        PushLive(session, client, "step/start", StepJson(1, 1));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));

        PushLive(session, client, "compaction/start", CompactionStartJson("cmp-1", 1));
        PushLive(session, client, "compaction/summary", CompactionSummaryJson("cmp-1", "这是摘要正文", new long[] { 2, 3 }, 42000));
        var checkpointSeq = PushLive(
            session,
            client,
            "user/message",
            CheckpointUserJson("cmp-1", "自动生成的检查点"),
            surfaceOp: ReplaceOp(2, 2),
            sourceEventSeqs: new long[] { 2 });
        PushLive(session, client, "compaction/end", CompactionEndJson("cmp-1", 1));

        var cards = session.Timeline.OfType<ChatMessageViewModel>()
            .Where(message => message.IsCompaction)
            .ToList();
        var card = Assert.Single(cards);
        Assert.Equal(checkpointSeq, card.Seq);
        Assert.True(card.HasCompactionSummary);
        Assert.Equal("这是摘要正文", card.CompactionSummary);
        Assert.Contains("已压缩 2 项", card.CompactionMetaText, StringComparison.Ordinal);
        Assert.Contains("42k", card.CompactionMetaText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            session.Timeline.OfType<ChatMessageViewModel>(),
            message => message.IsUser && message.TargetText.Contains("自动生成的检查点", StringComparison.Ordinal));
        Assert.False(session.IsCompacting);
        AssertTopLevelRowsAreUserSystemOrCompaction(session);
    }

    [Fact]
    public async Task SummaryCompaction_CardSurvivesSummaryArrivingAfterCheckpoint()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("继续", "rpc-c2"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));

        // Page split: the checkpoint is visible before its summary event.
        PushLive(
            session,
            client,
            "user/message",
            CheckpointUserJson("cmp-2", "检查点"),
            surfaceOp: ReplaceOp(3, 3),
            sourceEventSeqs: new long[] { 3 });

        var card = Assert.Single(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsCompaction);
        Assert.False(card.HasCompactionSummary);
        Assert.Contains("摘要尚未加载", card.CompactionHintText, StringComparison.Ordinal);

        PushLive(session, client, "compaction/summary", CompactionSummaryJson("cmp-2", "迟到的摘要", new long[] { 3 }, 1500));
        Assert.Equal(1, session.Timeline.OfType<ChatMessageViewModel>().Count(message => message.IsCompaction));
        Assert.True(card.HasCompactionSummary);
        Assert.Equal("迟到的摘要", card.CompactionSummary);
        Assert.Contains("1.5k", card.CompactionMetaText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanToolResult_IsStashed_AndBackfilledWhenTheCallArrives()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("跑个命令", "rpc-o1"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));

        // Page boundary: the result is visible while its tool/call is not loaded yet.
        PushLive(
            session,
            client,
            "tool/result",
            ToolResultJson("call-late", "被晚点匹配的结果"),
            surfaceOp: "append",
            sourceEventSeqs: new long[] { 900 });

        Assert.DoesNotContain(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsTool);
        var messagesBeforeCall = session.Messages.Count;

        PushLive(session, client, "tool/call", ToolCallJson(1, 2, "pwsh", "call-late", "{\"command\":\"ls\"}"));

        Assert.Equal(messagesBeforeCall + 1, session.Messages.Count);
        var fold = Assert.Single(session.Timeline.OfType<ChatTurnViewModel>());
        var tool = Assert.Single(fold.Process, message => message.IsTool);
        Assert.True(tool.HasResultApplied);
        Assert.Equal("被晚点匹配的结果", tool.ResultText);
        AssertTopLevelRowsAreUserSystemOrCompaction(session);
    }

    [Fact]
    public async Task LegacySurfaceEvents_WithoutSurfaceOp_StillRenderLikeAppends()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        // No surfaceOp at all (older host): tool/result must still reach its card.
        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("旧宿主", "rpc-l1"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));
        PushLive(session, client, "tool/call", ToolCallJson(1, 1, "read", "call-legacy", "{}"));
        PushLive(session, client, "tool/result", ToolResultJson("call-legacy", "旧格式结果"));

        var fold = Assert.Single(session.Timeline.OfType<ChatTurnViewModel>());
        var tool = Assert.Single(fold.Process, message => message.IsTool);
        Assert.Equal("旧格式结果", tool.ResultText);
    }

    [Fact]
    public async Task ManualCompaction_Card_IsMarkedManual()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("手动压缩", "rpc-m1"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));
        PushLive(session, client, "compaction/start", CompactionStartJson("cmp-manual", 1, "cmd-1"));
        PushLive(session, client, "compaction/summary", CompactionSummaryJson("cmp-manual", "手动摘要", new long[] { 5 }, 100));
        PushLive(
            session,
            client,
            "user/message",
            CheckpointUserJson("cmp-manual", "手动压缩检查点", "cmd-1"),
            surfaceOp: ReplaceOp(5, 5),
            sourceEventSeqs: new long[] { 5 });
        PushLive(session, client, "compaction/end", CompactionEndJson("cmp-manual", 1, sourceCommandId: "cmd-1"));

        var card = Assert.Single(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsCompaction);
        Assert.True(card.IsCompactionManual);
        Assert.Contains("手动压缩", card.CompactionMetaText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCompaction_WithoutCheckpoint_DoesNotRenderRows()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("会失败的压缩", "rpc-f1"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));
        PushLive(session, client, "compaction/start", CompactionStartJson("cmp-fail", 1));
        PushLive(session, client, "compaction/end", CompactionEndJson("cmp-fail", 1, error: "summary failed"));

        Assert.False(session.IsCompacting);
        Assert.DoesNotContain(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsCompaction);
        Assert.DoesNotContain(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsError);
        AssertTopLevelRowsAreUserSystemOrCompaction(session);
    }

    [Fact]
    public async Task FailedCompaction_WithCheckpoint_MarksTheCardFailed()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("失败压缩", "rpc-f2"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));
        PushLive(session, client, "compaction/start", CompactionStartJson("cmp-fail2", 1));
        Assert.True(session.IsCompacting);
        PushLive(session, client, "compaction/summary", CompactionSummaryJson("cmp-fail2", "部分摘要", new long[] { 7 }, 900));
        PushLive(
            session,
            client,
            "user/message",
            CheckpointUserJson("cmp-fail2", "检查点"),
            surfaceOp: ReplaceOp(7, 7),
            sourceEventSeqs: new long[] { 7 });
        PushLive(session, client, "compaction/end", CompactionEndJson("cmp-fail2", 1, error: "commit failed"));

        Assert.False(session.IsCompacting);
        var card = Assert.Single(session.Timeline.OfType<ChatMessageViewModel>(), message => message.IsCompaction);
        Assert.True(card.IsCompactionFailed);
        Assert.Contains("失败", card.CompactionMetaText, StringComparison.Ordinal);
        Assert.Contains("未完成", card.CompactionHintText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactionPruneBlock_ProducesNoRows_AndKeepsTheAppendResult()
    {
        var client = new ScriptedClient();
        await using var session = await OpenAsync(client);
        await session.LoadAsync();

        PushLive(session, client, "turn/start", TurnStartJson(1));
        PushLive(session, client, "user/message", UserJson("跑命令", "rpc-p1"));
        PushLive(session, client, "assistant/message", AssistantJson(1, 1, "处理中"));
        var callSeq = PushLive(
            session,
            client,
            "tool/call",
            ToolCallJson(1, 1, "pwsh", "call-prune", "{\"command\":\"Get-ChildItem\"}"));
        PushLive(
            session,
            client,
            "tool/result",
            ToolResultJson("call-prune", "原始完整输出"),
            surfaceOp: "append",
            sourceEventSeqs: new long[] { callSeq });

        var before = session.Messages.Count;
        var tool = Assert.Single(Assert.Single(session.Timeline.OfType<ChatTurnViewModel>()).Process, message => message.IsTool);

        // The 14:18 shape: prune marker + replacement tool/result for an older call outside the window.
        PushLive(
            session,
            client,
            "compaction/prune",
            PruneJson(new long[] { callSeq }, 2181));
        PushLive(
            session,
            client,
            "tool/result",
            ToolResultJson("call-prune", "裁剪后的输出"),
            surfaceOp: ReplaceOp(callSeq, callSeq),
            sourceEventSeqs: new long[] { callSeq });

        Assert.Equal(before, session.Messages.Count);
        Assert.Equal("原始完整输出", tool.ResultText);
        AssertTopLevelRowsAreUserSystemOrCompaction(session);
    }

    private static void AssertTopLevelRowsAreUserSystemOrCompaction(SessionViewModel session)
    {
        Assert.DoesNotContain(
            session.Timeline.OfType<ChatMessageViewModel>(),
            message => !message.IsUser
                       && !(message.IsSystem && message.IsError)
                       && !message.IsCompaction
                       && message.Turn <= 0);
    }

    private static object ReplaceOp(long startSeq, long endSeq) => new Dictionary<string, object?>
    {
        ["op"] = "replace",
        ["startSeq"] = startSeq,
        ["endSeq"] = endSeq
    };

    private static string ToolResultJson(string callId, string output) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["turn"] = 1,
        ["message"] = new Dictionary<string, object?>
        {
            ["role"] = "tool",
            ["source"] = new Dictionary<string, object?> { ["kind"] = "tool", ["callId"] = callId },
            ["toolCallId"] = callId,
            ["content"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = output }
            },
            ["isError"] = false
        }
    });

    private static string CheckpointUserJson(string compactionId, string text, string? sourceCommandId = null)
    {
        var source = new Dictionary<string, object?>
        {
            ["kind"] = "compact-checkpoint",
            ["compactionId"] = compactionId
        };
        if (sourceCommandId is not null)
        {
            source["sourceCommandId"] = sourceCommandId;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["content"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
            },
            ["source"] = source,
            ["role"] = "user"
        });
    }

    private static string CompactionStartJson(string compactionId, int turn, string? sourceCommandId = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["compactionId"] = compactionId,
            ["turn"] = turn
        };
        if (sourceCommandId is not null)
        {
            data["sourceCommandId"] = sourceCommandId;
        }

        return JsonSerializer.Serialize(data);
    }

    private static string CompactionSummaryJson(string compactionId, string summary, long[] shadowedSeqs, int tokens)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["compactionId"] = compactionId,
            ["summary"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = summary }
            },
            ["shadowedSeqs"] = shadowedSeqs,
            ["shadowedTokenCount"] = tokens
        });

    private static string CompactionEndJson(
        string compactionId,
        int turn,
        string? error = null,
        string? sourceCommandId = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["compactionId"] = compactionId,
            ["turn"] = turn
        };
        if (error is not null)
        {
            data["error"] = error;
        }

        if (sourceCommandId is not null)
        {
            data["sourceCommandId"] = sourceCommandId;
        }

        return JsonSerializer.Serialize(data);
    }

    private static string PruneJson(long[] shadowedSeqs, int tokens) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["shadowedRange"] = new Dictionary<string, object?>
        {
            ["start"] = shadowedSeqs[0],
            ["end"] = shadowedSeqs[^1]
        },
        ["shadowedSeqs"] = shadowedSeqs,
        ["shadowedTokenCount"] = tokens
    });

    // ---- Harness ----------------------------------------------------------------------------------

    private static SessionEventRecord Event(
        long seq,
        string type,
        string dataJson,
        long time = 0,
        object? surfaceOp = null,
        long[]? sourceEventSeqs = null,
        bool? ignorable = null)
    {
        using var document = JsonDocument.Parse(dataJson);
        var timeValue = time == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + seq : time;
        var envelope = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["seq"] = seq,
            ["time"] = timeValue,
            ["data"] = document.RootElement
        };
        if (surfaceOp is not null)
        {
            envelope["surfaceOp"] = surfaceOp;
        }

        if (sourceEventSeqs is not null)
        {
            envelope["sourceEventSeqs"] = sourceEventSeqs;
        }

        if (ignorable is not null)
        {
            envelope["ignorable"] = ignorable;
        }

        var raw = JsonSerializer.Serialize(envelope);
        // Route through the production parser so surfaceOp/sourceEventSeqs/ignorable are exercised exactly
        // like the wire path (legacy events without the envelope fields keep working through the same code).
        return SessionEventRecord.FromRawJson(raw)
               ?? throw new InvalidOperationException("test event envelope failed to parse");
    }

    private static string TurnStartJson(int turn) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["turn"] = turn
    });

    private static string StepJson(int turn, int step) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["turn"] = turn,
        ["step"] = step
    });

    private static string UserJson(string text, string? rpcId)
    {
        var source = rpcId is null
            ? new Dictionary<string, object?> { ["kind"] = "user" }
            : new Dictionary<string, object?> { ["kind"] = "user", ["rpcId"] = rpcId };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["content"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
            },
            ["source"] = source,
            ["role"] = "user"
        });
    }

    private static string AssistantJson(int turn, int step, string text) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["turn"] = turn,
        ["step"] = step,
        ["message"] = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }
            }
        }
    });

    private static string TurnEndJson(int turn) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["turn"] = turn,
        ["reason"] = new Dictionary<string, object?> { ["kind"] = "completed" }
    });

    private static string ToolCallJson(int turn, int step, string name, string callId, string argumentsJson)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["turn"] = turn,
            ["step"] = step,
            ["name"] = name,
            ["callId"] = callId,
            ["arguments"] = argumentsJson
        });

    private static string PresentedJson(int turn, string callId, string filesJson)
    {
        using var files = JsonDocument.Parse(filesJson);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["turn"] = turn,
            ["callId"] = callId,
            ["files"] = files.RootElement
        });
    }

    private static void AppendToolResult(
        ScriptedClient client,
        int turn,
        int step,
        string callId,
        string output)
    {
        client.Append("tool/result", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["turn"] = turn,
            ["step"] = step,
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["source"] = new Dictionary<string, object?> { ["kind"] = "tool", ["callId"] = callId },
                ["toolCallId"] = callId,
                ["content"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = output }
                },
                ["isError"] = false
            }
        }));
    }

    private static long PushLive(
        SessionViewModel session,
        ScriptedClient client,
        string type,
        string dataJson,
        object? surfaceOp = null,
        long[]? sourceEventSeqs = null,
        bool? ignorable = null)
    {
        var seq = client.NextLiveSeq();
        Invoke(session, "OnLiveEvent", Event(seq, type, dataJson, surfaceOp: surfaceOp, sourceEventSeqs: sourceEventSeqs, ignorable: ignorable));
        // Live events are normally applied in coalesced batches on a dispatcher timer; unit tests have no
        // running dispatcher loop, so flush the pending batch synchronously before asserting.
        Invoke(session, "FlushLiveEvents");
        return seq;
    }

    private static async Task<SessionViewModel> OpenAsync(ScriptedClient client, SessionCacheStore? cache = null)
    {
        var summary = new SessionSummary(
            "sess-test",
            "测试会话",
            @"D:\Code\VibeCoding\DSHRemote",
            "deepseek-v4.1-flash",
            Running: client.Running,
            AgentAvailable: true,
            Blank: false,
            UpdatedAt: 0,
            LastPromptAt: 0);
        var settings = new SettingsViewModel();
        var session = new SessionViewModel(
            client,
            summary,
            new RelayCommand(() => { }),
            new InteractionHub(),
            settings,
            cache);
        await Task.Yield();
        return session;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Task InvokeAsync(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"{target.GetType().Name}.{methodName} was not found");
        return (Task)(method.Invoke(target, arguments)
                      ?? throw new InvalidOperationException($"{methodName} returned null"));
    }

    private static void Invoke(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"{target.GetType().Name}.{methodName} was not found");
        method.Invoke(target, arguments);
    }

    /// <summary>
    /// Minimal in-memory DSH host: history paging mirrors the real server (append-surface user/assistant
    /// message budget with a contiguous slice), state is scripted, live events are pushed by the test.
    /// </summary>
    private sealed class ScriptedClient : IDshClient, ISessionFollowClient, IHistoryPagingClient
    {
        private readonly List<SessionEventRecord> _history = new();
        private readonly Channel<SessionEventRecord> _live = Channel.CreateUnbounded<SessionEventRecord>();
        private long _lastLiveSeq;

        public DshConnectionState State => DshConnectionState.Connected;

        public DshPairingInfo? Pairing => null;

        public bool Running { get; set; }

        public string? LastRequestId { get; private set; }

#pragma warning disable CS0067 // connection state changes are not scripted
        public event EventHandler<DshConnectionState>? StateChanged;
#pragma warning restore CS0067

        public void Append(
            string type,
            string dataJson,
            object? surfaceOp = null,
            long[]? sourceEventSeqs = null,
            bool? ignorable = null)
        {
            var seq = _history.Count;
            _history.Add(Event(seq, type, dataJson, surfaceOp: surfaceOp, sourceEventSeqs: sourceEventSeqs, ignorable: ignorable));
            _lastLiveSeq = Math.Max(_lastLiveSeq, seq);
        }

        public long NextLiveSeq() => ++_lastLiveSeq;

        public Task ConnectAsync(DshPairingInfo pairing, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionSummary>>(Array.Empty<SessionSummary>());

        public Task<SessionHistoryPage> GetHistoryAsync(
            string sessionId,
            long? beforeSeq = null,
            int maxMessages = 80,
            CancellationToken cancellationToken = default)
            => Task.FromResult(LatestPage(beforeSeq, maxMessages));

        public Task<SessionHistoryPage> GetLatestHistoryAsync(
            string sessionId,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(LatestPage(null, maxMessages));

        public Task<SessionHistoryPage> GetOlderHistoryAsync(
            string sessionId,
            long beforeSeq,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(LatestPage(beforeSeq, maxMessages));

        public Task<SessionLiteSnapshot> OpenSessionLiteAsync(
            string sessionId,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLiteSnapshot(LatestPage(null, maxMessages), StateSnapshot()));

        public Task<SessionRuntimeState> GetStateAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(StateSnapshot());

        public Task<string> SendPromptAsync(string sessionId, string text, bool steer = false, CancellationToken cancellationToken = default)
        {
            LastRequestId = "req-" + (_history.Count + 1);
            return Task.FromResult(LastRequestId);
        }

        public Task<string> SendPromptAsync(
            string sessionId,
            string text,
            IReadOnlyList<DshImageAttachment>? images,
            bool steer = false,
            CancellationToken cancellationToken = default)
            => SendPromptAsync(sessionId, text, steer, cancellationToken);

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<SessionEventRecord> ObserveEventsAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var record in _live.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }

        public Task<SessionSnapshot> OpenSessionAsync(string sessionId, int maxMessages = 80, CancellationToken cancellationToken = default)
        {
            var page = LatestPage(null, maxMessages);
            return Task.FromResult(new SessionSnapshot(page.Records, page.Cursor, page.HasMore, StateSnapshot()));
        }

        public IAsyncEnumerable<SessionEventRecord> FollowEventsAsync(string sessionId, CancellationToken cancellationToken = default)
            => ObserveEventsAsync(sessionId, cancellationToken);

        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateQueueAsync(string sessionId, string itemId, QueueEditAction action, string? text = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

#pragma warning disable CS1998 // no image/attachment access in these tests
        public async ValueTask DisposeAsync() => await Task.CompletedTask;
#pragma warning restore CS1998

        private SessionRuntimeState StateSnapshot()
            => new(_history.Count == 0 ? 0 : _history[^1].Seq, Active: true, Running, Array.Empty<QueuedPrompt>(), Array.Empty<string>());

        /// <summary>
        /// Mirrors the DSH host: count user/message + assistant/message events backwards from the page end and
        /// cut at the message that exhausts the budget; the slice keeps every intermediate event.
        /// </summary>
        private SessionHistoryPage LatestPage(long? beforeSeq, int maxMessages)
        {
            var end = beforeSeq is { } limit
                ? _history.FindIndex(record => record.Seq >= limit)
                : _history.Count;
            if (end < 0)
            {
                end = _history.Count;
            }

            var count = 0;
            var cut = 0;
            for (var index = end - 1; index >= 0; index--)
            {
                var record = _history[index];
                var type = record.Type;
                if (type is not ("user/message" or "assistant/message"))
                {
                    continue;
                }

                // The real host counts append-surface messages only; replacement messages (e.g. a compaction
                // checkpoint) are not part of the message budget.
                if (record.IsReplacementSurfaceEvent)
                {
                    continue;
                }

                count++;
                if (count >= Math.Max(1, maxMessages))
                {
                    cut = index;
                    break;
                }
            }

            var records = _history.GetRange(cut, end - cut);
            var cursor = _history.Count == 0 ? 0 : _history[^1].Seq;
            return new SessionHistoryPage(
                records,
                cursor,
                HasMore: cut > 0,
                HasMoreOlder: cut > 0,
                BeforeSeq: records.Count > 0 ? records[0].Seq : null);
        }
    }
}

