# 会话内容复制：职责归属与实现

> 日期：2026-11-14
> 范围：聊天页 transcript 的文本复制（整条消息 / 选中文本）
> 结论：**由 DSHRemote 自己实现；Arc 不需要改。**

---

## 1. 问题

用户反馈：手机聊天页里文本"选中后无法复制"。拆开看是两层：

- **即使产生了选区**（桌面鼠标、硬件键盘、部分输入方式），Avalonia 的 `SelectableTextBlock` 也没有任何复制入口——没有右键/长按菜单、没有选择手柄、没有浮动工具条；
- **纯触摸场景下**，Avalonia `ScrollViewer` 的触摸滚动手势与选择手势互斥，慢速拖动通常连选区都产生不了（本次模拟器实测，见 §5/§6.3）。

两条路径的结果一致：transcript 里的文字拿不出来。这不是 DSHRemote 独有的问题，而是 **Avalonia 自绘文本 + 组件库默认策略** 的组合结果。

---

## 2. 现状链路：为什么"选中了却复制不了"

```
ChatMessageViewModel
  └─ ChatView.axaml  DataTemplate
       └─ MarkdownBodyView (UseMarkdown=true)
            └─ ArcMarkdownView  Options.EnableTextSelection = true
                 └─ PuraMarkdownView（Arc 0.2 起把渲染委托给 PuraMarkdown.UI）
                      └─ 每个顶层块一个控件
                           └─ SelectableTextBlock（段落/标题/列表正文）
```

逐层事实：

| # | 位置 | 事实 |
|---|---|---|
| 1 | `DshRemote.App/Controls/MarkdownBodyView.cs` | 创建 `ArcMarkdownView` 时设置 `Options = new ArcMarkdownOptions { EnableTextSelection = true }`，即**已经开启选择**。 |
| 2 | `Arc.Avalonia.Markdown/Rendering/ArcMarkdownPuraOptions.cs` | `EnableTextSelection` 原样映射到 `PuraMarkdownOptions.EnableTextSelection`。 |
| 3 | `PuraMarkdown.UI/Rendering/TextBlockFactory.cs` | 开启选择时，正文控件用 `SelectableTextBlock`，否则用 `TextBlock`。 |
| 4 | **Avalonia 12.1.5 `SelectableTextBlock`** | 公共能力只有 `SelectionStart/SelectionEnd/SelectedText/CanCopy`、`Copy()`、`SelectAll()`、`ClearSelection()`；**复制只接在键盘 `OnKeyDown` 的 Ctrl+C 上**。控件本身没有内建右键菜单、长按菜单、选择手柄或浮动工具条。 |
| 5 | Android 平台 | Avalonia 是自绘 UI，Android 系统不会为目标文本弹出系统复制浮层（系统只看到 Avalonia 的一个 SufaceView）。手机又没有键盘复制快捷键 → **选中状态没有任何出口**。 |
| 6 | 组件层级 | Arc/PuraMarkdown 每个**顶层块**渲染成独立控件（段落/列表/代码块各一个），选择模型是逐控件的 → **跨块选择天然做不到**。 |
| 7 | ChatView 其它文本 | 推理正文、工具参数、工具结果用的是裸 `SelectableTextBlock`（不是 Arc），同样没有复制出口。 |

Arc 其实已经提供了两层原语：

- `ArcMarkdownView.GetPlainText()` / `CopyAllAsync()`（把整篇纯文本写入剪贴板，默认 no-op 剪贴板时回退 `TopLevel.Clipboard`）；
- `ArcMarkdownCodeBlock` 的**复制代码**按钮（组件内部行为，回退 `TopLevel.Clipboard`）。

缺的不是"能不能复制"，而是**"谁来提供用户按得到的复制入口、复制哪一段"**。

---

## 3. 归属判断

### 3.1 结论表

| 能力 | 归属 | 理由 |
|---|---|---|
| 文本可选中的渲染 | **Arc / PuraMarkdown** | 已由 `EnableTextSelection` 提供，是通用 Markdown 能力。 |
| 单个 Markdown 视图的"复制全文"API | **Arc** | `GetPlainText()` / `CopyAllAsync()` 已是通用 API。 |
| 代码块复制按钮 | **Arc / PuraMarkdown 组件** | 组件内局部行为，已有实现。 |
| **整条消息复制**（用户/助手/系统/工具结果/压缩摘要） | **DSHRemote** | 只有宿主知道消息边界、行类型、`ChatMessageViewModel` 的字段和"该复制什么"。 |
| **选中文本 → 复制**的页面级交互（浮动条、提示、清除选区） | **DSHRemote** | 聊天页同时包含 Arc 渲染的 Markdown 与宿主自己的 `SelectableTextBlock`（推理/参数/结果）；页面级指针监听能一次覆盖全部文本，Arc 只覆盖其中一部分。 |
| 跨块选择 / 通用选择工具条 | **Arc / PuraMarkdown**（若要做） | 需要改渲染架构或引入跨控件的选择模型，属于组件库职责，不是宿主能补的。 |

### 3.2 为什么不是 Arc

1. **Arc 是通用组件库，不认识"消息"**。`ArcMarkdownView` 不知道一条消息从哪里开始、工具结果和正文哪个该复制、压缩摘要是什么。把这些语义塞进 Arc，会把 DSHRemote 的产品逻辑变成所有宿主都要背的公共契约。
2. **复制对象随行类型变化**：助手消息复制正文，工具行复制结果（结果未到时复制参数），压缩卡复制摘要。这是 `ChatMessageViewModel` 的投影，属于 App。
3. **聊天页的文本不全是 Arc**：推理、工具参数、工具结果都是宿主裸 `SelectableTextBlock`。在 Arc 里做选择菜单只能覆盖 Markdown 正文，用户选中工具输出仍然复制不了，体验会割裂。
4. **Arc 当前的边界是合理的**：平台剪贴板通过 `IArcMarkdownClipboard` 注入，默认 no-op、由宿主提供真实实现；Arc 不假设宿主一定有可用的 `TopLevel`。这正是"组件提供原语、宿主决定 UX"的分工。
5. **改动成本与风险**：为了 Android 复制去改 Arc/PuraMarkdown 的渲染或选择模型，会影响所有宿主与版本矩阵；而当前需求用宿主的公开 API 就能完整实现。

### 3.3 什么情况下应该由 Arc 做

- 希望**任何** Markdown 宿主都获得"选中 → 复制"工具条：应在 Arc/PuraMarkdown 提供选择事件与 copy-selection 命令（或内建浮动菜单）；
- 希望**跨段落/跨代码块**一次选择整段回答：只能由 PuraMarkdown 的选择模型/单一文本布局解决，宿主无法绕过；
- 上述两点是"通用能力"，不是本需求的必要条件。本需求用"整条消息复制"绕过了跨块限制。

---

## 4. 实现（DSHRemote）

### 4.1 视图模型：复制内容投影

`app/src/DshRemote.App/ViewModels/ChatMessageViewModel.cs` 新增：

| 成员 | 作用 |
|---|---|
| `CopyText` | 该行要写入剪贴板的文本 |
| `CopyLabel` | 按钮文案（`复制` / `复制结果` / `复制参数` / `复制摘要`） |
| `HasCopyableText` | 是否有内容可复制 |
| `ShowFooter` | 尾部行（状态/耗时/复制）是否显示 |

复制内容规则：

| 行类型 | `CopyText` |
|---|---|
| 用户 / 助手 / 系统 | `TargetText`（完整消息正文；流式时也是已收到的全文，不是逐字泵出的可见子串） |
| 工具行 | 有结果时 `ResultText`，否则 `ArgumentsJson`（`CopyLabel` 同步切换） |
| 上下文压缩卡 | `CompactionSummary` |
| 空行 | 无复制入口 |

`TargetText`/`Text`/`ResultText`/`ArgumentsJson`/压缩摘要变化时会发出 `CopyText`/`HasCopyableText`/`ShowFooter` 变更通知，保证工具结果晚到时按钮文案从「复制参数」自动变成「复制结果」。

### 4.2 每行复制入口

`Views/ChatView.axaml` 两处消息模板（普通行 + turn 的 Final 行）在尾部状态行增加一个紧凑的 Ghost 按钮，绑定 `CopyLabel` / `HasCopyableText`，点击走 `ChatView.OnCopyMessageClick`：

- 复制成功 → 底部居中 toast「已复制」；
- 剪贴板不可用 → toast「复制失败：剪贴板不可用」，并写入诊断日志。

这解决了"跨块无法选择"带来的实际缺口：用户不需要精确拖选整段回答，点一下即可。

### 4.3 选中文本 → 浮动复制条

`Views/ChatView.axaml` 在 transcript 行（`Grid.Row=2`）叠加一个浮动条：

```
已选择 N 字   [复制] [取消]
```

`Views/ChatView.axaml.cs`：

1. 在 `MessagesScroll` 上以 `handledEventsToo: true` 监听 `PointerReleased`（`SelectableTextBlock` 会处理按下事件，但释放事件仍可被页面看到）；
2. 从 `e.Source` 向上找 `SelectableTextBlock`，读取 `SelectedText`；
3. 非空 → 显示浮动条（记录文本与控件），空 → 隐藏；
4. 点「复制」→ 写入剪贴板 → `ClearSelection()` → 隐藏 → toast「已复制 N 字」；
5. 点「取消」/在其它位置重新按下 → 清除选区并隐藏；
6. 浮动条在 transcript 外层，不进入长截图内容。

这样 Arc 渲染的 Markdown、宿主渲染的推理/工具参数/工具结果，都走同一条复制路径。

### 4.4 剪贴板

统一走 `TopLevel.Clipboard.SetTextAsync`（`Avalonia.Input.Platform` 扩展方法），失败不抛给 UI：

```
copy: N chars          // 诊断日志
copy: no clipboard
copy: failed <message>
```

### 4.5 为什么复制原文而不是渲染后的纯文本

- 原文是 Agent 产出的真实内容，**代码围栏、表格、链接目标都完整保留**；粘贴到 Markdown/代码编辑器不会丢结构。
- 渲染后的纯文本是 lossy 视图（PuraMarkdown 的 `GetPlainText()` 会去掉标记），不适合作为通用复制语义。
- 选中复制路径复制的本来就是屏幕上的选中文本（纯文本），两条路径互补。

---

## 5. 已知限制

| 限制 | 说明 | 规避 |
|---|---|---|
| 不能跨块选择 | Arc/PuraMarkdown 每块独立控件、独立选择模型；跨段选择只能由上游解决 | 用每行「复制」整条复制 |
| Android 触摸拖选不产生选区 | 模拟器实测：在 transcript 文本上慢速拖动没有选区（Avalonia `ScrollViewer` 的触摸滚动手势与 `SelectableTextBlock` 的选择手势互斥），因此浮动条在纯触摸场景通常不会出现 | 手机端以每行「复制」为准；桌面/鼠标/键盘选中仍走浮动条。若要在手机上支持部分复制，需要宿主自绘长按选择（见 §7） |
| 工具结果有 6000 字符上限 | `ChatMessageViewModel.MaxToolPayloadLength` 是既有的展示/内存保护，复制跟随该上限 | 如需完整结果需另开"原始结果"通道，超出本次范围 |
| 复制的是 Markdown 原文 | 例如复制出的正文里可能带 `**`、反引号 | 选中复制可得到纯文本；后续可加「复制纯文本」 |
| 桌面仍可用 Ctrl+C | `SelectableTextBlock` 自带键盘复制，与页面浮动条不冲突 | — |

---

## 6. 验证

> 证据与复跑命令：[docs/evidence/v184-session-copy/](evidence/v184-session-copy/README.md)

### 6.1 构建与单测

```
dotnet build app/src/DshRemote.App.Desktop -c Debug -m:1   → 0 warning / 0 error
App 单测（包含新增 ChatMessageCopyTests 6 条）            → passed=101 failed=0
Core 单测（未改 Core，回归）                              → passed=187 failed=0
签名 Release APK（-NoPublish）                            → dist/apk/dshremote-1.8.4-44-arm64-release.apk
                                                            36,579,936 bytes / sha256 39dc73aa…338a37
```

### 6.2 桌面 demo（真实窗口 + UI Automation + PrintWindow）

用 `DshRemote.App.Desktop.exe --demo-chat` 打开演示会话，然后：

- 找到 4 个「复制」按钮（两轮对话各一条用户/助手行），逐个 `InvokePattern.Invoke()` 并读 Windows 剪贴板：
  - 用户行 → `好，改吧；顺便确认一下配对 URI 的格式。`
  - 助手行 → `配对 URI 形如 \`dshremote://pair?...\`…`（完整正文）
  - 第一轮用户/助手行同样命中正确文本；
- 在用户气泡文本上拖动选择 → 浮动条出现「已选择 18 字」；点「复制」→ 剪贴板得到选中的 18 字、选区清除、toast「已复制 18 字」。

证据截图：`_scratch/copy-03-selection.png`（浮动条）、`_scratch/copy-05-toast.png`（toast）。

### 6.3 Android

构建：`pwsh -File scripts/build-apk.ps1 -Configuration Debug` → `dev.dshremote.app-Signed.apk`（79.5 MB，0 warning / 0 error）。

模拟器（`emulator-5554`，API 36 x86_64）实测：

- 连接页 →「查看演示会话（假数据）」→ 打开会话；消息尾部「复制」按钮正常显示（用户/助手各一）；
- 点助手「复制」→ 底部「已复制」toast 出现；诊断日志 `12:05:01.608 copy: 328 chars`；随后聚焦输入框，系统键盘的剪贴板建议显示 `配对 URI 形如 \`…`，证明 Android 剪贴板确实拿到了消息正文；
- 触摸拖选：慢速横拖/长按后拖动都没有产生选区（见 §5 限制），因此浮动条未在纯触摸模拟器上出现；桌面（鼠标）已完整验证该路径。

真机（ColorOS/TestDevice）上建议复验两点：消息尾部「复制」→ 粘贴；以及是否有可用的选择手势（若设备/输入法支持选中，浮动条应自动出现）。

---

## 7. 上游建议（可选，不阻塞本需求）

1. `ArcMarkdownView` 暴露选择变化事件与 `GetSelectedText()`/`CopySelectionAsync()`，宿主可以少做视觉树遍历；
2. PuraMarkdown 提供跨块选择模型（或以单一 `SelectableTextBlock` 承载整段文本的可选模式）；
3. 通用 Markdown 宿主的选择浮动工具条（复制/全选）可考虑作为 Arc 的可选能力，默认关闭；
4. 宿主侧若要在纯触摸手机上支持「部分复制」，可自绘长按选择：长按计时 + `TextLayout.HitTestPoint` 换算字符索引 + `SelectableTextBlock.SelectionStart/End`，拖动期间捕获指针避免被 `ScrollViewer` 抢走。本次未实现（整条复制已覆盖"把内容拿出来"的主诉求）。

以上任何一条落地后，DSHRemote 都可以删掉页面级的选择监听，但"整条消息复制"仍应留在 App（消息语义属于宿主）。
