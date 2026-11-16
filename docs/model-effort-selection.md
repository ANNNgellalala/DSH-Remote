# 会话模型 + 思考强度选择（方案 v1，待对齐）

> 状态：**已实现（App v1.6.0，2026-11-13）**；实现与验收记录见 `README.md` 的 v1.6.0 一节，
> 证据见 `docs/evidence/v160-*.png`。本文保留为设计依据与协议备忘。
> v1.6.1 修复了真机上「级联菜单/输入框点击卡死」：选择器由 Avalonia Popup 改为页内覆盖层、IME 高度改事件驱动，
> 详见 `docs/issue-20261113-model-picker-freeze.md`。
> 目标版本：App **1.5.0**（versionCode 29）；relay 不变（0.5.2，代理透明转发新端点，无需改）。
> 关联证据：`scripts/remote-contract.json`、`scripts/_extract/sc_typert_host.js`、DSH 安装包
> `app.asar` 内 `@deepseek-ai/dsh-api-session-controller` / `dsh-agent` / `dsh-llm` 源码、
> `docs/research/dsh-internals.md`、`docs/protocol-v1.md`。

---

## 0. 一句话方案

把聊天页头部现在**只读的模型小字**升级为可点的「模型 · 思考强度」chip；点开一个
**两级选择面板（模型 → 思考强度）**，选项由官方新端点 `session/modelCatalog` 动态渲染，
选择通过 `session/selectModel` **立即写入当前会话**（下一步生效，当前进行中的步骤不变），
并用服务端 durable 事件 `model/selection` / follow 快照保持界面一致；**旧版 DSH 不支持时自动隐藏入口**，
不引入任何 relay / 网关改动。

---

## 1. 需求

| 编号 | 需求 | 说明 |
|---|---|---|
| R1 | 查看 | 在聊天页看到当前会话「模型 + 思考强度」（现在只显示模型 id，且强度完全丢失） |
| R2 | 切换模型 | 从桌面 DSH 当前**可路由**的模型目录里选（可跨 provider，如 opencode-go / deepseek-*） |
| R3 | 切换思考强度 | 仅对支持 reasoning 的模型；选项由该模型目录项给出（如 low/medium/high），可回到「默认」 |
| R4 | 生效范围 | 本会话**下一步**生效；本轮已在跑的步骤保持旧模型（官方语义，见 §3.4） |
| R5 | 一致性 | 桌面端（或另一台手机）切换模型后，本机界面能同步；断线重连后不显示过期状态 |
| R6 | 降级 | 桌面 DSH 版本没有这两个端点时，入口隐藏/置灰，**不能**出现「连接失败」之类的误报 |
| R7 | 不回归 | 发消息、审批、问答、队列、图片、长图、权限卡片等现有能力不受影响 |

**本期非目标**（可后续）：手机端管理桌面全局默认模型；新建会话时预设模型；多桌面模型目录聚合；
`/model` 之类的文本命令。

---

## 2. 现状与差距（代码事实）

| 事实 | 位置 |
|---|---|
| 会话 summary 只取 `modelSelection.lastUsed.model ?? next.model`，**把 provider / reasoningEffort 丢掉** | `app/src/DshRemote.Core/Protocol/DshParsing.cs:40-42`；`Contracts.cs:185-196`（`Model` 是裸 `string?`） |
| 聊天页头部只显示这坨裸字符串，窄屏 <460px 会被隐藏 | `Views/ChatView.axaml:79-82`；`ChatView.axaml.cs:21,165-180` |
| `SessionViewModel.ModelText` 读**不可变的** `_summary`，且从不 `Raise` → 选完模型头不会自己变 | `ViewModels/SessionViewModel.cs:121,320-322` |
| `model/selection` durable 事件现在落进 `default:` 被丢弃（现成的同步通道） | `ViewModels/SessionViewModel.cs:2222-2224` |
| 一元 RPC 复用 `CallAsync`（cookie/重试/错误码映射齐全），新增调用不需要碰连接层 | `DshRemote.Core/DshClient.cs:545-609` |
| 目录/会话模型没有被任何契约类覆盖；App 侧无端点级能力探测；`IDshClient` 不能随便加方法（`DemoDshClient`、测试假实现会连锁编译失败） | `Contracts.cs:302-342`；`Services/DemoDshClient.cs` |
| relay 已把 `capabilities/probe/dshVersion` 写进 `pairing.json`，但 App 目前不读 | `relay/src/pairing.mjs:110-119`；`DshRemote.Core/DshPairingFile.cs:91-149` |

结论：**协议端能力早就存在，缺的是 Core 契约 + App 可观察状态 + 一个选择面板**。

---

## 3. 协议依据（已核对安装包源码）

### 3.1 两个端点

| 端点 | 入参 | 返回 |
|---|---|---|
| `POST /api/session/modelCatalog` | **空 args**：`{"args":{}}`（descriptor `parameters: []`，`args` 里多一个键 → `gateway/arguments-invalid`） | `ModelCatalog` |
| `POST /api/session/selectModel` | `{"args":{"request":{"sessionId","provider","model","reasoningEffort"?}}}`（`reasoningEffort` 省略 = 由适配器默认） | `{"selected":{"provider","model","reasoningEffort"?}}` |

依据：`scripts/remote-contract.json:3217-3233,4194-4250`；strict schema `scripts/_extract/sc_typert_host.js:880-925`；
实现 `app.asar` → `@deepseek-ai/dsh-api-session-controller/lib/index.js`（`modelCatalog`/`selectModel`）。

> **args 硬规则**：payload 必须**恰好一个** plain-object `args`；`modelCatalog` 的 `args` 必须是 `{}`；
> `selectModel` 的 `args` 必须**恰好是** `{"request":{…}}`（多一个顶层键 → `gateway/arguments-invalid`）。
> 内层字段只发本文列出的字段（旧 `docs/protocol-v1.md:118` 说内层多余字段会被 strict 拒绝，
> 但本安装宿主的内层 decode 实际是 `z.object`（未知字段被 strip，`gw_index.js:1806-1817`）——两种行为都不要赌，
> **只发文档字段**；待 smoke 时顺便实测确认，再回填 `docs/protocol-v1.md`）。

### 3.2 `ModelCatalog` 形状

```jsonc
{
  "default": { "provider": "opencode-go", "model": "deepseek-v4.1-flash", "reasoningEffort": "high" },
  // ↑ 注意：这是**部署全局默认**（agentDefaultModel），不是当前会话的值；
  //   当前值必须从 projections.modelSelection.next ?? lastUsed 读，两者都为 null 时才回退 default。
  "routableProviders": ["deepseek-account", "deepseek-official", "opencode-go"],
  "groups": [{
    "id": "<provider id，就是 selectModel 的 provider>",
    "name": "<provider 显示名>",
    "models": [{
      "id": "deepseek-v4.1-flash",              // selectModel 的 model
      "name": "DeepSeek V4.1 Flash",
      "description": "…",                        // 可选
      "reasoning": {                             // 可选：没有它就表示该模型不支持思考强度
        "efforts": [{ "id": "high", "name": "高", "description": "…" }],
        "defaultEffort": "high"                  // 可选
      },
      "badge": { "text": "推荐", "tone": "info" } // 可选，纯展示；tone: neutral|info|success|warning
    }]
  }],
  "failures": [{ "id": "<provider id>", "name": "…", "message": "<加载失败原因>" }]  // 不影响其它分组
}
```

补充（来自官方 GUI 实现，客户端要照做）：
- `badge.text` 是 **union：字面量字符串 或 i18n 对象**（`{en:"…", zh:"…", …}`，`en` 必填）→ 解析时两种情况都要吃，显示优先 `zh`，否则 `en`，否则任取。
- provider 分组顺序：官方 GUI 排序为 `deepseek-account` → `deepseek-official` → 其它（`uimodel-client:1114-1116`）；
  我们可直接用服务端顺序，或复刻该排序（建议复刻，跨端一致）。
- 目录里没有 API key 的 provider 会**整组消失**（不是失败项）；`failures` 只表示该 provider 加载抛错。
- 任一 provider 抛错只进 `failures`，不拖垮整目录（`buildModelCatalog()`）。

### 3.3 校验与错误（客户端可直接展示的）

| 情况 | 服务端行为 |
|---|---|
| provider/model 不在当前可路由目录 | `session/model-unavailable`，message「Select an available model before sending a message.」+ details `{provider,model}` |
| provider 存在但 `listModels` 失败 | `session/model-unavailable`，message=底层错误 |
| 模型不支持 reasoning 却传了 effort | 同上 code，message=`provider "…" model "…" does not support reasoning effort "…"`（`dsh-llm` `UNSUPPORTED_REASONING_EFFORT` 被包成 `session/model-unavailable`） |
| effort 不在该模型 efforts 里 | 同上 |
| 省略 effort 且模型有 `defaultEffort` | **成功**，且返回的 `selected.reasoningEffort` 会被**物化成实际生效值** |
| 会话不存在 | `session/not-found` |
| 会话正被另一个 DSH 进程写 | `session/writer-held`（桌面端 GUI 把它翻译成「当前会话已被占用…请退出其他 DSH」，我们照抄） |
| 子代理会话 | 必须用 parent 地址（`session/agent-busy`），手机端本来就在列表中过滤 subagent，风险低 |
| 归档会话 | `selectModel` 本身**没有归档检查**，会成功写入事件；但 `agent/pre-step` 门禁会拒绝执行 → **选择不生效**。手机端已隐藏归档会话；若归档前已打开聊天页，切模型会"看起来成功但不生效" → 归档后关闭聊天页（现有流程已如此） |

> 其它：resume 失败的兜底是 `gateway/internal`；所有业务错误都是 HTTP 200 + `ok:false`，
> 由 `RpcWire.ParseServerResponse` → `DshRemoteException` 保留 code/message/details。

### 3.4 运行时语义（关键）

- **`selectModel` 会先解析/恢复会话**（`resolveAgent`，冷会话会被 resume，首调可能秒级），然后：
  1. `requireModel` 校验可用性；2. `llm.resolveCallConfig` 校验/物化 effort；
  3. **往会话日志追加 durable 事件 `model/selection`**（data = `{provider,model,reasoningEffort?}`）；
  4. **后台**把选择保存为桌面全局默认（`agentDefaultModel.saveSelection`，失败只 warn，不影响本次返回）。
- **运行中允许切换**：选择是「pending」状态，作用于**下一个尚未开始准备的 step**（同一 turn 内后续 step 也会生效）；
  已准备/进行中的 step（含重试）保持旧路由。若 provider/model 变化，下一个 step 还会向上下文注入一条
  `source.kind='model-selection'` 的用户可见提示（英文 `[model changed: …]`）——**本 App 已按 `source.kind!='user'` 过滤掉它（`SessionViewModel.cs:2878-2883`），不会出现奇怪气泡**。
- **`projections.values.modelSelection`**：内部状态是 `{lastUsed, pending}`，wire 视图 `next = pending ?? lastUsed`。
  `selectModel` 成功后 `next` 立刻变；只有真正发出一次模型请求（写 `request/header`）后 `lastUsed` 才更新、`pending` 清空。
  **注意**：某些请求头把 effort 标记为"适配器默认"时，`lastUsed` 会**丢掉 reasoningEffort**（显示为未指定）——
  界面显示公式应为 `intended = next ?? default`，`effort = intended.reasoningEffort ?? reasoning.defaultEffort`，
  两者都没有才显示「默认」。
  `session/follow` 快照的 `projections.values.modelSelection` 同样有 `lastUsed`/`next`（都可为 null，新会话即双 null）。
- 并发：同一 Agent 的选择按 image-admission 链**串行**，最后提交者胜；全局默认按提交顺序串行保存——所以 UI 要做
  「最新一次操作胜出」的 generation 守卫（桌面 GUI 也是这么做的）。
- **全局默认副作用**：在手机上切模型 = 桌面「新建会话的默认模型」也会被改（官方端点行为，桌面 GUI 也是同一端点）。
  这点与桌面行为一致，**不额外弹窗**，在文档/提示文案里说明即可（见 Q5）。

### 3.5 桌面 GUI 的参考交互（照抄它的语义）

`app.asar` web client（`@deepseek-ai/dsh-client-ui-model-selection`）的 composer 模型位与 `/model` 弹窗
共用一个**每会话**的 ModelDirectory，是一个**两栏菜单**：根栏两格「模型 / 推理等级」，各自下钻；
目录**每个 Host generation 只加载一份**，并在 `llm/adapters-updated`、`llm/models-updated`、
`settings/document-updated`、凭据变化与 `connection/reset` 时失效重载；
选择时有 pending 态（latest-wins generation 守卫）、失败 toast（`code: message`，`session/writer-held` 专门文案）、
effort 显示 `efforts.find(id).name ?? id`，模型有 `defaultEffort` 时切换会显式带上它，选「Default」时省略字段；
**界面只展示 `next`（即 `next ?? catalog.default`），不单独显示 lastUsed**；subagent 会话不暴露模型位。
——我们的手机端交互按这套语义做，不自己发明状态。

### 3.6 版本 / 能力探测

- 当前桌面：`pairing.json` 记录 `dshVersion=0.2.2-rc.1`，两个端点**存在**（本方案按存在实现）。
- 旧版没有端点时，网关侧典型错误是 `gateway/invocation-unavailable`（未知 endpoint），部分路径也可能直接 HTTP 404。
- 现状坑：`CallAsync` 把 404 归到 `Transport`（「无法连接中继」），会误导用户 → 本次顺手修：
  **404 / `gateway/invocation-unavailable` / `gateway/method-unavailable` / `gateway/definition-unavailable`
  → 新错误码「端点不可用」，与网络故障区分**。
- 探测策略：**不放进 relay 的 REQUIRED_CAPABILITIES**（否则旧 DSH 直接配对失败）。
  App 首次进入聊天页时后台试拉一次 `modelCatalog`：成功 → 入口可用；上述「端点不可用」类错误 → 静默隐藏入口；
  其它错误（网络/超时）→ 入口保留，面板里显示「目录加载失败 + 重试」。

---

## 4. 方案设计

### 4.1 分层与数据流

```
session/modelCatalog ──(连接代际缓存, 懒加载)──▶ MainViewModel.ModelCatalog (共享)
                                                      │ 注入
                                                      ▼
session/selectModel ◀── ApplySelection(provider,model,effort) ── SessionViewModel
        │                                                     ▲
        ├─ 返回 selected ───────────────────────────────────────┘
        └─ durable event `model/selection` ──(follow 历史/实时)──┘   ← 桌面端改模型也能同步
```

- **目录是 Host 级、与连接代际绑定**：MainViewModel 持有；重连/切换桌面时丢弃重拉。
- **会话当前选择是 per-session**：SessionViewModel 每次打开新建实例，天然随会话切换重置；
  初始值取 `projections.modelSelection.next ?? lastUsed`，两者都没有时用目录 `default`。

### 4.2 Core 改动（不动 `IDshClient`）

新增可选接口，风格对齐 `IImageClient` / `IAclGuardClient` / `ISessionFollowClient`：

```csharp
public interface IModelClient
{
    // 每个连接代际只真正请求一次；失败不抛"网络错误"给 UI，而是返回带 Support 标记的结果
    Task<DshModelCatalogResult> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task<DshModelSelection> SelectModelAsync(string sessionId, string provider, string model,
                                             string? reasoningEffort = null, CancellationToken ct = default);
}
```

新增契约（`Contracts.cs`）：
`DshModelCatalog`（Default / RoutableProviders / Groups / Failures）、`DshModelGroup`、`DshModelDescriptor`、
`DshModelReasoning`、`DshModelEffort`、`DshModelBadge`、`DshModelFailure`、`DshModelSelection`
（Provider/Model/ReasoningEffort?，`null` 表示未指定）、`DshModelCatalogResult`
（`Catalog` + `Support`：`Supported | Unsupported | Unavailable` + `Message`）。

新增 args（`Protocol/DshArgs.cs`，照现有 `EmptyArgs()` / `State()` 写法）：
`ModelCatalog()` → `{}`；`SelectModel(sessionId, provider, model, reasoningEffort?)` → `{"request":{…}}`（无 effort 时**不写**该字段）。

新增 tolerant 解析（`Protocol/DshParsing.cs`）：`ParseModelCatalog`、`ParseModelSelection`（list / follow / selectModel 共用）、
`ParseSelectModelResult`；全部按「缺字段就 null、未知字段忽略、`badge.text` 同时接受字符串与 i18n 对象」处理，
坏形状抛 `ResultInvalid`。

新增 `DshClient.Model.cs`（partial，复用私有 `CallAsync`）：目录缓存（按连接代际）、`SelectModelAsync`、
`TryReadModelSelection(JsonElement projectionsValues)` 供 follow 快照用。

顺带扩展：
- `SessionSummary` 增加 `DshModelSelection? ModelSelection`（保留现有 `Model` 字符串兼容，不破坏现有调用）。
- `SessionSnapshot` / `SessionLiteSnapshot` 增加可选 `DshModelSelection? NextModel`（来自 follow 快照 projections）。
- `DshClient.cs:598-603`：404 专用错误码（见 §3.6）。

### 4.3 App 状态层

**MainViewModel**
- 连接成功后 `if (client is IModelClient m)` 绑定，并**懒触发**目录加载（不阻塞连接/首屏）；目录变化通知。
- 重连 / 换桌面 / 断开：清空目录与选择状态（`OnClientStateChanged`、`DisconnectAsync` 现有位置）。
- 构造 `SessionViewModel` 时把目录访问器传进去（`MainViewModel.cs:1334`）。

**SessionViewModel**（新增可观察状态，替换不可变的 `ModelText` 读取）
- `SelectedProvider/SelectedModel/SelectedEffort`（可空）、`ModelDisplayName`、`EffortDisplayName`、
  `ModelChipText`（`name · effort名`，目录未知时回退原始 id）、`HasModel`。
- `IsModelPickerEnabled`：`CanEditSession && catalog 支持`；`IsModelSelecting`、`ModelSelectionError`、`IsModelPickerOpen`。
- `OpenModelPickerCommand` / `SelectModelCommand(group, model)` / `SelectEffortCommand(effort)` / `RefreshCatalogCommand`。
- `ApplySelectionAsync`：乐观更新 → `IModelClient.SelectModelAsync` → 成功用返回的 `selected`（含物化的 effort）
  覆盖；失败回滚到旧值 + 中文错误（映射 `session/model-unavailable`、`session/writer-held`、端点不可用）
  → latest-wins 守卫（旧响应不覆盖新选择）。
- `case "model/selection"`（`SessionViewModel.cs:2222` 附近）：更新选择状态 + `CountHidden`。
- 打开会话/重连后：用 follow 快照的 `next` 校准一次（不额外发 RPC）。

**DemoDshClient**：实现 `IModelClient`（假目录：2 个 provider、若干模型、一个支持 reasoning），
让桌面 `--demo` 能出证据截图；选择时更新 demo summary 的模型，验证 UI 状态机。

### 4.4 UI（聊天页）

**入口（推荐）**：头部 `ChatView.axaml:79-82` 的只读 `ModelTextBlock` 换成同位置的紧凑 chip 按钮：
`[DeepSeek V4.1 Flash · 高 ▾]`，点击开面板；目录不支持/未加载时退回纯文本（保持现状）。
选择器打开时头部不再重复显示裸 id。窄屏 <460px 的隐藏逻辑保留（可改为只显示 chip 里的模型短名）。

**面板（两级，照桌面 GUI 语义）**：优先 `ArcBottomSheet`（Arc.Avalonia 已在 Meta 引用内，无新依赖）：

```
标题：模型与思考强度
┌───────────────────────────────┐
│ 模型        DeepSeek V4.1 Flash › │  ← 下钻到模型列表
│ 思考强度    高                   › │  ← 仅当模型有 reasoning 才显示；下钻到强度列表
│ （运行中提示）下一轮生效，本轮保持原模型 │
└───────────────────────────────┘
模型列表：按 provider 分组（组头 = group.name，deepseek-account 显示「DeepSeek 账号」）
  - 行：模型 name + badge + 选中勾；description 作次要行；>4 个模型提供搜索框
  - failures 分组折叠显示「<provider> 加载失败：<message>」
强度列表：efforts 列表（当前项打勾）；模型有 defaultEffort 时不额外给「默认」行，
          没有 defaultEffort 时给一行「默认（由提供方决定）」→ 省略 reasoningEffort
状态：选择中 spinner（行内/顶部）、失败条（中文错误 + 重试）、空态「暂无可用模型」
```

**兜底方案**：若 `ArcBottomSheet`/`ArcSelect` 在 Android 上实测有问题，改用**已验证**的
`ArcDrawer`（本仓聊天页已在用）或底部 `ComboBox` 行（工作区下拉已验证）——实现时先做 0.5d 探针再定。

**全屏输入 overlay**（`ChatView.axaml:686-734`）同步放一个同样的 chip（或至少在收起时可见），避免全屏模式无法切换。

**窄屏**：不挤占 composer 的 4 列底行（附图/输入/发送/停止已满）；chip 在头部，面板全屏宽 + 内部滚动。

### 4.5 降级与错误文案

| 场景 | 表现 |
|---|---|
| 旧 DSH 无端点 | chip 隐藏，维持现状纯文本；无任何报错 |
| 目录加载失败（网络/超时） | chip 保留，面板显示错误 + 重试；不阻塞发消息 |
| 目录为空（全部 provider 失败） | 面板显示 failures 列表，「暂无可用模型」 |
| 选择失败 | 回滚 + 中文提示（模型不可用 / 会话被占用 / 端点不支持 / 网络） |
| subagent / 只读会话 | chip 置灰或隐藏（`IsModelPickerEnabled=false`） |
| 运行中 | 允许选择，面板底部提示「下一轮生效」 |
| 断线 | 面板关闭、按钮禁用；重连后自动刷新目录并校准状态 |

### 4.6 持久化

服务端已是唯一真相（会话 `next`/`lastUsed` + `model/selection` 事件）。**本期不新增本地持久化**；
不发 "上次选择" 到 settings.json（避免与服务端双写漂移）。Demo 模式除外（进程内）。

### 4.7 兼容矩阵

| 桌面 DSH | 手机 App | 行为 |
|---|---|---|
| 新版（有端点） | 1.5.0+ | 完整功能 |
| 旧版（无端点） | 1.5.0+ | 入口隐藏，其余不变 |
| 任意 | ≤1.4.3 | 无感知（不调用新端点） |

relay / VPS 网关：**不改**（HTTP/WS 反代是透明的；新端点自动透传）。

---

## 5. 任务拆分（建议 2.5–3 人日）

| 任务 | 内容 | 文件（主） | 依赖 | 估时 |
|---|---|---|---|---|
| **T1 Core** | 契约 + args + 解析 + `IModelClient` + 目录缓存 + 404 错误码 | `Contracts.cs`、`Protocol/DshArgs.cs`、`Protocol/DshParsing.cs`、`DshClient.Model.cs`(新)、`DshClient.cs` | — | 0.5d |
| **T2 Core 测试** | 12 条左右：catalog 形状/容错/坏形状、空 args、selectModel 请求体、错误码、404 映射、selection 解析 | `tests/DshRemote.Core.Tests/*` | T1 | 0.5d |
| **T3 App 状态层** | catalog 共享与代际、SessionViewModel 选择状态机、`model/selection` 事件、snapshot 校准、Demo 实现 | `MainViewModel.cs`、`SessionViewModel.cs`、`DemoDshClient.cs` | T1 | 1d |
| **T4 View + Android 探针** | 头部 chip、面板（ArcBottomSheet→必要时 ArcDrawer/ComboBox）、全屏同步、错误/空态；模拟器+真机验证 | `ChatView.axaml(.cs)`、新增样式 | T3 | 1d |
| **T5 工具/证据/发布** | Smoke `--model-catalog`/`--select-model`、e2e 追加、桌面 demo 截图、真机截图、README+协议文档更新、1.5.0 签名 APK + VPS 同步 | `tools/DshRemote.Smoke`、`scripts/e2e.ps1`、`docs/`、`dist/apk` | T1/T4 | 1d |

T1 可先合入（无 UI）；T3/T4 依赖 T1；T2 与 T3 并行；T5 收尾。

---

## 6. 验收标准

**功能**
1. 聊天页头部显示「模型 · 思考强度」，与桌面端当前会话选择一致（含 provider 变化）。
2. 面板列出真实目录（多 provider、badge、失败 provider），可选任意可路由模型 + 该模型支持的强度。
3. 选择后：`model/selection` 事件出现、`projections.modelSelection.next` 更新、**下一轮消息实际使用新模型**（用下一轮 `request/header` 的 `lastUsed` 证明）。
4. 运行中切换：本轮不受影响，下一轮生效（面板有提示）。
5. 桌面端切模型 → 手机端（跟随/重连）显示同步；手机切 → 桌面 GUI 同步（同一 durable 事件）。
6. 冷会话（未打开过）直接切模型可用（有 loading）。
7. 失败路径：不可用模型、effort 不支持、会话被占用、断网 —— 均有中文提示且状态回滚。

**非功能**
- 旧版 DSH（可临时用未含端点的方式模拟）→ 入口隐藏，连接/发消息无回归。
- 连接建立与首屏时间不因目录加载变慢（懒加载 + 缓存）。
- Android 窄屏 320/360dp 无遮挡、无溢出；全屏输入模式可用。
- 单测全绿；Android 构建 0 warning；Release APK 走 `scripts/build-release-apk.ps1`。

**证据**
`docs/evidence/` 新增：桌面 `--demo` 面板截图、模拟器截图、真机（TestDevice）截图、
Smoke 输出（catalog 摘要 + selectModel selected + 下一轮 lastUsed 变化）、`docs/evidence/README.md` 新节。

---

## 7. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| `ArcBottomSheet`/`ArcSelect` 在 Android 未验证 | UI 返工 | T4 先做 0.5d 探针；兜底 ArcDrawer（已验证）/ComboBox（已验证） |
| `modelCatalog` 构建可能慢（逐 provider `listModels` + `resolveModelInfo`，部分 provider 走网络） | 首开面板卡顿 | 懒加载 + 连接代际缓存 + 面板内 spinner + 重试；不阻塞聊天 |
| 冷会话 `selectModel` 触发 resume | 首调秒级 / `session/writer-held` | loading 态 + 专用中文文案（照桌面 GUI） |
| 运行中切换的"下一步生效"容易被误解 | 用户以为本轮已换 | 面板内固定提示 + 头部 chip 在 pending 与 lastUsed 不同时加「待生效」点（可选） |
| 切换同时改桌面全局默认 | 用户预期外 | 与桌面 GUI 行为一致；在 README/设置页文案说明（Q5） |
| 旧版 DSH 404 被误翻译成网络故障 | 误导 | §3.6 错误码区分 + 静默降级 |
| 头部空间窄 | 标题被挤 | chip 只显示模型短名 + 强度短名；<460px 用省略号/只留模型名 |

---

## 8. 待对齐问题（请确认；括号内为建议默认值）

1. **入口形态**：头部 chip + 底部面板（推荐）／直接放 composer 上方一行／设置页？
2. **术语**：UI 用「思考强度」（你的说法）还是跟随 DSH 桌面用「推理等级」（推荐统一「思考强度」，
   协议字段仍叫 `reasoningEffort`）？
3. **范围**：本期只做「当前会话切换」（推荐），还是同时做「新建会话默认模型」？
   —— 注意：`session/create` 的 strict 字段只有 `workspaceId/cwd/sessionId/agentPreset`，**塞模型字段会被静默 strip**
   （不报错、看起来生效 = 最坏的坑）；正确序列是 `create → selectModel → prompt`（顺序 await，新会话是热的、
   没有冷启动代价）。工作量 +0.3d。
4. **跨 provider**：面板是否展示目录里的**全部** provider（推荐是，桌面就支持），还是只展示当前 provider 的模型？
5. **全局默认副作用**：手机切模型会同步改桌面「新建会话默认模型」——接受（推荐，与桌面一致）／
   需要 UI 明示（如选择后 toast「已同时设为桌面默认」）／希望手机端不碰默认（需要额外恢复逻辑，不建议）。
6. **运行中切换**：允许并提示「下一轮生效」（推荐）／运行中禁用入口？
7. **思考强度默认项**：模型有 `defaultEffort` 时只列具体等级（推荐，与桌面一致）／额外提供「默认」项？
8. **最低兼容**：只要「旧版隐藏入口」（推荐）还是要读 `pairing.json` 的 `dshVersion` 做显式版本门槛？
9. **发布**：版本号 1.5.0 / versionCode 29、走 VPS 资源中心同步（推荐）？

---

## 9. 附：本方案不做什么

- 不改 `IDshClient`、不改 relay、不改 VPS 网关、不改 DSH 桌面端。
- 不做桌面全局默认模型管理页、不做多桌面目录聚合。
- 不引入本地"每会话模型"持久化（服务端已记住）。

---

## 10. 附：本次调研证据

- `_work/model-probe/REPORT.md`（517 行）：协议语义逐条结论 + file:line + 真实请求/响应 + 错误表 + 未确认项。
- `_work/model-probe/src/`（24 份，只读）：从安装包 asar 抽出的
  `dsh-api-session-controller`、`dsh-llm`、`dsh-llm-deepseek`、`dsh-agent/model-selection`、
  `dsh-agent-loop`、`dsh-agent-default-model`、`dsh-client-ui-model-selection` 等源码副本，
  报告中的 `SC/LLM/AM/AL/DEF/UI` 简称即指向它们。
- 集成点盘点（只读，未落盘）：Core 调用链、composer/VM 分层、持久化、能力探测、测试与证据惯例、
  文件级任务拆分，对应本文 §2/§4/§5/§6。
- 本机运行实例的 `projections.values.modelSelection` 真实样本（lead 通过本机只读 debug 端点取得）：
  `{"lastUsed":{"provider":"opencode-go","model":"deepseek-v4.1-flash","reasoningEffort":"high"},"next":{…同上}}`，
  与源码语义一致。
- 未能实测（明确保留）：真实 `modelCatalog` 全量响应抓包（本机 `/api` 需要 `/connect` cookie，
  未取 launchToken）；旧版 DSH 的端点缺失表现；归档/冷会话 selectModel 的确切耗时与副作用。
  这些会在 T5 的 Smoke / e2e 里补实测并回填本文与 `docs/protocol-v1.md`。

