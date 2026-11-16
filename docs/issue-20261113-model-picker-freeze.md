# 问题记录：模型级联菜单/输入框点击导致界面卡死（2026-11-13）

- 影响版本：App **1.6.0（versionCode 32）**
- 修复版本：App **1.6.1（versionCode 33）**
- 现象来源：真机（ColorOS/OPPO 系）安装 1.6.0 后反馈
- 结论：**已修复（架构层面移除高风险机制）**；模拟器无法复现 OEM 环境，需真机回归确认（文末给出回归与取证步骤）

---

## 1. 现象

1. 打开聊天页 → 点右上角模型 chip 展开级联菜单 → 点某个 Provider → 点某个模型准备展开「推理等级」时，**界面卡死**（无响应，只能杀进程）。
2. 点击聊天页底部输入框时，**输入框没有展开（键盘/全屏编辑器没有出现），界面卡死**。

两个现象都发生在 1.6.0（首次引入模型选择器与 composer/IME 改动的版本）。

## 2. 排查过程

| 步骤 | 环境 | 结果 |
|---|---|---|
| 复现级联下钻 | AOSP 模拟器（emulator x86_64 API 36），Debug APK，411dp 宽 | 3 级下钻正常，无卡死 |
| 复现输入聚焦 | 同上 | 键盘正常弹出，输入/换行正常 |
| 复现级联下钻 | 同上，**Release x64**（与发布同 LinkMode=SdkOnly） | 正常，无卡死 |
| 真实目录 vs 演示目录 | 演示目录（2 provider / 3 模型） | 正常；真实目录场景未能在真机抓取 |
| 真机 | 用户设备（ColorOS） | 1.6.0 必现；无 logcat/ANR trace 可回收 |

**结论**：AOSP 模拟器（Debug/Release、手机宽度）无法复现，问题与 **OEM 的窗口/输入法实现** 相关，而不是纯业务逻辑死循环（否则模拟器也会复现）。

代码审查定位到三个高风险机制：

### 2.1 模型选择器使用 Avalonia `Popup`（主因假设）

1.6.0 中模型选择器是 `Popup`（Android 上对应独立的 Popup 窗口），且**逐级替换内容**：Provider 列表 → 模型列表 → 推理等级列表，行数与高度每次都变化，触发 Popup 自动定位/尺寸更新。

- Popup 是独立 Window，Android 上其焦点、触摸分发与 IME 交互由 OEM WindowManager 实现；当内容在展开状态下被替换（尤其带滚动条/最大高度约束）时，部分 OEM 会把 Popup 窗口的状态弄坏。
- 一旦 Popup 窗口卡住，它会**继续拦截整页的触摸事件**（表现为整页“死机”），这同时解释了现象 2：点输入框的触摸被卡住的 Popup 吞掉，输入框既不会展开也不会响应。
- 这也解释了为什么“杀进程重开后又能在点输入框时复现”——重开后 Popup 机制仍是同一套，只要触发条件出现即可再次卡住。

### 2.2 IME 高度双重补偿 + 250ms 轮询（次要风险）

- `AndroidManifest.xml` 为 `windowSoftInputMode="adjustResize"`（窗口本身已随键盘缩放）；
- `ChatView` 同时用 `IInputPane.StateChanged` 与 **250ms 定时器轮询** `PlatformIntegration.ImeHeight`，反复设置 `ChatRoot.Margin`；
- 两条通道在键盘动画期间可能给出不同高度，导致 margin 反复变化 → measure/arrange 抖动；OEM 输入法（第三方输入法/动画实现）下可能把 UI 线程拖死。

### 2.3 首字符自动展开全屏输入（风险放大器）

- `PromptBox.TextChanged` 在文本非空时调用 `OpenComposerFullscreen()`，与 `GotFocus` 的展开路径叠加，产生额外的焦点切换与 IME 重入。

## 3. 修复（1.6.1）

1. **模型选择器改为页内覆盖层，彻底移除 `Popup`**
   - `ChatView.axaml`：在 `ChatRoot` 内增加 `Grid RowSpan=4` 覆盖层（半透明背景 + 右上卡片），`IsVisible` 绑定 `IsModelPickerOpen`；
   - 点击背景空白处关闭（`OnModelPickerBackdropPressed`）；
   - 级联下钻逻辑完全不变（Provider → 模型 → 推理等级），卡片内滚动、错误/加载/失败 provider 提示均保留；
   - 不再有独立窗口，触摸事件不会落到 UI 根之外的 Window 上。
2. **IME 高度改为事件驱动 + 合并应用**
   - 删除 250ms 轮询定时器；
   - 订阅 `IInputPane.StateChanged` 与 `PlatformServices.LayoutInsetsChanged`（Android head 的 inset 通知），统一进 `RequestImeHeight`；
   - 80ms 合并窗口内只应用最后一次高度；高度变化 <1dp 忽略；页面卸载时退订事件；
   - 避免与 `adjustResize` 的窗口缩放相互触发。
3. **去掉 `PromptBox.TextChanged` 自动展开全屏**
   - 只保留“点击输入框聚焦时按设置展开”的单一路径，减少焦点/IME 重入。

## 4. 验证（1.6.1）

模拟器（AOSP API 36，**411dp 手机宽度**，Release x64，LinkMode=SdkOnly，与发布包同配置）：

| 场景 | 结果 |
|---|---|
| 点 chip → Provider → 模型 → 推理等级 三级下钻 | 正常，无卡死（`v161-freeze-fix-overlay-*.png`） |
| 选择「中」 | chip 由「V4.1 Flash / 高」→「V4.1 Flash / 中」，面板关闭（`v161-freeze-fix-applied.png`） |
| 点击半透明背景 | 面板关闭，页面其余操作恢复 |
| 点击输入框 | 键盘立即弹出，输入框位于键盘上方（键盘避让正常），无卡死（`v161-freeze-fix-input-ime.png`） |
| 输入 `abc` 后按换行 | 输入框出现第二行，`dumpsys input_method mInputShown=true`，未发送消息 |
| 打开/关闭全屏输入（设置开启时） | 焦点与 IME 正常，无抖动 |

自动化测试：Core **173/173**、App **76/76**；Solution 构建 0 warning / 0 error。

证据：`docs/evidence/v161-freeze-fix-*.png`（5 张）。

## 5. 残余风险与真机回归

- 由于 AOSP 模拟器无法复现 OEM 问题，本次属于**移除高风险机制 + 加固**，而非在真机抓到 ANR 栈后定向修复。1.6.1 发布后请在原真机回归上述两个操作。
- 若仍复现，请提供以下任一信息，可进一步定位：
  - `adb logcat -d > dshremote-log.txt`（重点 `AndroidRuntime`、`monodroid`、`ANR in dev.dshremote.app`、`InputMethod`）；
  - `adb shell dumpsys activity lastanr`；
  - `adb shell kill -3 <pid>` 后在 logcat 中抓取 UI 线程栈（需要复现时进程仍存活）。
- 备选承载方案：若某 OEM 上页内覆盖层仍有异常，可把选择器换到本仓已验证过的 `ArcDrawer`（侧边抽屉）或 `ArcBottomSheet`，逻辑层（`SessionViewModel` 的级联状态机）无需改动。

## 6. 产物

- 修复 APK：`dist/apk/dshremote-1.6.1-33-arm64-release.apk`
  - 36,506,208 bytes = 34.82 MB，versionName **1.6.1** / versionCode **33**
  - sha256 `4de59b0200504e898802f768483ec5a55d90becfdbdfc51a26beeec47ba278a4`
  - 证书 `b5613a45…493d6727`（同签名可覆盖安装）
  - 公网下载：<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.1-33-arm64-release.apk>

---

## 7. 追加（1.6.2，2026-11-13）：运行中「插话模式」出现时卡死 → 诊断 + 固定高度加固

### 7.1 新现象

真机（1.6.1）反馈：进入会话后「插话模式」选项未出现时操作流畅；该选项出现（IsRunning 变 true）时**整个 APP 卡死随后自动关闭**。

### 7.2 处理

- **无法在模拟器复现**：AOSP 模拟器（Debug，411dp）执行 demo 发送 → running=true → 插话行出现 → 流式输出，全程正常；因此仍判断为 OEM/输入法/窗口相关的运行时崩溃，需要设备侧证据。
- **新增诊断能力（1.6.2）**，让下一次崩溃可定位：
  - 新增 `DiagnosticsLog`（`app/src/DshRemote.App/Services/DiagnosticsLog.cs`）：滚动日志 + 最近 240 条 breadcrumb；
  - 捕获 `Dispatcher.UIThread.UnhandledException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`、Android `AndroidEnvironment.UnhandledExceptionRaiser`；
  - **UI 心跳 watchdog**：UI 线程 1s 心跳，超过 12s 未跳动且应用在前台 → 写入 `UI-HANG` + breadcrumb 尾部（用于区分"卡死"与"崩溃"）；
  - 日志位于应用专属外部目录，可免 root 取回：`/sdcard/Android/data/dev.dshremote.app/files/dshremote-diagnostics.log`；
  - 设置页新增「诊断」卡片：显示日志路径、**复制诊断日志**、清空。
  - breadcrumb 覆盖：连接开始/完成、打开会话、会话加载开始/完成、`running=true/false`、发送消息、模型选择器开/关、IME 显示/隐藏、聊天页 attach/detach。
- **针对"插话行出现"的加固**：把插话条从"整行 `IsVisible=IsRunning`"改为**固定高度 30dp 的容器**，容器常驻、只切换内部内容（空闲时显示一行灰色提示，运行时显示原有复选框）；
  - 作用 1：running 切换时 composer/页面**不再重排**（避免与 IME 连接/布局重入相关的崩溃面）；
  - 作用 2：`CheckBox` 随页面创建，不再在 running 切换瞬间首次应用模板。

### 7.3 取证与回归步骤（请在真机执行）

1. 安装 `dist/apk/dshremote-1.6.2-34-arm64-release.apk`（或公网链接）。
2. 复现"插话模式出现后卡死/关闭"的场景。
3. 取日志（任选其一）：
   - 电脑：`adb pull /sdcard/Android/data/dev.dshremote.app/files/dshremote-diagnostics.log`
   - 手机：设置页 → 诊断 → **复制诊断日志**（粘贴发给开发者）
4. 把日志（以及 `/sdcard/Android/data/dev.dshremote.app/files/dshremote-diagnostics.log.1` 如存在）发给开发者；日志中 `UI-HANG` / `UI-THREAD-UNHANDLED` / `APPDOMAIN-UNHANDLED` 段落会带 breadcrumb 与托管栈。

### 7.4 产物

`dist/apk/dshremote-1.6.2-34-arm64-release.apk`（36,510,304 bytes = 34.82 MB，versionName **1.6.2** / versionCode **34**，sha256 `bea4bf20c565cfe74a36de049b95c76f9f2f9a73d476e03a5007ba6a06395a50`），已上传 VPS：
<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.2-34-arm64-release.apk>

> 说明：1.6.1 已移除 Popup 与 IME 轮询两个高风险机制；1.6.2 交给现网的是"可诊断 + 插话行固定高度"的加固版本。拿到真机日志后可在 1.6.3 做定向修复。

---

## 8. 真机定位与修复（1.6.3，2026-11-13，无线调试）

### 8.1 真机复现与证据

无线调试连接真机（TestDevice / Android 16）后，用 1.6.2 诊断构建复现并抓到关键数据：

- 打开一个**正在运行的长会话**（本项目自身这个会话）时，诊断日志记录 **UI-HANG ≈13s**：
  `02:24:47 UI-HANG: UI thread did not respond for ~13s`；
  同一时段系统记录 **ANR**（`dumpsys activity lastanr`）：
  `02:25:10 Input dispatching timed out ... not responding. Waited 5000ms`。
- 1.6.3 临时加计时后抓到第二次同类场景：
  - 打开会话后 `load done` 仅 356 ms，但紧随其后出现 **6s UI-HANG**；
  - 日志显示 `Messages` 从 117 条被**短时间内灌到 800+ 条**，每 128 条一批由 UI 线程处理：
    `perf: live batch n=128 ms=31…437`、`perf: rebuild timeline rows=4 messages=478 turns=2 ms=409`；
  - 即：打开/追平一个正在流式输出的长回合时，**每个事件都触发一次全量时间线重建**（含回合折叠内的 Process/Final 集合清空重建），
    加上聊天跑动时事件洪峰（每秒数十条）+ 长 Markdown 首次布局，UI 线程被连续占满 → 冻结 → ANR → 系统/用户关闭。

**结论：用户看到的“插话模式出现后卡死”是表象**——插话模式行出现的时刻正是 `running=true`、流式事件开始灌入的时刻；
真正的根因是长会话流式追赶时的 **UI 线程事件处理成本**，与插话模式控件本身无关（1.6.2 的固定高度加固仍然保留）。

### 8.2 修复（1.6.3）

1. **live 事件合并批处理**：`OnLiveEvent` 不再立即应用，先进队列；120ms 定时器一次最多取 128 条，
   `ApplyRecord` 期间挂起时间线重建，批次结束只 `RebuildTimeline()` 一次、`TranscriptUpdated` 一次。
2. **重连/补洞快照批处理**：`ApplyResyncSnapshot` 不再逐条 `ApplyEventCore`，改为一次性批量应用。
3. **回合折叠增量同步**：`ChatTurnViewModel.Update` 对 `Process`/`Final` 做前缀 diff（追加/截断/必要时全量），
   不再每个事件 `Clear()+逐条 Add`；避免几百张工具卡每批重建、大幅降低每批耗时。
4. **Markdown 延迟渲染队列**：`MarkdownBodyView` 先渲染纯文本，`MarkdownRenderQueue` 每 40ms 只升级一个视图为 Arc Markdown；
   打开长会话时不会一次性构建几十个长 Markdown 文档阻塞布局。
5. 保留 1.6.2 的插话条固定高度、IME 事件化合并与全部诊断能力（含 perf 计时行：`perf: live batch / resync / rebuild timeline / markdown build / add batch / reveal tick`）。

### 8.3 真机验证（1.6.3）

- 冷安装后重新加载配对，打开一个正在运行（running=true）的会话：`load done ms=1248`（首屏网络拉取），
  **无 UI-HANG、无新增 ANR**（`lastanr` 仍为修复前的 02:25:10），界面可正常操作；
- 打开本项目长会话：运行中的回合保持在折叠状态（`正在执行中 · 34m08s`，>40 条 process 自动折叠），
  插话模式行正常显示，滚动/返回/菜单均可用；
- 日志新增的批量/重建计时显示单批降至几十毫秒级，事件洪峰被摊平到 120ms 批次中。

> 说明：本项目自身的会话是极端样本（单回合 1000+ 事件、数百条工具调用）。修复后 UI 更新被批处理与增量更新约束；
> 若个别会话仍出现 >6s 卡顿，诊断日志会记录 `UI-HANG` 与最近 `perf:` 行，可据此继续优化（例如按时间预算处理批次）。

### 8.4 产物

`dist/apk/dshremote-1.6.3-35-arm64-release.apk`（36,514,400 bytes = 34.82 MB，versionName **1.6.3** / versionCode **35**，
sha256 `97f9c79947e58e55dadbd3101429a3b1f1d864718cfdda46cadcc7c46f42a106`），已上传 VPS：
<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.3-35-arm64-release.apk>

---

## 9. 追加（1.6.4）：删除空闲态「运行中可发送插话」提示条

1.6.2 为降低运行切换时的 composer 重排风险，曾把插话条改成固定 30dp 常驻容器，并在空闲时显示灰色提示「运行中可发送插话（steer，不排队）」。该提示无实际信息量，1.6.4 已移除：空闲时不占行，运行时仍显示原复选框行。1.6.3 已定位并修复真正的卡死根因（流式事件洪峰下的 UI 重建成本），此处不再需要固定高度兜底。
