# Arc.Avalonia Agent 组件 × Android 可行性调研

> 调研员：`arc-android`（只读 Arc 仓库 + `_scratch/` 受控构建验证）
> 时间：2026-11-08 23:2x–23:4x（Asia/Shanghai）
> Arc 仓库：`D:\Code\VibeCoding\Arc.Avalonia`（commit `6b4619d`，工作区干净，**未做任何修改**）
> 结论一句话：**`net10.0-android` 可构建、APK 已成功构建并装到 TestDevice 上跑起来；Arc Agent 组件可原样跑在 Android 上，但「宿主接入层 + 移动端适配（安全区/IME/返回键/窄屏布局）」全部需要自写。**

---

## 0. 结论速览

| 问题 | 结论 | 证据 |
|---|---|---|
| `net10.0-android` 目标可用？ | **可用**。`dotnet build` 0 error 0 warning，两轮共 ~3.5 min | `_scratch/build-bare.log`、`_scratch/build-arc.log` |
| APK 能构建？ | **能**。Debug 全 ABI APK：裸 Avalonia **70.9 MB**，+Arc 全家桶 **75.3 MB** | `_scratch/android-*/bin/Debug/net10.0-android/*-Signed.apk` |
| 能装能跑？ | **能**。TestDevice（无线 adb）在线，两个包都 `Success` 安装；Arc 版冷启动 `Displayed +1s205ms`，界面正常渲染 | `_scratch/arc-probe-device.png` |
| Arc 有 Android 样例/移动端适配？ | **没有**。全仓库单一 `net10.0` TFM、无 android 工程、无 SafeArea/IME/BackRequested 代码 | `Directory.Build.props:4`、全仓 grep（§5） |
| 兄弟项目已有先例？ | **有**。`我的账单APP` 用 `Arc.Avalonia 0.9.0-alpha` + `ArcTheme` 跑 Android（Debug APK 78.8 MB），证明 Arc 主题/图标/Markdown 在 Android 生产可用 | `我的账单APP/src/SmartBill.Mobile/SmartBill.Mobile.csproj:12`、`docs/I17-产品改进盘点.md:216` |

---

## 1. 工具链现状（全部实测）

### 1.1 版本

| 项 | 值 | 来源 |
|---|---|---|
| .NET SDK | **10.0.114**（MSBuild 18.0.36，Host 10.0.14） | `dotnet --info` |
| Android workload | **`android` 36.1.118 / 10.0.100**（Msi 安装源，已安装） | `dotnet workload list` |
| JDK | **21.0.1 LTS**，`JAVA_HOME=C:\Program Files\Java\jdk-21` | `java -version`、`$env:JAVA_HOME` |
| Android SDK（可用） | **`D:\SmartBillAndroidSdk`** → `platforms/android-36`、`build-tools/36.0.0`、`platform-tools` | `ls D:\SmartBillAndroidSdk` |
| Android SDK（默认位置，**不可用**） | `C:\Users\user\AppData\Local\Android\Sdk` 只有 `android-33` + `build-tools/33.0.1` | `ls ...\Sdk\platforms` |
| `ANDROID_HOME` | **未设置**（`$env:ANDROID_HOME` 空） | 实测 |
| 手机 | **TestDevice 在线**：`adb-SERIAL._adb-tls-connect._tcp device product:TestDevice model:TestDevice` | `adb devices -l` |
| NuGet 全局缓存 | `D:\.nuget\packages`；`avalonia.android 12.1.4/12.1.5`、`arc.* 0.9.0-alpha` 全部已在本地 | `dotnet nuget locals global-packages --list` |

**两条硬性注意点（踩过的坑）**：
1. **必须显式传 `-p:AndroidSdkDirectory=D:\SmartBillAndroidSdk`**。`ANDROID_HOME` 为空且默认 SDK 缺 API 36，裸 `dotnet build -f net10.0-android` 会因找不到符合要求的 SDK 失败。
2. **SDK 路径必须是纯 ASCII**（`zipalign` 在含中文路径下报 `Unable to open ... as zip archive`，见 `我的账单APP/tools/android/build-apk.ps1` 注释）。`_scratch/` 在 ASCII 路径下，安全。

### 1.2 可复现构建命令

```powershell
cd D:\Code\VibeCoding\DSHRemote
$sdk='-p:AndroidSdkDirectory=D:\SmartBillAndroidSdk'
$java='-p:JavaSdkDirectory=C:\Program Files\Java\jdk-21'
dotnet build _scratch\android-bare\AndroidBare.csproj -c Debug $sdk $java -v m   # 1m53s, 0 error
dotnet build _scratch\android-arc\AndroidArc.csproj   -c Debug $sdk $java -v m   # 2m13s, 0 error
adb install -r -t _scratch\android-arc\bin\Debug\net10.0-android\dev.dshremote.probearc-Signed.apk
```

### 1.3 实测产物

| 探针 | 内容 | Debug APK | 构建 | 装机/启动 |
|---|---|---|---|---|
| `_scratch/android-bare` | 纯 Avalonia 12.1.4 + Fluent，无 Arc | **70.9 MB** | ✅ 1m53s | ✅ 安装 Success |
| `_scratch/android-arc` | 同上 + `Arc.Avalonia`/`Arc.Avalonia.Agent 0.9.0-alpha`，`<arc:ArcTheme/>` + `<agent:ArcAgentSurface/>` + 4 个 AgentUI 叶子控件 | **75.3 MB**（**Arc 净增 4.4 MB**） | ✅ 2m13s | ✅ 冷启动 `Displayed ... +1s205ms` |

截图（真机 1272×2800）：`_scratch/arc-probe-device.png` —— 运行条、Transcript、Approval Tray（含 `shell.exe` / `High risk` / `Apply the migration patch` 示例数据）全部正常渲染，暗色 + Violet accent 生效。

> Debug 包体积说明：`AndroidLinkMode=None` + `EmbedAssembliesIntoApk=true` + **默认全 4 个 ABI**。这是「可 adb 直装」的调试配置，不代表发布体积（见 §7）。

### 1.4 运行时必踩的坑（已在探针中修好，写进宿主模板）

**必须有 AppCompat 主题**，否则进程秒崩：

```
FATAL EXCEPTION: main
Process: dev.dshremote.probearc
java.lang.RuntimeException: Unable to start activity ... MainActivity:
  java.lang.IllegalStateException: You need to use a Theme.AppCompat theme (or descendant) with this activity.
```

原因：`AvaloniaMainActivity` 继承 `AppCompatActivity`。修复（`_scratch/android-arc/Resources/values/styles.xml`）：

```xml
<style name="DshProbeTheme" parent="@style/Theme.AppCompat.Light.NoActionBar">
  <item name="android:windowActionBar">false</item>
  <item name="android:windowNoTitle">true</item>
</style>
```

并在 Activity 上标 `Theme = "@style/DshProbeTheme"`。另需 `EmbedAssembliesIntoApk=true`，否则手动 `adb install` 的 Debug APK 会因找不到 assemblies 直接退出（兄弟项目已验证，见其 csproj 注释）。

---

## 2. Arc 组件清单与真实 API

### 2.1 工程事实

| 项 | 事实 |
|---|---|
| TargetFrameworks | **单一 `net10.0`**（`Directory.Build.props:4`），全仓 50 个 csproj 无 `-android`/`-ios` 多目标 |
| Avalonia | **12.1.4**（`Directory.Packages.props`），CPM 中央版本管理，第三方 UI 库为零（R7 门禁） |
| Arc Agent 分布 | 两个包：`src/Agent/Arc.Avalonia.AgentUI`（叶子控件，L4，100 KB）与 `src/Agent/Arc.Avalonia.Agent`（组合根 + 表现运行时，185 KB） |
| 分发形态 | 本地 NuGet 已有 **`0.9.0-alpha` 的 19 个 `arc.*` 包**（`D:\.nuget\packages`），`Arc.Avalonia` 元包把 Agent 一起带上 |
| Android 先例 | **无** Android 工程/无 `Avalonia.Android` 引用；Gallery/样例全是 Desktop |

### 2.2 组件清单（命名空间 · 基类 · 关键 API）

**`Arc.Avalonia.Agent`（组合根）**

| 控件/类型 | 基类 | 关键 API |
|---|---|---|
| `ArcAgentSurface` | `Panel, IArcAgentCommandSink` | `Workspace` / `Transport` / `Services` / `ViewRegistry` / `ShowAttentionInbox` / `ShowBackgroundDock`；`Attach(workspace, transport?, services?)`；`StartTurnAsync(text, model?)`；`SendAsync(command)`；`CommandRequested`/`CommandRaised`（普通事件 + RoutedEvent） |
| `ArcAgentComposer` | `Panel` | `SubmitRequested`(`ArcComposerSubmit(Text, Model?)`)、`StopRequested`、`IsRunning`、`Text`、`Clear()`、`FocusInput()`；**Enter 提交、Shift+Enter 换行**（`KeyDown` 隧道处理） |
| `ArcAgentStreamingText` | `Panel` | `Text`、`IsStreaming`、`Append(delta)`、`SetText`、`Reset` |
| `ArcAgentToolCallCard` | `Panel` | `Call`(`ArcToolCall`)、`ApproveRequested`/`DenyRequested`/`RetryRequested`/`DetailsRequested`（均带 callId） |
| `ArcAgentApprovalTray` | `Panel` | `Queue`(`ArcApprovalQueue`)、`DecisionRequested`(`ArcApprovalDecision`) |
| `ArcAgentChoiceCard` | `TemplatedControl` | `Model`(`ArcAgentChoiceModel`)、`IsInteractive` |
| `ArcAgentConfirmCard` | `TemplatedControl` | `Model`(`ArcAgentConfirmModel`)、`IsInteractive` |

**`Arc.Avalonia.AgentUI.Controls`（叶子控件）**

| 控件 | 关键 StyledProperty（XAML 可直接写） | 事件 |
|---|---|---|
| `ArcAgentTranscript` | `Events`(`IEnumerable<ArcAgentEvent>`)、`Status`、`IsStreaming`、`EmptyText`、`MarkdownOptions`；增量 API `Append/AppendRange/AppendOrUpdate/ResetIncremental`；计数器 `FullRebuildCount/RenderedRowCount/TotalEventCount/DroppedRowCount/ViewportRows/MaxRenderedRows` | `RetryRequested` |
| `ArcAgentAskCard` | `Question`、`Options`(`ArcAgentAskOption[]`)、`AllowMultiple`、`AllowFreeText`、`State`(`Question/Answered/Skipped/Deferred/Expired`) | `AskSubmitted`、`AskDeferred` |
| `ArcRunControlBar` | `Status`(`ArcRunBarStatus`)、`CurrentStep`、`StepLabel`、`ElapsedSeconds` | `Stop`/`StopAfterStep`/`Pause`/`Resume`/`Steer`/`Takeover` |
| `ArcToolApprovalSheet` | `ToolName`、`Intent`、`Risk`(`ArcApprovalRisk`)、`Arguments`、`PreviewLines`、`ExpiresInSeconds`、`Scope`、`State` | `Approval`/`Deny` |
| `ArcBackgroundRunDock` | `Status`(`ArcDockStatus`)、`TaskLabel`、`Progress` | `Pause`/`Stop`/`Expand` |
| `ArcAgentAttentionInbox` | （组合进 Surface） | `ItemActionRequested`、`Dismissed` |

> **API 陷阱 1：存在两个同名 `ArcAgentEvent` / `ArcAgentEventKind`**
> - 归约器事件：`Arc.Avalonia.Agent.ArcAgentEvent`（abstract record，`(ArcEventStamp Stamp, DateTimeOffset At)`，**19 种** Kind：`RunStarted/RunPhaseChanged/Step*/Message*/ReasoningDelta/Tool*/Approval*/Question/Attention/Artifact/State*/Custom`）
> - Transcript 行事件：`Arc.Avalonia.AgentUI.Controls.ArcAgentEvent`（扁平 class，`Id/Kind/Title/Detail/Status/Timestamp/IsUserMessage`，**8 种** Kind：`Message/Thinking/Tool/Plan/Approval/Ask/Result/Error`）
>
> `ArcAgentSurface` 自己就是用 `using UiEvent = Arc.Avalonia.AgentUI.Controls.ArcAgentEvent;` 区分（`ArcAgentSurface.cs:22`）。写 ViewModel 时**必须**用别名，否则 CS0104。
> 另外 `ArcAttentionKind`、`ArcApprovalRisk`/`ArcApprovalScope` 也有 Agent/AgentUI 两份同名枚举。

> **API 陷阱 2：`ArcAgentTranscript` 单独用要自己装 Markdown 工厂**
> `ArcAgentSurface` 的静态构造里做 `ArcAgentTranscript.DefaultMarkdownOptionsFactory ??= () => ArcAgentMarkdown.DefaultOptions;`（`ArcAgentSurface.cs:59-63`）。**只引用 AgentUI、不引用 Agent**的话，独立使用 Transcript 需自己设置这个静态属性，否则 Markdown 消息体退回纯文本。

> **API 陷阱 3：Agent 控件不需要额外样式 include**
> `ArcAgentUITheme.axaml` / `ArcAgentTheme.axaml` 内部是空资源字典（"composite P0 controls are built in C# and do not need per-control ControlThemes"）；`ArcTheme` 的 13 个 `StyleInclude` 里也**不含** Agent 主题。探针实测：只写 `<arc:ArcTheme/>` 即可让 Agent 控件正常上色。

### 2.3 数据模型 / 宿主接缝

| 类型 | 作用 |
|---|---|
| `ArcEventStamp(RunId, Sequence, EventId)` | 每个事件的归属与排序键；`ArcEventStamp.Create(runId, seq)` 自动派生 EventId |
| `ArcAgentRunRef(RunId, SessionId, Kind, ParentRunId?, Label?)` | run ↔ session；`Kind`：`Foreground/Background/SubAgent`（Dock 吃 Background，子 Agent 吃 SubAgent） |
| `IArcAgentWorkspace`（实现 `ArcAgentWorkspace`） | `Sessions`、`Active`、`Runs`、`BackgroundRuns`、`PendingApprovals`、`Approvals`、`Attention`、`Artifacts`、`GetSession(id)`、`Activate(id)`、`Apply(evt)`、`ApplyRange(evts)`、`Reset()`、`Changed` |
| `IArcAgentSession` | `SessionId`、`RunRef`、`Run`(`ArcAgentRunState`)、`Messages`、`ToolCalls`、`Steps`、`Timeline`、`Snapshot()`、`Changed` |
| `IArcAgentTransport` | `Task<string> StartTurnAsync(ArcAgentTurnRequest, IArcAgentEventSink, ct)`、`Task SendCommandAsync(ArcAgentCommand, ct)` |
| `IArcAgentEventSink` | `Push(evt)`、`Complete(runId, phase)`、`Fail(runId, msg)` —— **⚠ 在 `ArcAgentSurface` 里 `Complete`/`Fail` 是空实现**（源码注释：表现层不制造事件），相位必须靠宿主推 `ArcRunPhaseChangedEvent` |
| `IArcAgentCommandSink` | `SendAsync(ArcAgentCommand)` + `CommandRaised`；`ArcAgentSurface` 自身实现 |
| `IArcAgentServices` | `OpenExternalAsync` / `CopyTextAsync` / `ConfirmAsync`；缺省 `ArcAgentNullServices` |
| 命令族 | `ArcStopCommand(RunId, AfterCurrentStep)`、`ArcPauseCommand`、`ArcResumeCommand`、`ArcSteerCommand(RunId, Instruction)`、`ArcTakeoverCommand`、`ArcApprovalCommand(RunId, Decision)`、`ArcAnswerCommand(RunId, Answer)`、`ArcRetryCommand(RunId, EventId)`、`ArcOpenItemCommand(RunId, ItemId)` |

### 2.4 长连接会话流 → 多 run 映射（Lead 特别关注）

**结论：支持，而且天然支持 —— 一个长连接 + 一个 `IArcAgentEventSink` 可以喂多个 run/session。**

证据链：
1. `ArcAgentWorkspace.ApplyCore` 完全按 `@event.Stamp.RunId` 路由（`ArcAgentWorkspace.cs:208-240`）——事件自带 runId，workspace 内部用 `_sessions`/`_orphans` 字典分桶，不存在"一次 StartTurn 一个流"的结构约束。
2. `ArcRunStartedEvent` 首次到达时建 session：`if (@event is ArcRunStartedEvent started) { ... } DrainOrphans(runId)`（`ArcAgentWorkspace.cs:218/233`）。
3. 早到的其他事件按 run 缓冲：`_orphans[runId]`，上限 `MaxOrphansPerRun = 256`，超出返回 `ArcApplyResult.Orphaned` 丢弃；`RunStarted` 到达时 `DrainOrphans` 补投（`ArcAgentWorkspace.cs:89/276/293`）。
4. `ArcAgentSurface.StartTurnAsync` 只是把 `IArcAgentEventSink` 交给宿主一次（`_transport.StartTurnAsync(request, _sink)`），sink 由 Surface 持有到销毁；`SurfaceEventSink.Push` → `_coalescer.Enqueue`（任何线程安全，`Dispatcher.UIThread` `Priority.Background` 合批）。
5. 多 run 的 UI 分工已经内建：`Workspace.BackgroundRuns` → `ArcBackgroundRunDock`，`Workspace.Attention` → `ArcAgentAttentionInbox`，`RunRef.Kind == SubAgent` 用于并行子 Agent。
6. 命令路由跟随**当前激活会话**：`SendForActive` 用 `_workspace.Active?.RunRef.RunId ?? _lastRunId`（`ArcAgentSurface.cs` 尾部）→ 切 run 只需 `Workspace.Activate(runId)`。

**因此推荐的 DSH 侧映射**：
- 建一条 SSE 长连接（`GET /v1/events`），**全部事件**都走同一个 `sink.Push`；
- `StartTurnAsync` = `POST /v1/turn` 拿到 `runId`（turn 请求走命令通道，事件仍从长连接回来）；
- 每帧按 `(runId, seq)` 造 `ArcEventStamp`；run 开始时网关必须先发 `run.started`（否则前端要缓冲 ≤256 条）；
- 完成后网关发 `run.phase → Done/Failed`（对应 `ArcRunPhaseChangedEvent`）—— **不要依赖 `sink.Complete()`**。

---

## 3. 最小宿主示例（复制到 Android 模板即可）

### 3.1 引用与主题（3 行）

```powershell
dotnet add package Avalonia.Android --version 12.1.4
dotnet add package Arc.Avalonia --version 0.9.0-alpha      # 元包：主题 + 13 个组件族 + Agent
```

```xml
<!-- App.axaml：唯一一行主题接入 -->
<Application.Styles>
  <arc:ArcTheme />
</Application.Styles>
```

```csharp
// App.axaml.cs：可选，运行期选 scheme/accent（不写则用 ArcThemeManager.Options 默认）
ArcThemeManager.Apply(this, ArcColorScheme.Dark, ArcAccent.Violet);
// 其他 API：SetScheme / ToggleScheme / SetAccent(app, ArcAccent) / SetAccent(app, Color) /
//           ArcThemeOptions{ Scheme, Accent, AccentColor?, Density=ArcDensity.Comfortable, CornerRadiusScale }
//           ArcThemeManager.ReducedMotion = true（移动端建议开）
```

### 3.2 View（XAML）

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:agent="using:Arc.Avalonia.Agent"
             x:Class="DshRemote.Mobile.MainView">
  <agent:ArcAgentSurface x:Name="Surface" />
</UserControl>
```

### 3.3 宿主接线（C#，~30 行）

```csharp
public partial class MainView : UserControl
{
    private readonly ArcAgentWorkspace _workspace = new();
    private readonly DshTransport _transport;

    public MainView(DshTransport transport)
    {
        _transport = transport;
        AvaloniaXamlLoader.Load(this);
        Surface.Attach(_workspace, _transport, new DshAgentServices());  // 一次接好：Workspace + Transport + Services
        _transport.AttachWorkspace(_workspace);                          // 供自定义命令路由（可选）
    }
}

sealed class DshTransport : IArcAgentTransport
{
    private IArcAgentEventSink? _sink;
    private readonly HttpClient _http = new();   // 由宿主注入

    // ① 用户点发送时 Surface 自动调用这里；返回网关分配的 runId
    public async Task<string> StartTurnAsync(ArcAgentTurnRequest request, IArcAgentEventSink sink, CancellationToken ct = default)
    {
        _sink = sink;                                        // 长连接复用同一个 sink
        return await Gateway.PostTurnAsync(request.Text, request.Model, ct);
    }

    // ② 所有 UI 意图（stop/pause/steer/approve/answer/retry/open）都从这里出去
    public Task SendCommandAsync(ArcAgentCommand command, CancellationToken ct = default)
        => Gateway.PostCommandAsync(command, ct);

    // ③ SSE 长连接回调（后台线程；一帧一条，任意 run）
    public void OnSseFrame(DshEvent d)
    {
        var stamp = ArcEventStamp.Create(d.RunId, d.Seq, d.Id);
        ArcAgentEvent evt = d.Kind switch
        {
            "run.started"        => new ArcRunStartedEvent(stamp, d.At, new ArcAgentRunRef(d.RunId, d.SessionId)),
            "run.phase"          => new ArcRunPhaseChangedEvent(stamp, d.At, d.Phase),
            "message.started"    => new ArcMessageStartedEvent(stamp, d.At, d.MessageId, d.Role),
            "message.delta"      => new ArcMessageDeltaEvent(stamp, d.At, d.MessageId, d.Text),
            "message.ended"      => new ArcMessageEndedEvent(stamp, d.At, d.MessageId),
            "tool.started"       => new ArcToolCallStartedEvent(stamp, d.At, d.CallId, d.ToolName, d.Intent, d.Risk),
            "approval.requested" => new ArcApprovalRequestedEvent(stamp, d.At, d.ToApprovalRequest()),
            "attention.raised"   => new ArcAttentionRaisedEvent(stamp, d.At, d.ToAttentionEntry()),
            _                    => new ArcCustomEvent(stamp, d.At, d.Kind, d.RawJson)
        };
        _sink?.Push(evt);        // 线程安全；合批后由 Dispatcher 刷到 UI
    }
}
```

**顺序协议（务必按此实现网关，否则 UI 会丢事件）**
1. `run.started` **先于**该 run 的任何其他事件（否则进 orphan 缓冲，256 条上限后丢弃）；
2. 每 run 的 `Sequence` 单调递增；`EventId` 幂等（重复 → `Duplicate`，缺口 → 缓冲补投 ≤256）；
3. `approval.requested` 是**全局串行**的（同一时刻一个待决审批），`RequestId` 去重，`Blocking` 优先级最高；
4. 结束推 `run.phase = Done/Failed/Stopped`（`Complete`/`Fail` 在 Surface 是 no-op）；
5. 同一长连接可以穿插多个 run 的帧；后台 run 用 `ArcAgentRunKind.Background`、子 Agent 用 `SubAgent`。

---

## 4. Arc 对 Android 的适配现状（逐项）

| 能力 | Arc 现状 | 证据 |
|---|---|---|
| 安全区 / 刘海 / 手势条 | **无**。全仓无 `SafeArea`/`InsetsManager` 引用 | 全仓 grep 只命中文档文案与 `UsePlatformDetect()` |
| IME / 软键盘避让 | **无**。无 `InputPane`/`TopLevel.InsetsManager`/`Window.AutoAdjust` 处理；仅 `ArcAgentComposer` 用 `KeyDown` 判 `Key.Enter` 提交（软键盘 Enter 行为依赖平台） | `ArcAgentComposer.cs:42,114-116` |
| 返回键 | **无**。无 `BackRequested`/`OnBackPressed` 任何封装 | 全仓 grep |
| 长列表虚拟化 | **部分有**：Transcript 自带**有界窗口**（`ViewportRows=40` 默认，`MaxRenderedRows=2×视口=80`，超出丢头行）；10k 事件实测 0 次全量重建、rendered=80 | `ArcAgentTranscript.cs:174-175,446`、`docs/17-...:3.2` |
| 触摸适配（点击目标/hover 语义） | **无触摸模式分支**；控件按桌面 hover 设计（`IsPointerOver` 等） | grep 无 `TouchMode`/`IsMobile` |
| 窄屏布局 | **无**。`ArcAgentSurface` 内部写死 `_transcript.Height = 340`、`_attentionInbox.Height = 220`，外层 `Border+ScrollViewer`；探针真机（1272×2800，~447dp 宽）上**运行条出现重叠**：`Pause`/`Steer` 与标题互相压字（见截图） | `ArcAgentSurface.cs:41-42`、`arc-probe-device.png` |
| 系统字体/Inter | 主题包依赖 `Avalonia.Fonts.Inter`，Android 上正常加载（探针渲染无 tofu） | 探针截图 |
| 性能类特效 | `Special.Glass`（毛玻璃）/`Effects` 包用 Skia 效果，Android 上可用但 GPU 成本未知（未压测） | `src/Special/Arc.Avalonia.Special.Glass` |

---

## 5. 包体积 / 启动时间预期

| 场景 | 实测/推算 | 依据 |
|---|---|---|
| Debug，全 4 ABI，不链接不裁剪，`EmbedAssembliesIntoApk=true` | **裸 Avalonia 70.9 MB；+Arc 全家桶 75.3 MB** | 本次实测 |
| Arc 自身净增（Debug，全 ABI） | **+4.4 MB**；`arc.*` 19 个 dll 合计约 2.9 MB 未压缩（Agent 185 KB + AgentUI 100 KB + Agent 依赖族） | `D:\.nuget\packages\arc.*\lib\net10.0\*.dll` 实测 |
| Release 预期（单 ABI arm64 + trimming + AOT 可选） | 裸 Avalonia 通常 **15–25 MB**；+Arc 约 **+2–4 MB**。（**未实测**：需要 `RuntimeIdentifiers=android-arm64`、`PublishTrimmed=true`、`AndroidLinkMode=SdkOnly`；本次时间不够） | 行业经验 + 兄弟项目 Debug 78.8 MB 参照（`docs/I17-产品改进盘点.md:216`） |
| 冷启动 | **Debug 全 ABI、无 AOT：`Displayed +1s205ms`**；Release/AOT 预期 ~0.4–0.8 s（未测） | 本次 `adb logcat` 实测 |
| 首帧内存/掉帧 | 未测（需 `dumpsys gfxinfo` 多帧） | — |

> 结论：**Debug 阶段 APK 70–80 MB 属正常，不要为此改架构**；发布前用 `<RuntimeIdentifiers>android-arm64</RuntimeIdentifiers>` + Release + 裁剪压到 ~20 MB 级。

---

## 6. 直接复用 / 需自写 / 风险

### 6.1 直接复用（零改动，已验证在 Android 上可用）

| 项 | 说明 |
|---|---|
| `ArcTheme` + `ArcThemeManager` + `ArcThemeOptions` | 一行 `<arc:ArcTheme/>` 在 Android 探针上正常渲染暗色 + Violet；`Density=Comfortable`/`CornerRadiusScale` 可做移动端放大 |
| `ArcAgentSurface` 组合根 | 真机渲染成功（运行条 + Transcript + Approval Tray + Dock + Attention + Composer）；`Attach(workspace, transport, services)` 一条线接完 |
| `ArcAgentWorkspace` / `IArcAgentSession` 归约器 | 纯内存、无线程/IO 依赖，多 run/session 分桶已内建 |
| 全部 19 种 `ArcAgentEvent` + 9 种 `ArcAgentCommand` | 契约即 DSH 网关的 JSON 契约蓝本，可直接映射 |
| `ArcAgentTranscript` 有界窗口 | 长会话天然有上限（80 行），10k 事件实测可用 |
| `ArcAgentComposer` / `ArcAgentStreamingText` / `ArcAgentToolCallCard` / `ArcAgentApprovalTray` / `ArcToolApprovalSheet` / `ArcAgentAskCard` / `ArcAgentChoiceCard` / `ArcRunControlBar` / `ArcBackgroundRunDock` | 叶子控件均按"宿主推数据、控件只报意图"设计，Android 无需改 |
| `Arc.Avalonia 0.9.0-alpha` NuGet 包 | 本地缓存已有，离线可还原；兄弟项目 Android 生产验证 |

### 6.2 需自写（Arc 完全没有，必须做）

| 项 | 工作量 | 建议做法 |
|---|---|---|
| `IArcAgentTransport` 实现（HTTP + SSE 长连接、断线重连、`(runId, seq)` 去重/乱序缓冲、认证） | **大**（1–2 周） | 与 DSH 网关 JSON 契约一起定；SSE 帧 → `ArcAgentEvent`；命令走 `POST /v1/command` |
| Android 宿主工程（`AvaloniaMainActivity` + AppCompat 主题 + `Application` 私有目录 + `EmbedAssembliesIntoApk`） | 小（0.5 天） | 直接照抄 `_scratch/android-arc` 骨架 + §1.4 |
| 安全区 / 状态栏、导航条配色 | 小（0.5–1 天） | `TopLevel.InsetsManager` / Android `WindowInsets`；主题里已有 statusBar/navBar 颜色示例 |
| IME 避让（Composer 贴键盘、列表跟随滚动） | 中（2–3 天） | `TopLevel.InputPane`?（Avalonia 12 用 `TopLevel.InsetsManager.DisplayEdgeToEdge` + `Window` 软键盘回调），或宿主 Activity `adjustResize` |
| 返回键语义（弹层逐级关闭 → 退出） | 中（1–2 天） | `TopLevel.BackRequested` + 自己维护导航/弹层栈（Arc 的 Sheet/Dialog 不接这个） |
| 窄屏布局（手机断点） | 中（2–4 天） | Surface 内部高度 340/220 写死，需换 `Auto`/`*`；运行条需重排成两行或换 Icon-only（截图已暴露重叠） |
| 触摸目标尺寸 / hover 语义降级 | 中（2–3 天） | Arc 有 `Density.Comfortable` 但没有 touch 分支；可用 `ArcThemeOptions` 放大 + 局部样式覆盖 |
| 会话持久化 / 重连恢复（`ArcStateSnapshotEvent` 重建） | 中 | Arc 有 `StateSnapshot`/`StateDelta` 事件与 `Reset()`，但缓存策略要自写 |
| 安卓侧安全存储（网关 token）、后台/前台切换、通知 | 中 | Android Keystore + 前台服务（若需后台跑 run） |

### 6.3 风险清单

| 风险 | 等级 | 说明与缓解 |
|---|---|---|
| **Arc 无任何移动端适配，窄屏布局需自己重排** | **高** | 真机截图已暴露 `ArcRunControlBar` 重叠；`ArcAgentSurface` 写死高度。→ 首版就做手机断点：Surface 换成自建布局，只复用叶子控件（每个 leaf 都能独立用） |
| `net10.0-android` 依赖本机私有的 `D:\SmartBillAndroidSdk` + 显式 `-p:AndroidSdkDirectory` | 中 | 换机/CI 会立刻失败。→ 写进 README/构建脚本，或装 API 36 到标准位置 |
| AppCompat 主题缺失 = 启动即崩 | 中（已解决） | 模板必须带 `styles.xml`；已写入本次探针 |
| Arc 依赖 `Avalonia 12.1.4` 精确版本 | 低 | `Avalonia.Android 12.1.4` 匹配；兄弟项目用 12.1.5 也能跑，但**别跨大版本** |
| 两个同名 `ArcAgentEvent`/`ArcAgentEventKind`，编译期易错 | 低（易踩） | 统一用 `using UiEvent = ...` 别名；封装自己的 ViewModel 层 |
| `ArcAgentTranscript` 单独使用时 Markdown 工厂未初始化 | 低 | 要么直接引用 `Arc.Avalonia.Agent`，要么手动设 `DefaultMarkdownOptionsFactory` |
| `sink.Complete/Fail` 是空实现，容易误以为 run 会结束 | 低（契约坑） | 网关必须发 `run.phase` 事件 |
| Arc 0.9.0-alpha 版本、公开 API 基线 878 行且仍在演进 | 中 | pin 版本（本地 NuGet 已有 0.9.0-alpha）；升级前跑自己的宿主回归 |
| 10k 事件实测是 headless 桌面数据，Android 真机长会话未压测 | 中 | Transcript 有界窗口缓解；但增量 `TextBlock` 直改在低端机上需实测 |
| Debug APK 70–80 MB 被误当发布体积 | 低 | Release + 单 ABI + 裁剪；预算按 ~20 MB 规划 |

---

## 7. 证据文件索引

| 用途 | 路径 |
|---|---|
| 裸 Avalonia Android 探针 | `_scratch/android-bare/`（csproj / App.axaml / MainView.axaml / MainActivity.cs） |
| Arc + Android 探针（真机验证过） | `_scratch/android-arc/`（含 `Resources/values/styles.xml` AppCompat 主题） |
| 构建日志 | `_scratch/build-bare.log`、`_scratch/build-arc.log` |
| 真机截图（Arc Agent Surface 在 TestDevice） | `_scratch/arc-probe-device.png` |
| APK 产物 | `_scratch/android-{bare,arc}/bin/Debug/net10.0-android/*-Signed.apk` |
| Arc 源码：组合根 | `D:\Code\VibeCoding\Arc.Avalonia\src\Agent\Arc.Avalonia.Agent\Controls\ArcAgentSurface.cs` |
| Arc 源码：宿主契约 | `...\src\Agent\Arc.Avalonia.Agent\Hosting\ArcAgentHostContracts.cs` |
| Arc 源码：归约器 | `...\src\Agent\Arc.Avalonia.Agent\Workspace\ArcAgentWorkspace.cs` |
| Arc 源码：事件模型 | `...\src\Agent\Arc.Avalonia.Agent\Events\ArcAgentEvents.cs` |
| Arc 源码：Transcript | `...\src\Agent\Arc.Avalonia.AgentUI\Controls\ArcAgentTranscript.cs` |
| Arc 源码：主题 | `...\src\Theme\Arc.Avalonia.Theme\ArcTheme.axaml(.cs)`、`...\src\Foundation\Arc.Avalonia\Theme\ArcThemeManager.cs`、`ArcThemeOptions.cs` |
| 工程事实 | `Arc.Avalonia\Directory.Build.props`（TFM）、`Directory.Packages.props`（Avalonia 12.1.4 / CPM）、`NuGet.config`（离线源） |
| 设计文档（叙事依据） | `Arc.Avalonia\docs\17-m2-wave2-agent-and-theme-api.zh.md`（Agent MVP + Theme API）、`docs\13-framework-layering-and-agent-top.zh.md` |
| Android 先例（兄弟项目，只读参考） | `D:\Code\VibeCoding\我的账单APP\src\SmartBill.App.Android\`、`...\SmartBill.Mobile\`、`tools\android\build-apk.ps1` |
