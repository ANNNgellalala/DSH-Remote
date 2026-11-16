# Arc.Avalonia Chat / Agent 接入 DSH Remote 会话渲染 — 调研与落地方案

> 调研员：`arc-android`（task-14）
> 仓库：`D:\Code\VibeCoding\Arc.Avalonia`（HEAD = `7c15f4c` + 工作区未提交改动；**只读，未修改**）
> 产品基线：`D:\Code\VibeCoding\DSHRemote\app`（Arc.Avalonia `0.9.0-alpha`，自研 `MarkdownBodyView`）
> 实验产物：`_scratch/android-chat-demo/`（Android 真机可跑的最小 demo）
> 结论日期：2026-11-09

---

## 0. 结论速览

| 问题 | 结论 |
|---|---|
| Arc 有没有能直接重做会话渲染的组件？ | **有，但要分清两层**：`Arc.Avalonia.Chat`（气泡对话面）适合「人/AI 聊天气泡 + Markdown 流式」；`Arc.Avalonia.Agent`（`ArcAgentConversation` / `ArcAgentSurface` + 事件归约器）才是「工具卡 + 审批 + 问答 + 推理 + 后台 run」的正确载体。DSH 是 agent 会话，**建议走 Agent 层**，Chat 层只当气泡外壳。 |
| 能把 DSH 事件映射成 Arc agent 事件吗？ | **能**。19 种 `ArcAgentEvent` 覆盖 turn/step/message/stream/ reasoning/tool/approval/question/attention/artifact 全链路，映射表见 §3。 |
| ArcMarkdownView 在 Android 上还叠层吗？ | **仍然坏**（实测 `emulator-5556`，三组对照）。不仅叠层：**中文变 tofu 方块 + 行高塌陷 + 表格溢出裁切**。Arc HEAD 未修（当前未提交改动只加触摸/滚动链，不修字体与布局）。详见 §2。 |
| 现在能直接换掉自研 Markdown 吗？ | **暂时不能**。必须先解决 §2 的字体/布局缺陷（我们已定位到 `ArcMarkdownStyle` 的字体来源，给出三种修法与待验证结论）。 |
| 缺什么组件？ | 见 §5：工具卡字段、推理折叠块、排队/steer 列表、Chat 自定义行、图片块 5 项，附建议归属包与 API 草案。 |

---

## 1. `Arc.Avalonia.Chat` 盘点（L2，新包，**尚未发布 NuGet**）

源码：`src/Components/Arc.Avalonia.Chat/`（6 个控件 + 1 个模型文件）。
> ⚠️ 本地 NuGet 只有 `arc.avalonia.* 0.9.0-alpha`（无 `Arc.Avalonia.Chat`）；`0.9.0-alpha` 里的 `ArcMarkdownChatView/ArcMarkdownChatMessage` 是**上一代**，已被 commit `2d98af6` 的 `ArcChatView` 取代。要接 Chat 层必须 ProjectReference 源码或让 Arc 重新 pack。

### 1.1 控件与 API

| 类型 | 基类 | 关键成员 |
|---|---|---|
| `ArcChatView` | `TemplatedControl, IDisposable` | **`Messages`**（`ObservableCollection<ArcChatMessage>`）；属性 `AutoScrollMode`(`Never/NearBottom/Always`)、`MarkdownOptions`、`Participants`、`CurrentUserId`、`Typing`、`Placeholder`、`ShowComposer`、`AllowAttachments`、`Label`、`GroupWindow`(TimeSpan)、`ShowAvatars`、`ShowTimestamps`；只读 `Composer`、`ActiveSession`、`IsStreaming`、`IsNearBottom`、`FocusedMessageId`；事件 `MessageAdded/Updated/Removed`、`Send(ArcChatDraft)`、`StopRequested`、`InteractionOccurred`、`Warning`、`Announced`；方法 `AddMessage/InsertMessage/UpdateMessage/RemoveMessage/ClearMessages/ReplaceMessages/FindMessage/IndexOf/BeginStreamingMessage/AbortStreaming(Async)/ScrollToEnd/ScrollToMessage` |
| `ArcChatThread` | `ArcChatView` | 别名（无新增成员） |
| `ArcChatBubble` | `TemplatedControl` | `Message`、`IsOwn`、`ShowHeader`、`ShowStatus`、`ShowAvatar`、`FirstInGroup`、`LastInGroup`、`MarkdownOptions`、`AnimateEntry`；`Body => ArcMarkdownView`；`BeginStreaming()`；事件 `InteractionOccurred/Warning/Announced` |
| `ArcChatComposer` | `TemplatedControl` | `Text`、`Placeholder`、`IsRunning`、`AllowAttachments`、`MaxAttachments`、`LeadingContent`；事件 `Send(ArcChatDraft)`、`StopRequested`；`SendEvent`/`SendRouted`；`PendingAttachments`、`AddAttachment/TryAddAttachment/RemoveAttachment/Clear/FocusInput/Submit` |
| `ArcChatRow` | `internal StackPanel` | 内部行容器，宿主不可用 |

### 1.2 数据模型（`ArcChatModels.cs`）

- `ArcChatMessage`：`Id`、`Role`(`User/Assistant/System`)、`Author`、**`Markdown`（setter 递增 `ContentVersion` 并触发 `Changed`）**、`Status`(`Draft/Streaming/Complete/Failed/Aborted`)、`Delivery`(`None/Sent/Delivered/Read`)、`Error`、`CreatedAt/UpdatedAt`、`Attachments`、`Metadata`、`IsStreaming`、`Append(delta)`、`Complete()`、`Changed` 事件。
- `ArcChatAttachment`：`Name/Size/Url/IsImage`；`ArcChatDraft(Text, Attachments)`；`ArcChatParticipant(Id/Name/Avatar)`。

### 1.3 虚拟化 / 自动滚动 / 流式槽位

- **虚拟化**：模板部件 `PART_List` 为 `ArcVirtualList : ListBox`（`StickToBottom`、`ScrollState/AtBottom`、`ScrollToEnd/ScrollToIndex`）——真虚拟化长列表，适合几百条消息。
- **自动滚动**：`AutoScrollMode` 三档 + `NearBottomThreshold = 56`；`IsNearBottom` 只读供宿主显示“回到底部”。
- **流式槽位**：`PART_StreamingSlot`（`ContentControl`）在列表**之外**渲染正在生成的消息；`BeginStreamingMessage(msg?) => ArcMarkdownStreamingSession`，`AbortStreaming(Async)` 冻结当前内容。
- **扩展点（关键缺口）**：行模板由私有方法 `BuildItemTemplate()`（`FuncDataTemplate<ArcChatMessage>`）在 `OnApplyTemplate` 里写死 → **宿主无法注入自定义行/卡片**。Chat 层只能出「纯 Markdown 气泡」，工具卡/审批/系统行都塞不进去。

> 结论：Chat 层适合「纯聊天」，不满足 DSH 的工具卡/审批/问答需求；它最大的价值是把 `ArcAgentConversation` 的内部气泡实现讲清楚了 —— Agent 层就是「Chat 气泡 + 全宽事件卡」的组合（Arc 自己在 csproj 注释里明确写了这一点）。

---

## 2. `Arc.Avalonia.Markdown` 与 Android 渲染验证（重点）

### 2.1 正确用法（0.9.0-alpha 与 HEAD 一致）

```csharp
// 1) Options/管道（可选，默认 CommonMark+GFM）
var options = new ArcMarkdownOptions
{
    Pipeline = ArcMarkdownPipeline.Default,          // 或 new ArcMarkdownPipelineBuilder().UseAgentComponents().Build()
    ImageLoader = myLoader,                          // IArcMarkdownImageLoader
    LinkHandler = myLinks, Clipboard = myClipboard,
    InteractionEnabled = true, EnableTextSelection = true,
    Limits = new ArcMarkdownLimits()                 // 行数/字符上限（防超长消息）
};

// 2) 一次性渲染
view.Markdown = text;            // 或 view.RenderDocument(PuraDocument)
view.Options = options;
view.Render();                   // Setter 已自动 Render，显式调用幂等

// 3) 流式
var session = view.BeginStreaming(new ArcMarkdownStreamingOptions
{
    TailWindowChars = 16 * 1024, MaxTailChars = 1024 * 1024, AutoScrollToEnd = true
});
session.Append(delta);            // 或 AppendAsync(ReadOnlyMemory<char>)
await session.CompleteAsync();    // 完成；AbortAsync() 冻结

// 4) 样式（全局静态，必须在首个控件渲染前设置）
ArcMarkdownStyle.Current = new ArcMarkdownStyle
{
    BodyFont = new FontFamily("sans-serif"),   // ← Android 中文能否渲染的关键，见 2.3
    DisplayFont = new FontFamily("sans-serif"),
    MonoFont = new FontFamily("monospace")
};
```

`ArcMarkdownView` 公开面：`Markdown`/`Options`、`Blocks`、`Outline`、`Diagnostics`、`ActiveSession`、`IsStreaming`、`InteractionStates`、`CurrentOptions`、`ScrollViewer`；`Render/RenderDocument/BeginStreaming/AbortStreaming/RefreshTheme/Prewarm/GetOutline/GetPlainText/CopyAllAsync/ScrollToAnchor/ScrollToEnd/SetInteractionState/ApplyInteractionStates/Announce`；事件 `LinkClicked/Warning/InteractionOccurred/Announced`。

### 2.2 实测：Android 上**仍然坏**（3 组对照）

环境：`emulator-5556`（x86_64 / API 36 / 1080×2400 / density 420）；demo `_scratch/android-chat-demo`（`dev.dshremote.arcchatdemo`），内容为真实形态长中文 Markdown（标题 / 长段落 / 有序无序列表 / 表格 / C# 代码块 / 引用 / 行内代码）。

| 组 | 依赖 | 结果 | 截图 |
|---|---|---|---|
| A | **`Arc.Avalonia 0.9.0-alpha`**（= 产品当前版本） | 中文大量变 **tofu 方块**；段落/列表**行高塌陷、上下行互相压字**；表格「说明」列溢出裁切 | `_scratch/arc-md-demo-tab1.png` |
| B | **Arc HEAD**（源码 ProjectReference，含当前未提交改动） | **与 A 几乎完全一致**（未修复） | `_scratch/arc-md-head-tab1.png` |
| C | HEAD + `.WithInterFont()` | 拉丁字符变 Inter，**中文全部 tofu**，叠层更严重 | `_scratch/arc-md-fontfix-tab1.png` |
| D | HEAD + `ArcMarkdownStyle.Current{BodyFont=DisplayFont=sans-serif, MonoFont=monospace}` | **编译通过，装机验证未完成**：emulator 被其他任务占用（ANR 弹窗）后掉线，恢复后 `package` 服务仍异常（`Failure calling service package: Broken pipe (32)`），两次安装失败 | `_scratch/build-chat-demo-style.log` |

**同一现象与产品自研替换的记载完全吻合**：`app/src/DshRemote.App/Controls/MarkdownBodyView.cs` 类注释写着 "on Android/Skia that control drew long wrapped paragraphs with overlapping lines (multiple text layouts stacked in one block)"。

### 2.3 根因判断（有依据，但未 100% 闭环）

- Arc 主题把 `ArcFontBody` 解析为 **"Inter"**，并在 `tools/ThemeReport.cs:157` 硬校验必须是 Inter；渲染器又从 `ArcMarkdownStyle.BodyFont = ArcResources.Font(ArcTokens.FontBody)` 显式给每个 `TextBlock` 设 `FontFamily`。
- **Inter 不含 CJK**。产品 Android 宿主（`DshRemote.App.Android/MainActivity.cs`）**从未调用 `.WithInterFont()`，也没有配 `FontManagerOptions.FontFallbacks`**：
  - 组 A 的情形：Inter 未注册 → Skia 逐段回退，中文回退到的字体度量异常 → 行高塌陷 + 部分字形缺失；
  - 组 C 的情形：Inter 注册成功 → 整段用 Inter，中文无 fallback → **全 tofu**。
- 结论：**这不是"Markdown 解析/布局"算法 bug，而是"中文字体解析 + 度量"缺陷**；Arc 的组件把 `FontFamily` 钉在某一个拉丁字体上，Android 端没有 CJK fallback 时必然出问题。Windows 桌面有系统 CJK 回退，所以桌面看不出来。
- 组 D 就是把 `ArcMarkdownStyle` 的三个字体指到 Android 系统族（`sans-serif` / `monospace`）的尝试；**编译通过**，只差一次真机截图确认（命令见 §7）。

### 2.4 给 Arc 的 bug 单（可直接转上游）

- 复现：`_scratch/android-chat-demo`（`dotnet build -f net10.0-android` → 安装 → 首 tab 即是）。
- 最小复现代码：`MainView.axaml` 一个 `ArcMarkdownView` + `MainView.axaml.cs` 里 `view.Markdown = Samples.LongMarkdown`（含中文段落 + 表格 + 代码块）。
- 期望：① 中文正常字形；② 换行段落行距 = 正常行高，不互相压字；③ 表格按容器宽度分配列宽，超出时横向滚动而不是裁切。
- 建议修法（按优先级）：
  1. `ArcMarkdownStyle` 增加 `FontFallbacks`（或 `BodyFont` 由宿主按平台覆盖）+ 文档写明 Android 必须配 CJK fallback；
  2. 渲染器不硬编码单一 `FontFamily`，至少给 `ArcTokens.FontBody` 的解析加平台回退（Android → `sans-serif`，Windows → `Segoe UI` 等）；
  3. 表格列宽改为比例分配 + `MaxWidth` 约束（当前溢出）。
- 复现证据：`_scratch/arc-md-demo-tab1.png`、`_scratch/arc-md-head-tab1.png`、`_scratch/arc-md-fontfix-tab1.png`。

---

## 3. `Arc.Avalonia.Agent` 盘点与「DSH 事件 → Arc 事件」映射

### 3.1 组件（0.9.0-alpha 有 / HEAD 新增）

| 类型 | 说明 |
|---|---|
| `ArcAgentConversation`（**HEAD 新增，手机首选**） | `Panel, IDisposable`；`Workspace`、`Session`、`MarkdownOptions`、`ShowHeader`、`Title`、`EmptyText`；`Timeline`（只读）；事件 `ApprovalDecisionRequested`、`AnswerRequested`、`RetryRequested`、`InteractionOccurred`、`Warning`；`Refresh()`、`ScrollToEnd()`。**它就是一个把 session 时间线渲染成「气泡 + 全宽事件卡」的 transcript**，比 `ArcAgentSurface`（桌面组合根，写死高度）更适合手机。 |
| `ArcAgentSurface` | 桌面组合根：RunBar + Transcript(Height=340) + ApprovalTray + Dock + AttentionInbox + Composer，内部写死高度，手机需自建布局。 |
| `ArcAgentToolCallCard` | `Call`(`ArcToolCall`)、`ApproveRequested/DenyRequested/RetryRequested/DetailsRequested`（带 callId）。`ArcToolCall` 字段：`Id/ToolName/Intent/Risk/Status/Arguments/ResultSummary`。 |
| `ArcAgentApprovalTray` | `Queue`(`ArcApprovalQueue`)、`DecisionRequested(ArcApprovalDecision)`。 |
| `ArcToolApprovalSheet` | `ToolName/Intent/Risk/Arguments/PreviewLines/ExpiresInSeconds/Scope/State` + `Approval/Deny`。 |
| `ArcAgentAskCard` | `Question/Options/AllowMultiple/AllowFreeText/State(Question/Answered/Skipped/Deferred/Expired)` + `AskSubmitted/AskDeferred`。 |
| `ArcAgentChoiceCard` / `ArcAgentConfirmCard` | `Model`(`ArcAgentChoiceModel/ConfirmModel`) + `IsInteractive`。 |
| `ArcAgentItemViewRegistry` | **自定义行扩展点**：`Register<T>(Func<T, Control?>)` / `Register(Type, Func<object, Control?>)` / `Resolve(object)` / `CreateTemplate()`（返回 `IDataTemplate`）。默认已注册消息/工具/审批/artifact 视图。 |
| `ArcAgentJson` | `Parse(string?)`(`ArcAgentPayloadResult`)、`Normalize`、`CreateComponentId`；限制 `MaxPayloadChars=256KB`、`MaxDepth=32`、`MaxOptionCount=200`。 |
| `ArcAgentMarkdownPlugin` | 注册 `pura-agent` fence（`choice`/`confirm` 组件），把 JSON 围栏渲染成 `ArcAgentChoiceCard`/`ConfirmCard`；`ArcAgentMarkdown.DefaultOptions` 是带该插件的 options。 |
| 归约器 | `ArcAgentWorkspace`/`IArcAgentSession`（`Messages/ToolCalls/Steps/Timeline/Snapshot`）、`ArcDeltaCoalescer`（`Enqueue` / `Flush` / `maxBatchSize=2048` / Dispatcher 合批）、`ArcApprovalQueue`/`ArcAttentionQueue`/`ArcArtifactIndex`。 |

### 3.2 DSH 事件 → Arc 事件映射表

| DSH 事件（`docs/protocol-v1.md`） | Arc 事件（`Arc.Avalonia.Agent`） | 渲染位置 |
|---|---|---|
| `turn/start`（或 `session/state.active`） | `ArcRunStartedEvent(stamp, at, new ArcAgentRunRef(runId, sessionId))` | Conversation 头 / RunBar 状态 |
| `step/start` | `ArcStepStartedEvent(stamp, at, stepId, title)` | 步骤行（Timeline） |
| `step/end` | `ArcStepFinishedEvent(stamp, at, stepId, ArcStepStatus)` | 步骤行 |
| `assistant/message`（content 文本块，权威文本） | `ArcMessageStartedEvent` → `ArcMessageEndedEvent(..., Done)`；正文作为消息体（见 §4 伪代码用 `StateSnapshot` 或 Event 携带） | 助手气泡（Markdown） |
| assistant 流式 chunk（`chunk.text-delta`） | `ArcMessageDeltaEvent(stamp, at, messageId, delta)` | 流式气泡（`ArcMarkdownView.BeginStreaming` / Conversation 自动处理） |
| `assistant` reasoning delta | `ArcReasoningDeltaEvent(stamp, at, reasoningId, delta)` | 推理折叠行（当前 transcript 有 Thinking 行；独立折叠卡见 §5 缺口 2） |
| `tool/call` | `ArcToolCallStartedEvent(stamp, at, callId, toolName, intent, risk)` | `ArcAgentToolCallCard` |
| `tool/result` | `ArcToolCallEndedEvent(stamp, at, callId, status, ...)`（错误 → `ArcToolCallStatus.Failed`） | 同上（结果区） |
| `approval/request`（waterfall） | `ArcApprovalRequestedEvent(stamp, at, ArcApprovalRequest{...})` | `ArcAgentApprovalTray` / `ArcToolApprovalSheet` |
| `approval/asked`、`approval/decided` | `ArcApprovalResolvedEvent(stamp, at, requestId, decision)` | 托盘消解 |
| `user-questions/request` | `ArcQuestionRequestedEvent(stamp, at, ArcQuestionRequest{...})` | `ArcAgentAskCard` |
| 问答应答（本地 UI） | `ArcAnswerCommand(runId, ArcQuestionAnswer)` → `IArcAgentTransport.SendCommandAsync` | — |
| `turn/end`（`data.reason.kind='error'` 表示失败） | `ArcRunPhaseChangedEvent(stamp, at, ArcRunPhase.Done/Failed/Stopped)` ⚠ `sink.Complete/Fail` 在 Surface 是空实现 | RunBar / 状态徽章 |
| `user/message` | `ArcMessageStartedEvent(Role.User)` + `ArcMessageEndedEvent` | 用户气泡（右侧） |
| `session/state.queue[]`（queued/steering/context） | 无内建 → `ArcCustomEvent(stamp, at, "queue", json)` 或自绘（§5 缺口 3） | 排队条 |
| 图片块（`content[]{type:image}`） | 无内建 → `ArcArtifactPublishedEvent` 或 `ArcCustomEvent` + 自绘图（§5 缺口 5） | 图片缩略图 |
| `system`/`developer` 行 | `ArcMessageStartedEvent(Role.System)` 或 Custom | 系统行 |
| 隐藏/过滤事件（app 现有 `HiddenEventsText`） | `ArcCustomEvent(..., "hidden", ...)` | 提示文本 |

> **事件构造要点**：`ArcEventStamp.Create(runId, seq, eventId)`；每个 run 必须先 `ArcRunStartedEvent`，否则进 orphan 缓冲（≤256 后丢弃）；`EventId` 幂等去重；序列缺口缓冲补投。这些在前次调研（`docs/research/arc-android.md`）已验证。

---

## 4. 落地伪代码（DSH 宿主 → Arc Agent 渲染）

### 4.1 适配器（`DshArcEventMapper`，新增 ~120 行）

```csharp
public sealed class DshArcEventMapper
{
    private readonly ArcAgentWorkspace _workspace = new();
    private readonly IArcAgentEventSink _sink;      // Surface/Conversation 内部 sink，或自建

    public void OnDshEvent(DshEvent e)               // 从 DshClient.Follow 的事件回调进来
    {
        var stamp = ArcEventStamp.Create(e.RunId, e.Seq, e.EventId);
        ArcAgentEvent arc = e.Type switch
        {
            "turn/start"         => new ArcRunStartedEvent(stamp, e.At, new ArcAgentRunRef(e.RunId, e.SessionId)),
            "step/start"         => new ArcStepStartedEvent(stamp, e.At, e.StepId, e.Title),
            "step/end"           => new ArcStepFinishedEvent(stamp, e.At, e.StepId, Map(e.Status)),
            "assistant/message"  => new ArcMessageEndedEvent(stamp, e.At, e.MessageId, ArcMessageStatus.Done),
            "tool/call"          => new ArcToolCallStartedEvent(stamp, e.At, e.CallId, e.ToolName, e.Intent, Map(e.Risk)),
            "tool/result"        => new ArcToolCallEndedEvent(stamp, e.At, e.CallId, Map(e.Status), e.Output),
            "approval/request"   => new ArcApprovalRequestedEvent(stamp, e.At, e.ToApprovalRequest()),
            "user-questions/request" => new ArcQuestionRequestedEvent(stamp, e.At, e.ToQuestionRequest()),
            "turn/end"           => new ArcRunPhaseChangedEvent(stamp, e.At, e.IsError ? ArcRunPhase.Failed : ArcRunPhase.Done),
            "user/message"       => new ArcMessageStartedEvent(stamp, e.At, e.MessageId, ArcAgentRole.User),
            _                    => new ArcCustomEvent(stamp, e.At, e.Type, e.RawJson)
        };
        _sink.Push(arc);                              // 线程安全；rebatch 后由 Dispatcher 刷 UI
    }

    // 流式文本（assistant/message 之前的增量事件）
    public void OnDelta(DshDelta d) =>
        _sink.Push(new ArcMessageDeltaEvent(ArcEventStamp.Create(d.RunId, d.Seq, d.EventId), d.At, d.MessageId, d.Text));

    public void OnReasoning(DshDelta d) =>
        _sink.Push(new ArcReasoningDeltaEvent(ArcEventStamp.Create(d.RunId, d.Seq, d.EventId), d.At, d.ReasoningId, d.Text));
}
```

### 4.2 命令回程（`IArcAgentTransport`，复用前次调研模板）

```csharp
public Task<string> StartTurnAsync(ArcAgentTurnRequest request, IArcAgentEventSink sink, CancellationToken ct)
    => _dsh.SendPromptAsync(_sessionId, request.Text, request.Attachments, steer: false, ct); // 返回 runId

public Task SendCommandAsync(ArcAgentCommand command, CancellationToken ct) => command switch
{
    ArcStopCommand c        => _dsh.StopAsync(c.RunId, ct),
    ArcSteerCommand c       => _dsh.SendPromptAsync(_sessionId, c.Instruction, null, steer: true, ct),
    ArcApprovalCommand c    => _dsh.AnswerApprovalAsync(c.Decision, ct),   // allowed-once / rejected / cancelled
    ArcAnswerCommand c      => _dsh.AnswerQuestionAsync(c.Answer, ct),
    ArcPauseCommand c       => _dsh.PauseAsync(c.RunId, ct),
    ArcResumeCommand c      => _dsh.ResumeAsync(c.RunId, ct),
    _ => Task.CompletedTask
};
```

### 4.3 手机端布局（推荐：`ArcAgentConversation` + 自有底栏）

```xml
<UserControl xmlns:agent="using:Arc.Avalonia.Agent"
             xmlns:agentui="using:Arc.Avalonia.AgentUI.Controls">
  <Grid RowDefinitions="Auto,*,Auto">
    <agentui:ArcRunControlBar Grid.Row="0" Status="Running" CurrentStep="{Binding StepText}" />

    <!-- 会话时间线：气泡 + 工具卡 + 审批/问答卡，自带虚拟化与自动滚动 -->
    <agent:ArcAgentConversation Grid.Row="1"
                                Workspace="{Binding Workspace}"
                                Session="{Binding ActiveSession}"
                                Title="{Binding Title}"
                                ShowHeader="False"
                                ApprovalDecisionRequested="OnApproval"
                                AnswerRequested="OnAnswer"
                                RetryRequested="OnRetry" />

    <!-- 底栏：排队提示 + 审批托盘 + 输入框（Arc 没有手机断点布局，需要自己拼） -->
    <StackPanel Grid.Row="2">
      <agent:ArcAgentApprovalTray x:Name="Tray" Queue="{Binding Workspace.Approvals}" />
      <agentui:ArcAgentComposer x:Name="Composer" />
    </StackPanel>
  </Grid>
</UserControl>
```

> Chat 层若要用（例如做“纯聊天”模式）：`<chat:ArcChatView x:Name="Chat" ShowComposer="True" AutoScrollMode="NearBottom"/>` + `Chat.Messages.Add(new ArcChatMessage{ Role=..., Markdown=..., Status=...})` + `Chat.BeginStreamingMessage(msg)` 流式。**但行模板不可扩展**，工具卡放不进去。

---

## 5. 缺口清单（逐条：缺什么 · 建议加在哪个包 · API 草案）

> Lead 可直接转给用户/上游；每条都标注了我们绕不过去的原因。

| # | 缺口 | 现状证据 | 建议归属 | API 草案 |
|---|---|---|---|---|
| 1 | **工具卡字段不足**：无耗时、无错误态、无完整结果正文、无结果图片。DSH `tool/result` 有 `output`/`isError`/时间戳 | `ArcToolCall` 只有 `Id/ToolName/Intent/Risk/Status/Arguments/ResultSummary`（`Events/ArcAgentEvents.cs`） | `Arc.Avalonia.Agent`（模型）+ `AgentUI`（卡） | `ArcToolCall` 增 `DateTimeOffset? StartedAt/FinishedAt`、`bool IsError`、`string? ResultBody`、`IReadOnlyList<ArcArtifact> Images`；`ArcAgentToolCallCard` 增 `ShowResultBody`、`Duration` 显示策略 |
| 2 | **推理折叠块缺失**：没有独立「思考过程」折叠控件，只有 transcript 的 Thinking 行（不可展开全文、无耗时） | `grep class Arc*Reasoning*` 无结果；产品靠 `ChatMessageViewModel.IsReasoningExpanded` 自绘 | `Arc.Avalonia.AgentUI` | `ArcAgentReasoningBlock : Panel { string Text; bool IsExpanded; bool IsStreaming; double? ElapsedSeconds; string Header => "思考过程"; event Toggled; }`（流式时折叠显示摘要，完成展开全文） |
| 3 | **排队 / steer 列表缺失**：`session/state.queue[]`（queued/steering/context）无组件，编辑/删除/转 steer 全要自绘 | 产品 `ChatView.axaml` 自绘队列行 + `RemoveCommand/SteerNowCommand` | `Arc.Avalonia.AgentUI` | `ArcAgentQueueList : Panel { IEnumerable<ArcAgentQueueItem> Items; event EditRequested(id); event RemoveRequested(id); event SteerRequested(id); }`，`ArcAgentQueueItem{ Id, Placement(Queued/Steering/Context), Markdown, IsEditable }` |
| 4 | **Chat 行不可自定义**：`ArcChatView` 行模板私有（`BuildItemTemplate`），无法注入工具卡/系统行 | `ArcChatView.cs:710` 私有 `FuncDataTemplate` | `Arc.Avalonia.Chat` | `public IDataTemplate? RowTemplate { get; set; }` 或 `public Func<ArcChatMessage, Control?>? RowFactory`；同时把 `ArcChatRow` 的模板暴露为 `ControlTheme` |
| 5 | **图片块缺失**：Agent 事件模型没有"消息图片"概念（DSH `user/message`、`tool/result` 都带 image content）；Chat 有 `ArcChatAttachment.IsImage` 但无缩略图/全屏控件 | `ArcChatAttachment` 只有 Name/Size/Url/IsImage；Agent 无对应 | `Arc.Avalonia.Agent` + `AgentUI` | `ArcAgentImageBlock : Panel { IReadOnlyList<ArcAgentImage> Images; double MaxHeight; }`，`ArcAgentImage{ Id, Uri?, Bytes?, Width/Height, Caption }`；`ArcAgentMessageEndedEvent` 增 `IReadOnlyList<ArcAgentImage> Images` |
| 6 | **Markdown Android 渲染缺陷**（见 §2）：CJK tofu / 行高塌陷叠层 / 表格溢出 | 三组真机截图 | `Arc.Avalonia.Markdown` | `ArcMarkdownStyle.FontFallbacks` 或按平台解析 `ArcTokens.FontBody`；表格列宽比例化 |
| 7 | **Agent 层无手机断点**：`ArcAgentSurface` 写死 `transcript.Height=340`、`attentionInbox.Height=220`；`ArcAgentConversation` 可用但仍需外层自拼底栏/安全区/IME | 前次调研 + `ArcAgentSurface.cs:41-42` | `Arc.Avalonia.Agent` | `ArcAgentSurface` 增加 `LayoutMode(Desktop/Mobile)` 或干脆让手机用 `ArcAgentConversation`（推荐后者） |

**已够用、可直接复用的部分**：`ArcAgentConversation`（HEAD）、`ArcAgentToolCallCard` 骨架、`ArcAgentApprovalTray`/`ArcToolApprovalSheet`、`ArcAgentAskCard`/`ArcAgentChoiceCard`、`ArcAgentItemViewRegistry`（自定义行）、`ArcAgentJson`、`ArcAgentMarkdownPlugin`（`pura-agent` fence）、事件归约器与 `ArcDeltaCoalescer`、`ArcMarkdownStreamingSession`、`ArcChatView` 的虚拟化/自动滚动实现思路。

---

## 6. App 侧改造清单（若 Arc 缺口补齐后）

### 6.1 保留（不动）

- `DshRemote.Core`：全部协议解析 / `DshClient.Follow` / 重连 / TLS；
- `ChatMessageViewModel` 的**字段与语义**（`Kind/Time/Seq/CallId/Text/TargetText/ReasoningText/ToolName/ArgumentsJson/ResultText/StatusText/IsError/DurationText/Images/IsStreaming`）——它其实就是 Arc `ArcToolCall`/`ArcAgentMessage` 的并集，改造成 adapter 即可；
- `InteractionHub` / `InteractionViewModels`（审批/问答状态机）；
- `ChatImagePartViewModel` + 图片模板（Arc 无图片控件，§5-5 前保留）。

### 6.2 替换 / 删除

| 现状 | 替换为 | 备注 |
|---|---|---|
| `Controls/MarkdownBodyView.cs`（自研 Markdown） | `ArcMarkdownView`（**待 §2 修复**）或先保留 + 只换外层行 | 修复验证前不要换 |
| `Views/ChatView.axaml` 的 `ItemsControl` + `DataTemplate`（300 行：气泡/推理/工具卡/状态） | `ArcAgentConversation`（HEAD）+ 少量自绘底栏 | 可删约 250 行模板 |
| `Bubble`/`tool-toggle`/`reasoning-header` 样式（`AppStyles.axaml`） | Arc token 主题 | 需保留 `Classes="bubble"` 的少量差异样式 |
| `MessagesScroll` 手工滚动 | `ArcVirtualList`/Conversation 自带 | 队列/公告行仍自绘 |

### 6.3 新增

1. `DshArcEventMapper`（DSH 事件 → `ArcAgentEvent`，§4.1）；
2. `DshArcTransport : IArcAgentTransport`（§4.2，包装 `DshClient`）；
3. `DshAgentServices : IArcAgentServices`（打开链接 / 复制 / 确认 → Android Intent / Clipboard）；
4. `ArcAgentItemViewRegistry` 注册：DSH 隐藏事件、queue 行、图片行（在缺口补齐前用 `CustomEvent` + 自绘控件兜底）；
5. `MainView` → `TabControl` 或分区：会话（Conversation）+ 审批/问答（Tray/AskCard）+ 输入（Composer）。

### 6.4 模板/绑定示例（工具卡用 registry 注入）

```csharp
var registry = new ArcAgentItemViewRegistry();
registry.Register<ArcToolCall>(call => new ArcAgentToolCallCard { Call = call });
registry.Register<DshQueueItem>(q => new DshQueueRow { DataContext = q });   // 自绘
conversation.ItemViewRegistry = registry;                                    // 若 Conversation 暴露；否则用其默认
```

---

## 7. Android 验证步骤（可复现）

```powershell
# 环境（本机私有 SDK，必须显式指定；ASCII 路径）
$sdk='-p:AndroidSdkDirectory=D:\SmartBillAndroidSdk'
$java='-p:JavaSdkDirectory=C:\Program Files\Java\jdk-21'
adb devices                                  # 期望 emulator-5556 device / TestDevice

cd D:\Code\VibeCoding\DSHRemote
dotnet build _scratch\android-chat-demo\ArcAndroidDemo.csproj -c Debug $sdk $java -v m
adb -s emulator-5556 install -r -t _scratch\android-chat-demo\bin\Debug\net10.0-android\dev.dshremote.arcchatdemo-Signed.apk
adb -s emulator-5556 shell monkey -p dev.dshremote.arcchatdemo -c android.intent.category.LAUNCHER 1
adb -s emulator-5556 exec-out screencap -p > _scratch\verify.png
```

- 首 tab = `ArcMarkdownView`（长中文 Markdown）；次 tab = `ArcChatView`（3 条消息：user / 长 assistant / streaming）。
- 判定标准：中文正常字形、段落行距不重叠、表格不裁切、滚动到底无重影。
- 若要复验组 D（CJK 字体修法）：`_scratch/android-chat-demo/App.axaml.cs` 里 `ArcMarkdownStyle.Current` 已设 `sans-serif`，直接 build+install 截图即可。
- demo 的 `ArcChatView` 需要 **Arc HEAD 源码 ProjectReference**（`0.9.0-alpha` 里叫 `ArcMarkdownChatView`）；若要回到 NuGet 版本，把 `MainView.axaml` 换成 `md:ArcMarkdownChatView`。

---

## 8. 风险与备注

| 风险 | 说明 |
|---|---|
| **Arc 仓库正在被别人改** | 调研期间工作区出现未提交改动（`ArcMarkdownView.cs`、`ArcChatView.cs`、`ArcAgentConversation.cs`、新增 `ArcTouch.cs`、`ArcPalette/ArcTokens/ArcThemeOptions` 等，共 11+ 文件），且一度编译失败（`ArcTouch.cs` 用了 `Control.Padding`）。这些改动是**触摸/滚动链/触摸目标**适配，**不修 §2 的中文与叠层**。接入时请 pin 确切 commit。 |
| **Chat 包未发布** | `Arc.Avalonia.Chat` 不在本地 NuGet（只有 `arc.avalonia.* 0.9.0-alpha`，不含 Chat）。要用 Chat 层必须 ProjectReference 或让 Arc 重新 `pack`。 |
| **HEAD 与 0.9.0-alpha 差异** | `ArcMarkdownChatView` → `ArcChatView`；`ArcAgentConversation` 新增。产品升级需同步改引用。 |
| **emulator 共享** | 验证期间 emulator-5556 被其他任务占用（出现「我的账单 / DSH Remote isn't responding」ANR 弹窗）并中途掉线（`adb: device offline`），组 D 的最终截图未完成。 |
| **Agent 事件不带 Markdown 正文** | `ArcMessageEndedEvent` 没有 text 字段；正文要靠 `ArcMessageDeltaEvent` 累积或 `ArcStateSnapshotEvent`。若 DSH 只在 `assistant/message` 给全文（重连后常见），需要走 snapshot 或 `ArcCustomEvent`——这是接入时要先定的契约细节。 |

---

## 9. 证据文件索引

| 用途 | 路径 |
|---|---|
| Android demo（ArcMarkdownView + ArcChatView） | `_scratch/android-chat-demo/`（`ArcAndroidDemo.csproj`、`MainView.axaml(.cs)`、`App.axaml.cs`、`Samples.cs`） |
| 组 A 截图（0.9.0-alpha，坏） | `_scratch/arc-md-demo-tab1.png` |
| 组 B 截图（Arc HEAD，坏） | `_scratch/arc-md-head-tab1.png` |
| 组 C 截图（+InterFont，更坏） | `_scratch/arc-md-fontfix-tab1.png` |
| 组 D 构建日志（CJK 字体修法，编译通过） | `_scratch/build-chat-demo-style.log` |
| 各轮构建日志 | `_scratch/build-chat-demo*.log` |
| Arc Chat 源码 | `Arc.Avalonia\src\Components\Arc.Avalonia.Chat\`（Controls/Models/Themes） |
| Arc Markdown 源码 | `...\src\Components\Arc.Avalonia.Markdown\`（Controls/Rendering/Streaming/Themes） |
| Arc Agent 源码 | `...\src\Agent\Arc.Avalonia.Agent\`（Controls/Events/Hosting/Views/Streaming/Workspace） |
| 产品当前渲染实现 | `app\src\DshRemote.App\Controls\MarkdownBodyView.cs`、`Views\ChatView.axaml`、`ViewModels\ChatMessageViewModel.cs` |
| 协议契约 | `docs\protocol-v1.md`（事件名与字段） |
| 前次 Android 工具链调研 | `docs\research\arc-android.md` |
