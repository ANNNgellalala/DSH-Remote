# 设计：DSHRemote 支持「会话被压缩」（compaction / surface replace）

- 日期：2026-11-14
- 状态：设计定稿（可直接实施）；P0 范围已用真实日志回放验证
- 运行版本：DSH Desktop **`@deepseek-ai/dsh` 0.2.2-rc.3**（`app.asar\dsh\node_modules\@deepseek-ai\dsh`）
- 前置定位报告：[`docs/issue-20261114-timeline-order-misplacement.md`](issue-20261114-timeline-order-misplacement.md)
- 独立复核：[`docs/evidence/20261114-compaction-support/dsh-compaction-semantics.md`](evidence/20261114-compaction-support/dsh-compaction-semantics.md)（协议语义）、[`.../client-change-plan.md`](evidence/20261114-compaction-support/client-change-plan.md)（客户端改造核对）
- 证据目录：`docs/evidence/20261114-compaction-support/`
- 目标读者：DSHRemote 客户端（Core / App / UI）实现者

---

## 0. 结论（TL;DR）

1. DSH 的「压缩」在协议层不是一种新消息，而是会话日志 v4 的 **surface 替换（`surfaceOp: { op: "replace", startSeq, endSeq }`）** 加一组 log-only 的 `compaction/*` 事件。
2. **聊天转录（transcript）的正确投影是「只取 append-surface 事件」**——这是 DSH 自己的规则（`isAppendSurfaceEvent`）。替换事件（含工具结果裁剪副本、摘要检查点 user/message、系统提示替换）**永远不应该作为一条新消息渲染**；它们只用于说明“模型上下文发生了什么”。
3. 因此客户端**不需要**移植完整 surface fold（模型上下文视图）。需要做的是四件事：
   - Core 解析信封里的 `surfaceOp / sourceEventSeqs / ignorable`；
   - App 投影层在 `ApplyRecord` 最前面过滤“非 append 的 surface 事件”和 `compaction/*`（**必须在 `ResolveTurn` 之前**）；
   - 新增压缩状态机与「上下文已压缩」卡片（自动/手动/失败/摘要未加载/进行中）；
   - 修掉上一轮定位的兜底孤儿行（暂存跨页匹配 + 无锚点不投影 + 顶层行不变量）。
4. 验证：真实会话日志做「append-surface 过滤」后，用现有生产投影代码回放，压缩导致的逃逸行从 37/31/38 条降为 **0**；剩余 1 条是页边界把 call/result 裁开的独立缺陷（由 P0 的兜底守卫解决）。
5. 分阶段：**P0（正确性，~0.5–1 人日）→ P1（压缩卡与状态，~1–2 人日）→ P2（可选增强 ~1 人日）**。

---

## 1. DSH 压缩机制（权威语义）

> 依据：安装版 DSH 文档 `$env:DSH_INSTALL_DIR/docs/`（`subsystems/compaction.zh.md`、`subsystems/session.zh.md`、`persistence-catalog.zh.md`、`subsystems/session-query.zh.md`）、asar 内打包代码，以及本机真实会话日志（见 §2）。

### 1.1 会话日志与 surface 事件

- 会话日志是 **append-only** 的类型化事件流，每条携带单调 `seq`、`time`、`type`、`data`，以及条件字段（`docs/subsystems/session.zh.md:254-296`）：
  - `surfaceOp`：**每个 surface 事件必填**，`'append'` 或 `{ op:'replace', startSeq, endSeq }`；
  - `sourceEventSeqs`：可选的“被引用/被遮蔽的较早事件 seq 集合”（完整、非空、唯一、全部早于自身）；
  - `ignorable?: true`：未知类型是否可安全跳过；**缺失表示不可忽略**（读到未知且无 `ignorable` 的事件，严格读取器必须拒绝重建而不是静默丢弃）。
- surface 事件类型（只有这些会进入模型历史，`session.zh.md:302-319`）：
  `system/message`、`developer/message`、`user/message`、`assistant/message`、`tool/result`。
  `tool/call`、`turn/*`、`step/*`、`compaction/*` 等都是 log-only（log-only **禁止**携带 `surfaceOp`/`sourceEventSeqs`）。
- **replace 的精确语义**（`session.zh.md:321-342`；实现校验见 `dsh-session/src/surface.ts:290-299,403-504`）：
  - 只接受恰好 3 个键（`op/startSeq/endSeq`）；遮蔽“当前 surface 中从 `startSeq` 到 `endSeq`（含端点）的节点”，并把本事件插入到该位置；
  - 端点是 **surface 位置**而非数值区间（按 `nodes.indexOf` 比较，`startIdx<=endIdx` 校验），因此此前发生过替换后 **`startSeq` 可以大于 `endSeq`**；
  - 端点必须早于替换事件；`sourceEventSeqs` 必须包含全部被遮蔽节点；`tool/result` 的 replace 除 `message.content` 外其余 `data` 必须深比较相等；
  - 替换后 **surface 顺序与 seq 顺序分叉**：新事件 seq 最大，但在 surface 中位于被替换区间的位置。

### 1.2 三类 surface 替换的生产者

| 生产者 | 日志事件 | surface 变化 | 是否需要 UI |
| --- | --- | --- | --- |
| 系统提示更新 | `system/message` + replace | 替换旧系统提示节点（`session.zh.md:302`） | 否（本应用一直隐藏 `system/message`） |
| **工具结果裁剪**（compaction-tool-result-pruner） | `compaction/prune`（log-only，带 `shadowedSeqs`/token 计价）+ **紧跟**一条 replace `tool/result` | 把大体积旧结果换成裁剪副本 | **否**（transcript 保留原始 append 结果；DSH UI 也不为 prune 单独建节点） |
| **摘要压缩**（compaction-basic） | `compaction/start` → `compaction/summary` → **replace `user/message`（compact-checkpoint）** → `compaction/end` | 把一段连续 surface 节点换成一条摘要检查点消息 | **是**（「上下文已压缩」卡片） |

#### 1.2.1 工具结果裁剪的精确形态（真实日志）

- `compaction/prune`：`data = { shadowedRange:{start,end}, shadowedSeqs:[…], shadowedTokenCount }`；`shadowedSeqs` 通常单元素；它与替换事件**同时间戳、严格相邻**（prune 在前）。
- 替换 `tool/result`：
  - `surfaceOp={op:'replace',startSeq:<旧seq>,endSeq:<旧seq>}`、`sourceEventSeqs:[旧seq]`；
  - `data` 除 `content` 外与原事件完全一致：同 `callId`、同 `message.id`、同 `turn/step`、同 `meta`；
  - `content` 为确定性裁剪：`原文[0..4096] + "\n\n[... tool result middle pruned ...]\n\n" + 原文[末尾1024]`，长度恒 5159（真实日志 38/38 验证）；触发阈值 8192 码点（`dsh-compaction-tool-result-pruner`）。
- 该替换**不是新的对话内容**；模型上下文用它，聊天转录不该多出一张卡。

#### 1.2.2 摘要压缩的精确形态（真实日志）

```
compaction/start   { compactionId, turn, sourceCommandId? }                 // 锁开始（turn=null 表示手动空闲压缩）
compaction/summary { compactionId, summary:[ContentBlock], rawOutput?, llmStreamCall?, shadowedRange:{start,end},
                     shadowedSeqs:[…], shadowedTokenCount, provider, model, maxTokens?, usage?, sourceCommandId? }
user/message       surfaceOp:{op:'replace', startSeq, endSeq}, sourceEventSeqs:[start,summary, …shadowed],
                   data.source.kind='compact-checkpoint', data.source.compactionId, data.source.sourceCommandId?,
                   data.content = [前导文本, 摘要正文, 结尾文本]（3 个 text 块）
compaction/end     { compactionId, turn, error?, sourceCommandId? }          // 锁结束；error 表示失败尝试
```

- 检查点消息的判定（DSH UI 同款，bundle `compactSource()`）：`user/message` + replace + `data.source.kind === "compact-checkpoint"` + `source.compactionId` 为字符串。
- 自动压缩（无 `sourceCommandId`）与手动压缩（有 `sourceCommandId`）走同一替换，只差归属。
- **`compaction/*` 全部是 log-only**，不进入 surface；摘要本身由检查点 `user/message` 承载。
- 失败：`compaction/end.error` 存在、可能没有检查点；`compaction/summary-error` 是进程内 waterfall 事件，不落盘。

### 1.3 DSH 自己的投影规则（本次设计的直接依据）

在 asar 打包代码中确认（`SURFACE_EVENT_TYPES` / `isSurfaceEvent` / `isAppendSurfaceEvent` / `compactSource` / `compactSummary` / conversation 节点定义）：

```ts
const SURFACE_EVENT_TYPES = new Set(['system/message','developer/message','user/message','assistant/message','tool/result'])
function isSurfaceEvent(e)    { return SURFACE_EVENT_TYPES.has(e.type) && e.surfaceOp !== undefined }
function isAppendSurfaceEvent(e) { return isSurfaceEvent(e) && e.surfaceOp === 'append' }
function isReplacementSurfaceEvent(e) { return isSurfaceEvent(e) && e.surfaceOp !== 'append' }
```

DSH 的聊天会话视图（conversation 节点）统一遵守：

| 节点 | match 规则（摘录） |
| --- | --- |
| 输入消息 | `user/message` **且** `isAppendSurfaceEvent && !isCompactionCheckpoint` |
| 工具结果更新 | `tool/result` **且** `isAppendSurfaceEvent` |
| turn / deliverables / schedule | 只接 append 的 `tool/result` 等 |
| 未知 surface 兜底 | `isAppendSurfaceEvent`（替换事件不回退显示） |
| 压缩卡 | 按 `compactionId` 聚合 `compaction/start|summary|end` 与检查点；只有存在检查点才 `buildViewNode` |

压缩卡数据（bundle `compactSummary()`）：

```ts
{ kind:'compaction', seq: checkpoint.seq, time: checkpoint.time,
  summary,                       // compaction/summary.data.summary 中 text 块拼接
  summaryEventSeq,               // 可为 null（摘要未加载）
  shadowedItemCount,             // shadowedSeqs.length
  shadowedTokenCount }           // 可为 null
```

手动压缩在 DSH UI 中归入 command 节点（`manual-compaction`，带命令信息）；裁剪（prune）**没有**独立节点/徽标。

**官方人类转录的取法（决定性依据，`dsh-session` 源码注释 `surface.ts:84-106`）**：

> “model-visible surface deliberately shadows replaced ranges, so it is the wrong source for a human transcript —
> a landed replacement would erase conversation the user already saw. Append-origin events are that transcript's
> durable source material; replacement copies stay model-only.”

同一规则被官方导出继承：`dsh-session-log-export` 的 **Markdown 导出只输出 append 事件**
（`src/markdown.ts:121/127/147`）；**ZIP 导出保留原始日志**（含 `surfaceOp`/replace，不解析、不重排）。
本机 22 份会话日志盘点中，真实转录行的 `surfaceOp` 全部为 `append`，无一条 replace 出现在官方导出里。

### 1.4 分页语义与 replace 的关系

- `session/events` 返回的是**原始日志按 seq 切片**（不是 surface 解析结果）；`maxMessages` 只按 **append-surface 的 user/assistant 消息**计数（宿主默认 50，客户端按需传 30），页内会夹杂 replace 与 log-only 事件（`docs/protocol-v1.md §4.3`、bundle 中历史分组逻辑 `!MESSAGE_TYPES.has(type) || !isAppendSurfaceEvent(event) → continue`）。
- 页起点 = 目标消息 seq 与其 `sourceEventSeqs` 最小值的**连续切片**；因此页内完全可能出现“替换事件在页内、被替换目标在更早页”的情况——**客户端不能假设页内一定同时有目标**，也不能依赖宿主帮你解析 surface。
- **宿主没有面向外部消费者的 surface 解析 RPC**（已复核）：`ctx.sessionQuery.readSurface()` → `SessionSurfaceSnapshot` 只对进程内 Host 服务开放（`dsh-session-query` 是 Service，未出现在任何 `api-*` Remote 包与 gateway 文档中）；`session/events` 返回的 `surfaceOp` 在 wire 上是 opaque JSON。**外部消费者必须自解析**——本方案选择的“按 append 过滤 + 自行聚合压缩状态”正是官方导出/分页预算的同一规则。

---

## 2. 真实日志证据（本机，可复核）

日志来源：`%USERPROFILE%\.dsh\sessions\...\{sessionId}\session.v4.jsonl.zstd`（解压副本见 `_work/session-dump/`）。

| 会话 | 事件数 | 压缩形态 | 关键 seq |
| --- | --- | --- | --- |
| `session-127b430a-…`（超长会话渲染卡死分析） | 2775 | 仅工具结果裁剪：38 组 prune↔replace，无摘要 | prune 2269–2343，replace 2270–2344；覆盖 turn 1/5/6 |
| `session-c6a167ff-…`（PuraMarkdown 主开发会话） | 11818 | **摘要压缩 ×2** + 裁剪 ×15 + 系统提示替换 ×5 | 压缩1：start 4195 / summary 4196（span 18–2969，1107 节点，420,694 tokens）/ checkpoint 4197 / end 4198；压缩2：7989 / 7990（span 4197–6547，1161 节点，502,372 tokens）/ 7991 / 7992 |

要点：

- 检查点消息 `source.kind="compact-checkpoint"`、`source.compactionId` 存在，`data.content` 为 3 个 text 块（前导 321 + 摘要 9571/13465 + 结尾 20 字符）。
- 替换可以“替换之前的替换”：`system/message` 存在 seq 45→replace 17、79→replace 66、80→replace 45、130→replace 80、4839→replace 130 的链式记录——再次说明 `startSeq/endSeq` 是 surface 位置、不能当数值区间。
- 当前客户端对摘要检查点的实际行为：`IsRealUserMessage` 要求 `source.kind=="user"` → 检查点被**静默隐藏**，所以既不会出现巨大的“用户消息”，也**没有任何压缩提示**（用户完全感知不到上下文被压缩过）。

---

## 3. 方案核心决策

### 3.1 转录 = append-surface-only（主决策）

- 规则：`IsTranscriptRow(record) = !IsSurfaceType(type) || surfaceOp != replace`。
  - `surfaceOp == 'append'` → 正常行（现有逻辑）。
  - `surfaceOp == replace`（任意形状）→ **不是转录行**：不创建行、**也不原地改写已有行**（不把工具卡内容换成裁剪副本、不删除被摘要替换的历史消息）。
  - **缺少 `surfaceOp` 的 surface 事件**（旧版宿主/旧格式）→ **按 append 处理**（向后兼容）。
- 依据：`dsh-session` 源码注释明确“替换副本只属于模型历史；人类转录使用 append 来源事件，替换若落地会抹掉用户已看到的对话”（§1.3）；官方 Markdown 导出也只取 append。
- 收益：压缩相关的所有错位一次性消失；替换事件不再污染 `Messages`、`_currentTurn`、`tool/result` 的 callId 匹配、图片附件与耗时统计。
- 代价：聊天里看到的是**完整历史原文**（被裁剪前的工具结果、压缩前的消息），而不是模型上下文——这正是“聊天记录”应有的语义（DSH UI 同款）。
- **已评估但明确不采用的替代方案**：①“replace 命中已加载行则原地更新内容/加徽标、span replace 删除被覆盖的历史行”——会丢失用户已看到的原文，且与官方 transcript/导出规则不一致；②完整 surface fold（得到模型侧视图）——见 §3.2。若未来产品需要“模型视角”，只作为独立的只读视图（P2），不改变聊天转录。

### 3.2 不做 surface fold（明确非目标）

完整 surface fold（把 replace 应用到有序节点表、得到模型实际上下文）需要**全量日志**才能正确解析（替换目标可能在很早的页外），与分页/冷启动冲突，且对聊天 UI 没有价值。`SessionSurfaceSnapshot` 只适合作为 P2 的“模型上下文视图”开关，本方案不实现。

### 3.3 压缩状态机 + 卡片（可见性）

- 自动/手动摘要压缩 → 时间线中插入一张顶层「上下文已压缩」卡片，锚点 = 检查点事件 `seq/time`（DSH UI 同款）。
- 进行中（`compaction/start` 后、检查点前）→ 不建行，仅置 `IsCompacting` 状态（聊天页顶部/输入区显示“正在压缩上下文…”），结束后清除。
- 失败（`compaction/end.error` 且无检查点）→ 一条低干扰 system 警告行（可折叠显示错误摘要）。
- 工具结果裁剪 → 不建卡片（与 DSH UI 一致）；仅在诊断计数里记录“已处理 N 条 surface 替换”。
- 卡片内容：摘要 Markdown（可折叠，默认收起）+ 元信息（自动/手动、时间、已压缩 N 项 / 约 M tokens、摘要未加载时提示上翻补全）。

### 3.4 修复兜底孤儿行（与压缩正交，P0 必须一起做）

上一轮已确认的第二触发：页边界把 `tool/call` 裁掉而把 `tool/result` 留下时，兜底分支会造 `Turn=0` 顶层行。P0 一并修复：**匹配失败不再建行**，改为暂存 + 延迟匹配；投影端加不变量兜底（见 §5.3、§5.4）。POC 证明：append 过滤后仅剩此类 1 条，必须靠这项修复归零。

---

## 4. 数据模型与 Core 改动

### 4.1 `SessionEventRecord`（`app/src/DshRemote.Core/Contracts.cs:200`）

```csharp
public enum SurfaceOpKind { None = 0, Append = 1, Replace = 2 }

public sealed record SurfaceReplace(long StartSeq, long EndSeq);

public sealed record SessionEventRecord(long Seq, long Time, string Type, JsonElement Data, string RawJson)
{
    // 新增（init，默认值保证旧构造点与缓存兼容）
    public SurfaceOpKind SurfaceOp { get; init; }
    public SurfaceReplace? ReplaceRange { get; init; }
    public IReadOnlyList<long>? SourceEventSeqs { get; init; }
    public bool Ignorable { get; init; }

    public bool IsSurfaceEvent { get; init; }   // 由解析器按 SURFACE_EVENT_TYPES 填充
    public bool IsReplacementSurfaceEvent => IsSurfaceEvent && SurfaceOp == SurfaceOpKind.Replace;
    public bool IsTranscriptRow => !IsSurfaceEvent || SurfaceOp != SurfaceOpKind.Replace;
}
```

> `IsTranscriptRow` 的语义即 DSH 的 `isAppendSurfaceEvent` + 旧格式兼容：**只有“表层事件且是 replace”才排除**；`None`（旧宿主无字段）保留显示。

### 4.2 解析（`app/src/DshRemote.Core/Protocol/DshParsing.cs:116`）

`ParseEvent` 是历史页、follow 快照、observe 三条路径的唯一信封解析入口（`ParseHistoryPage:99`、`FollowParsing:76/107`、`TryParseObserveValue:158`），只改这里即可全覆盖。

```csharp
public static SessionEventRecord? ParseEvent(JsonElement eventElement)
{
    ...
    var type = GetString(eventElement, "type");
    var surface = SurfaceEventTypes.Contains(type);
    var (op, range) = ParseSurfaceOp(eventElement);      // 见下
    return new SessionEventRecord(...)
    {
        SurfaceOp = surface ? op : SurfaceOpKind.None,
        ReplaceRange = surface ? range : null,
        SourceEventSeqs = ParseSeqList(eventElement, "sourceEventSeqs"),
        Ignorable = GetBool(eventElement, "ignorable") == true,
        IsSurfaceEvent = surface,
    };
}
```

`ParseSurfaceOp` 规则（保守，前向兼容）：

| `surfaceOp` 原始值 | 结果 |
| --- | --- |
| 缺失 / `null` | `None`（旧格式；转录按 append 处理） |
| 字符串 `"append"` | `Append` |
| 对象 `{ op:"replace", startSeq:int, endSeq:int }` | `Replace` + range |
| 其它（未知形状） | `Replace`（保守：不进转录，避免未来新语义错位；上游 `isReplacementSurfaceEvent` 也是“非 append 即替换”） |

### 4.3 缓存兼容（`app/src/DshRemote.App/Services/SessionCacheStore.cs`）

- **无需 schema 版本升级**：`CachedEvent` 已通过 `[JsonExtensionData] Extra` 保留所有未知顶层字段（`:814-839`），`BuildRawJson()`（`:882`）会原样写回。也就是说**旧版本写下的缓存里已经带有 `surfaceOp/sourceEventSeqs`**。
- 唯一要改：`CachedEvent.ToRecord()`（`:870`）现在手工构造 `SessionEventRecord`，需要改为走统一解析：
  ```csharp
  public SessionEventRecord ToRecord() => DshParsing.ParseRecord(BuildRawJson());
  ```
  （Core 新增 `ParseRecord(string rawJson)`：`JsonDocument.Parse` → `ParseEvent(root)`。）
- 迁移策略：读旧缓存时字段自然补齐；写缓存无需变化。若将来新增持久化派生状态，再升级 `SchemaVersion`。

---

## 5. App 投影层改动（`app/src/DshRemote.App/ViewModels/SessionViewModel.cs`）

### 5.1 `ApplyRecord` 最前面加“surface 过滤闸门”（`:3201` 起）

```csharp
private void ApplyRecord(SessionEventRecord record, ...)
{
    // 必须在 ResolveTurn 之前：replace 事件携带旧 turn（本次样本是 turn 1/5/6），
    // 若先 ResolveTurn 会把 _currentTurn 拨回历史 turn（已审计为 E4 风险）。
    if (record.IsReplacementSurfaceEvent)
    {
        TrackSurfaceReplacement(record);      // 诊断计数；不建行
        CountHidden(trackRuntime);
        return;
    }

    if (record.Type.StartsWith("compaction/", StringComparison.Ordinal))
    {
        ApplyCompactionEvent(record, trackRuntime);
        return;
    }

    var turn = ResolveTurn(record);            // 以下保持现有逻辑
    switch (record.Type) { ... }
}
```

> 旧宿主无 `surfaceOp` 时 `IsReplacementSurfaceEvent == false`，行为与今天完全一致。

### 5.2 压缩状态机

```csharp
private sealed class CompactionState
{
    public required string CompactionId { get; init; }
    public bool Manual { get; init; }           // sourceCommandId 存在
    public int? Turn { get; init; }             // compaction/start|end 的 turn
    public long? SummarySeq { get; set; }
    public string? SummaryText { get; set; }
    public int ShadowedItemCount { get; set; }
    public int? ShadowedTokenCount { get; set; }
    public long? CheckpointSeq { get; set; }
    public long CheckpointTime { get; set; }
    public string? Error { get; set; }
    public ChatMessageViewModel? Row { get; set; }   // 时间线卡片（唯一）
}
private readonly Dictionary<string, CompactionState> _compactions = new(StringComparer.Ordinal);
public bool IsCompacting { get; private set; }        // UI 状态
```

事件分派（全部幂等，按 seq 顺序处理；历史页/live 共用）：

| 事件 | 处理 |
| --- | --- |
| `compaction/start` | 建状态（`compactionId`、`turn`、`sourceCommandId`），`IsCompacting = true` |
| `compaction/summary` | 存 `summary` 的 text 块拼接、`shadowedSeqs.Length`、`shadowedTokenCount`、`summarySeq`；若卡片已存在则刷新内容（覆盖“摘要晚于检查点到达”的页边界场景） |
| `compaction/end` | 存 `error`；`IsCompacting = false`；无检查点且有 error → 一条 system 警告行（“上下文压缩失败：…”，`Turn=0`） |
| replace `user/message` + `source.kind=="compact-checkpoint"` | 视为压缩检查点：`CheckpointSeq/Time`；**创建卡片行**（已有则更新），`Messages.Add`（到达顺序即 seq 顺序）；不当作普通用户消息 |
| `compaction/prune` | 仅诊断计数（`_prunedResultCount++`），不建行 |
| 其它 replace surface 事件（tool/result、system/message、非检查点 user） | 已被 §5.1 闸门拦截（计数后丢弃） |

卡片创建（检查点到达时）：

```csharp
var row = ChatMessageViewModel.CreateCompaction(
    compactionId: state.CompactionId,
    summary: state.SummaryText,
    shadowedItemCount: state.ShadowedItemCount,
    shadowedTokenCount: state.ShadowedTokenCount,
    manual: state.Manual,
    failed: state.Error is { Length: > 0 },
    time: record.Time,
    seq: record.Seq);
state.Row = row;
Messages.Add(row);        // 顶层行（Kind=Compaction, Turn=0），位置=检查点 seq
```

刷新（`compaction/summary` 晚到）：`state.Row?.ApplyCompactionState(...)`。历史路径的“摘要早于检查点”与“摘要晚于检查点”（页边界）都能收敛。

### 5.3 `tool/result` 兜底守卫（修复第二触发，P0）

```csharp
case "tool/result":
{
    var (callId, output) = ParseToolResult(record);
    var target = string.IsNullOrEmpty(callId) ? null : findToolCall(callId);
    // 跨页/跨缓存窗口补充匹配：append tool/result 的 sourceEventSeqs[0] 是 tool/call 的 seq
    target ??= record.PrimarySourceSeq is { } callSeq ? FindToolCallBySeq(callSeq) : null;
    if (target is not null)
    {
        if (target.HasResultApplied) return;          // 幂等：同 callId 多次结果只应用一次
        target.ApplyToolResult(output, record.Time, isError);
        AttachImageParts(target, record);
        return;
    }

    // 找不到 call：可能是页边界把 call 裁掉 / live 乱序 / 历史窗口外的旧结果。
    // 绝不创建 Turn=0 顶层行；暂存等 tool/call 到达时回填。
    StashPendingToolResult(callId, output, record.Time, isError);
    CountHidden(trackRuntime);
    return;
}
```

配套：

- `tool/call` 分支创建行后：`if (TryTakePendingToolResult(callId, out var pending)) ApplyToolResult(...)`。
- 暂存表 `Dictionary<string,(string Output,long Time,bool IsError)>`，上限 512 条（超限丢最旧），`ReloadCoreAsync`/会话切换时清空。
- `ChatMessageViewModel` 新增 `HasResultApplied`（不能用 `HasResult`：空输出时 `HasResult == false`，`:294`），`ApplyToolResult` 里置位。
- `PrimarySourceSeq`（Core，`SourceEventSeqs` 首个有效项）：append `tool/result` 用它指回 `tool/call`，是 callId 缺失或跨窗口时的第二匹配键；replace 事件的 `sourceEventSeqs[0]` 则是被替换节点 seq（本方案里 replace 不参与转录，故不用于这里）。

### 5.4 `RebuildTimeline` 不变量（P0，防御任何未来逃逸）

```csharp
foreach (var message in Messages)
{
    if (message.IsArtifact) continue;
    if (message.Turn <= 0 || message.IsUser || (message.IsSystem && message.IsError))
    {
        // 顶层行只允许：user、system-error、compaction 卡片
        if (message.Turn <= 0 && !message.IsUser && !(message.IsSystem && message.IsError)
            && message.Kind != ChatMessageKind.Compaction)
        {
            _suppressedProjectionRows++;   // 诊断计数（DiagnosticsLog）
            continue;                       // 永不投影
        }
        desired.Add(message);
        continue;
    }
    ... AppendFold(message.Turn);
}
```

注意：`ChatMessageKind.Compaction` 行 `Turn=0`，会自然成为顶层行；它不参与 `byTurn` 分组，不影响折叠锚点。

### 5.5 其它 App 细节

- `ApplyLiveBatch` / `OnLiveEvent` 不变：闸门在 `ApplyRecord` 内，live 与历史同一路径。
- `ReloadCoreAsync` 清空 `_compactions`、`IsCompacting=false`、暂存表。
- 诊断：新增计数 `surfaceReplacementsIgnored`、`prunedResults`、`suppressedProjectionRows`，写 `DiagnosticsLog.Trace`（不在 UI 显示，避免噪音）。
- `IsCompacting` 的 UI 绑定：聊天页 header 或 composer 区域显示 `ArcSpinner + "正在压缩上下文…"`；不改 `IsRunning`（压缩期间 turn 可能仍在运行/等待）。

---

## 6. UI：压缩卡片（P1）

### 6.1 卡片形态（遵循 Arc 设计语言）

```
┌───────────────────────────────────────────────┐
│ ▸ 上下文已压缩            自动 · 14:18         │   ← 折叠头（默认收起）
│   已压缩 1,107 项 · 约 420.7k tokens            │
├───────────────────────────────────────────────┤   ← 展开后
│ （Markdown 摘要正文，MarkdownBodyView）         │
│ 此前的历史仍保留在上方；模型侧已改用该摘要。      │
└───────────────────────────────────────────────┘
```

变体：

| 状态 | 显示 |
| --- | --- |
| 自动压缩 | `自动 · HH:mm`；摘要可展开 |
| 手动压缩 | `手动压缩 · HH:mm`（可选显示命令来源） |
| 摘要未加载（页起点晚于 summary） | 头部正常、正文位置提示“摘要未加载，向上翻页可补全”；补到旧页后自动刷新 |
| 失败（`compaction/end.error`，已有检查点） | 卡片标“失败”，提示“本次压缩未完成，历史仍可正常查看”；不额外插入行 |
| 失败（无检查点） | 不渲染任何行（与 DSH UI 一致：无 checkpoint 不产出节点），仅写诊断日志；避免窗口边界产生孤立错误行 |
| 进行中 | 不建卡片；页面显示“正在压缩上下文…”状态（结束/失败后消失） |

### 6.2 实现点

- `ChatMessageViewModel.cs`：
  - `enum ChatMessageKind { User, Assistant, System, Tool, Artifact, Compaction }`；
  - `CreateCompaction(...)` + 属性 `IsCompaction / CompactionSummary / CompactionMetaText / CompactionHintText / IsCompactionExpanded / ToggleCompactionCommand / HasCompactionSummary`；
  - `IsMarkdown` 对 `Compaction` 返回 true（摘要走 Markdown 渲染）；`HasHeader` 保持 true；`Header => "上下文压缩"`。
- `ChatView.axaml`：在顶层 `ChatMessageViewModel` 模板（`:271` 起）内新增 `IsVisible="{Binding IsCompaction}"` 的压缩卡区块（`Classes="compaction"`），并让通用气泡体在压缩行隐藏；或新增一个独立 `DataTemplate` 分支。样式新增 `compaction` / `compaction-header` / `compaction-meta` 三个 class（复用 `file-card`/`reasoning` 的 token 与圆角）。
- 聊天页顶部/输入区新增 `IsCompacting` 状态行（复用 `ArcSpinner`）。
- 可选（P2）：若压缩发生在某个 turn 内（`compaction/start.data.turn` 可定位），给该 turn 折叠头加“本轮已压缩”小徽标。

### 6.3 不做的 UI

- 不显示“已裁剪”徽标（DSH UI 亦无；prune 可能高频，会产生噪音）。
- 不提供“被裁剪内容对比”视图（P2 可做：仅诊断页）。
- 不实现「模型上下文视图」（surface fold，P2/P3）。

---

## 7. 分页、实时与缓存一致性

| 路径 | 处理 |
| --- | --- |
| 首屏（latest 页 + 180 事件裁剪） | 逐条 `ApplyRecord`：replace/compaction 走闸门；检查点建卡。页可能在压缩块中间开始 → 卡片“摘要未加载”，上翻补全。**裁剪预算只统计可见记录**（模型侧 replace/prune 不占首屏行预算），压缩块不会挤掉真实对话行 |
| 加载更早（`LoadOlderAsync` → `Messages.Insert(0)`） | 老页事件按 seq 升序处理；`compaction/summary` 若晚于检查点到达，用 `compactionId` 找到 `state.Row` 刷新；**不会新增重复卡片**（卡片只在首次见到检查点时创建） |
| 实时 follow（live batch） | start → summary → checkpoint → end 严格按 seq 到达；卡片在 checkpoint 时出现；`IsCompacting` 在 start/end 翻转；replace 事件被闸门丢弃，不触发 `ResolveTurn`/`tool/result` 兜底 |
| 断线 resync / follow snapshot | 与历史路径相同（记录按 seq 重放，幂等） |
| 缓存冷启动 | 原始事件（含 envelope）已持久化；`ToRecord()` 统一解析后重放，结果与在线一致；无 schema 升级 |
| 会话切换/重载 | `_compactions`、暂存表、`IsCompacting` 全部清空 |

并发/幂等保证：所有状态以 `compactionId` / `callId` 为键；卡片在 `Messages` 中最多一行；重复事件（重发/重连）只会刷新不会新增。

---

## 8. 兼容性与边界

| 场景 | 行为 |
| --- | --- |
| 旧版 DSH（事件无 `surfaceOp`） | `SurfaceOp=None` → 按 append 显示（与今天一致）；无 `compaction/*` 事件则无卡片 |
| 旧缓存（新客户端读取） | `CachedEvent.Extra` 已保留 envelope 字段 → 解析后行为正确；无需迁移 |
| 新缓存（旧客户端读取） | 旧客户端忽略未知字段（既有 `Extra` 机制）→ 不会崩；但旧客户端仍有错位 bug（需要升级） |
| 只发生工具结果裁剪（无摘要） | 无卡片；转录显示原始完整结果；错位消失 |
| 摘要压缩发生在窗口外 | 未加载时无卡片；翻到该页时出现 |
| 手动压缩（`sourceCommandId`） | 同一卡片标“手动压缩” |
| `compaction/end.error`（失败） | 警告行；若已有检查点则卡片标失败 |
| 未知 `surfaceOp` 形状 / 未知事件类型 | 未知形状按替换处理（不进转录）；未知事件类型无 `ignorable` → 记录诊断日志，UI 不崩（不拒绝重建） |
| 子代理会话 | 与普通会话同一套逻辑（本就只读） |

---

## 9. 测试计划

### 9.1 Core 解析（新增 `DshParsingSurfaceTests`）

1. `surfaceOp:"append"` → `Append`；`surfaceOp:{"op":"replace",startSeq,endSeq}` → `Replace` + range；
2. 缺字段 → `None`；未知形状 → `Replace`；非法数字 → 不抛、保守 `Replace`；
3. `sourceEventSeqs` 解析（含空数组、非数字混合）；`ignorable` 解析；
4. `ParseRecord(rawJson)` 与 `ParseEvent(JsonElement)` 结果一致（缓存路径回归）；
5. 旧缓存 round-trip：`CachedEvent.FromRecord → ToRecord` 保留 surface 字段。

### 9.2 投影与时间线（新增 `CompactionTimelineTests`，复用 `TranscriptTimelineTests` 的 ScriptedClient）

在 helper 中允许给事件加信封字段（`Append(type, dataJson, surfaceOp, sourceEventSeqs)`）。

1. **replace 不建行**：turn 运行中收到 `tool/result` replace（同 callId、窗口外 call）→ `Timeline` 不新增任何行、原有工具卡不变（文本/时长/图片不变）；
2. **旧宿主兼容**：同一序列但无 `surfaceOp` → 与当前行为一致（不回归）；
3. **摘要压缩卡**：start/summary/checkpoint/end → 恰好一张 `Compaction` 行，位置在 checkpoint seq、时间正确、摘要文本/项数/token 正确；检查点不出现在 user 行；
4. **手动压缩**：带 `sourceCommandId` → 卡片标手动；
5. **失败**：end.error 无检查点 → 一条 warning system 行；有检查点 → 卡片标失败；
6. **页边界**：窗口从 checkpoint 开始（summary 在更老页）→ 卡片“摘要未加载”；`LoadOlderAsync` 后摘要被补全且不重复；
7. **重复 replace / 重放**：同一 callId 两次 replace、同一 checkpooint 重复到达 → 无重复卡、无重复图片；
8. **裁剪（prune）**：prune + replace 成对到达 → 无新行，原工具结果保留完整文本；
9. **页边界孤儿**：call 被裁、result 保留（无 replace 的普通事件）→ 不出现顶层工具行；上翻加载到 call 后结果回填；
10. **不变量**（每个用例末尾）：`Timeline` 中不存在非 user/system-error/compaction 且 `Turn<=0` 的 `ChatMessageViewModel`；turn 折叠顺序单调；
11. **缓存**：把含 replace/compaction 的事件写入 `SessionCacheStore` 再打开 → 投影结果与在线一致。

### 9.3 真实日志回归（可选，脚本级）

用 `_work/repro` 回放真实日志（append-surface 过滤前后对比），断言：
- 修复前：不变量失败（37/31/38 条逃逸行）；
- 修复后：0 条逃逸行、压缩卡片数量 = 日志中唯一检查点数。
（不把真实日志放进单测仓库；用脚本 + `docs/evidence` 留证。）

---

## 10. 分阶段实施

### P0 —— 正确性与防错位（必须，~0.5–1 人日）

改动：

| 文件 | 改动 |
| --- | --- |
| `app/src/DshRemote.Core/Contracts.cs` | `SurfaceOpKind` / `SurfaceReplace` / `SessionEventRecord` 新字段与 `IsTranscriptRow` |
| `app/src/DshRemote.Core/Protocol/DshParsing.cs` | `surfaceOp/sourceEventSeqs/ignorable` 解析 + `ParseRecord(raw)` |
| `app/src/DshRemote.App/ViewModels/SessionViewModel.cs` | §5.1 闸门、§5.3 兜底暂存守卫、§5.4 不变量、诊断计数、重载清理 |
| `app/src/DshRemote.App/ViewModels/ChatMessageViewModel.cs` | `HasResultApplied`（及 `/result` 幂等） |
| `app/src/DshRemote.App/Services/SessionCacheStore.cs` | `ToRecord()` 走统一解析 |
| `app/tests/DshRemote.App.Tests/` | 9.1 + 9.2 的 1/2/9/10/11 用例 |
| `docs/protocol-v1.md` | §4.2 补充 surface 语义（本方案附带） |

验收：真实日志回放 0 逃逸行；现有测试全绿；旧宿主合成事件行为不变。

### P1 —— 压缩可见性（~1–2 人日）

| 文件 | 改动 |
| --- | --- |
| `SessionViewModel.cs` | §5.2 压缩状态机（start/summary/checkpoint/end、手动/失败/部分、`IsCompacting`） |
| `ChatMessageViewModel.cs` | `ChatMessageKind.Compaction`、`CreateCompaction`、属性与刷新方法 |
| `ChatView.axaml` + Theme | 压缩卡片模板与 `compaction*` 样式、进行中状态行、可选 turn 徽标 |
| 测试 | 9.2 的 3/4/5/6/7/8 |
| 文档 | 本设计 + `protocol-v1.md` 的压缩事件小节 |

验收：模拟器/真机上长会话压缩时可见卡片与状态；无错位、无重复；`TranscriptTimelineTests` 全绿。

### P2 —— 可选增强（~1 人日，按需）

- 被裁剪工具卡的“模型视角已裁剪”提示（低频、可关闭）；
- “模型上下文视图”（surface fold；需要全量日志或宿主 RPC，建议仅桌面端）；
- 未知/不可忽略事件的诊断页与导出；
- 大历史下压缩状态机的性能优化（当前 O(1)/事件）。

---

## 11. 风险与开放问题

| 风险 | 缓解 |
| --- | --- |
| 本机 `dotnet build`（App/测试工程，含 Arc.Avalonia 项目引用）在沙箱内失败（MSB4276/SDK workload 嵌套解析） | P0 实现需在可构建环境验证；也可先用 `_work/repro`（预编译 DLL）做行为回放验证 |
| 旧宿主不发送 `surfaceOp`，无法区分“replace” | 按 append 处理（保持现状）；只有新版 DSH 才产生压缩替换 |
| 页起点晚于 summary 导致卡片信息不全 | 卡片支持“摘要未加载”，上翻补全；计数缺省显示为 undefined |
| 卡片位置在 turn 折叠块之后（压缩发生在 turn 中时） | 与 DSH 聊天节点顺序一致；如觉突兀，P2 加 turn 折叠头徽标 |
| `assistant/live-chunk` 缺 turn 的潜在 Turn=0 行（审计 E2） | 本次顺带在 `EnsureLiveAssistant`/`FinishLiveAssistant` 回填 Turn（小修） |
| 未知事件语义（`ignorable` 缺失） | 不拒绝 UI 重建，但写诊断日志；后续可加“会话格式不兼容”提示 |

---

## 12. 实施状态（2026-11-14）

**已完成（代码修改，待构建验证）**：

| 范围 | 文件 | 内容 |
| --- | --- | --- |
| Core | `Contracts.cs` | `SurfaceOpKind` / `SurfaceReplaceSpan`；`SessionEventRecord` 新增 `SurfaceOp/ReplaceSpan/SourceEventSeqs/Ignorable/IsSurfaceEvent` 与 `IsReplacementSurfaceEvent/IsTranscriptEvent/PrimarySourceSeq`、`FromRawJson`（缓存复用同一解析） |
| Core | `Protocol/DshParsing.cs` | `ParseEvent` 解析 `surfaceOp`（append/replace/缺失/未知形状保守按 replace）、`sourceEventSeqs`、`ignorable`；新增 `ParseRecord(rawJson)` |
| App | `ViewModels/SessionViewModel.cs` | `ApplyRecord` 最前面闸门（`compaction/*` → 状态机；replace → 模型侧忽略，均在 `ResolveTurn` 前）；压缩状态机（start/summary/checkpoint/end、手动/失败/摘要晚到、`IsCompacting`）；`tool/result` 不再造 Turn=0 行（按 callId + `sourceEventSeqs[0]` 跨页匹配 → 暂存等 call 回填、`HasResultApplied` 幂等）；`RebuildTimeline` 顶层白名单（仅 user / system-error / compaction 卡）；`Ensure/FinishLiveAssistant` 回填 Turn/Step；首屏裁剪只统计可见记录（压缩块不挤占行预算）；重载清理 |
| App | `ViewModels/ChatMessageViewModel.cs` | `ChatMessageKind.Compaction`、`CreateCompaction`/`ApplyCompactionState`（摘要、项数、token、自动/手动/失败/未加载）、`HasResultApplied`、`ShowHeaderRow`、`ToggleCompactionCommand` |
| App | `Services/SessionCacheStore.cs`、`Services/DemoDshClient.cs` | 记录重建统一走 `SessionEventRecord.FromRawJson`（缓存/Demo 保留 surface 字段） |
| UI | `Views/ChatView.axaml`、`Theme/AppStyles.axaml` | 「上下文压缩」卡片模板（折叠头 + Markdown 摘要 + 提示）、`IsCompacting` 状态行、`compaction*` Arc 样式 |
| 协议文档 | `docs/protocol-v1.md` | §4.2 补充 surfaceOp/replace 与 compaction 事件语义 |
| 测试（未运行） | `app/tests/DshRemote.Core.Tests/SurfaceParsingTests.cs`、`app/tests/DshRemote.App.Tests/TranscriptTimelineTests.cs`、`SessionCacheStoreTests.cs` | 新增 10 个解析用例 + 9 个时间线用例（replace 不建行、prune 块无行、摘要卡与锚点 seq、摘要晚到、手动/失败卡、孤儿 result 回填、旧宿主兼容、顶层不变量）+ 1 个缓存 surface 字段 round-trip 用例；测试假宿主分页按 append-surface 计数 |

**代码审查与收尾（2026-11-14，未构建）**：两位独立审查（`review-code.md` / `review-semantics.md`）已归档到证据目录。已按审查结论修复：

- `compaction/prune` 分支可达性（真实载荷无 `compactionId`，改为先于 id 判断处理）；
- 跨页/基线路径的 `tool/result` 匹配增加已加载窗口回退（避免 follow baseline 把结果永久滞留）；
- live assistant 的 Turn/Step 以结算事件为准纠正（stale/缺失 turn 不再被顶层白名单丢弃）、`turn/end` 结算前按当前轮回填；
- 无检查点的失败压缩不渲染孤立 system 行（与 DSH UI 一致，仅诊断日志）；
- 摘要 text 块按官方 `join('')` 拼接；
- 首屏裁剪预算只统计可见记录（模型侧 replace/prune 不挤占真实对话行）；
- 压缩卡外层改用 `compaction-holder` 独立样式类，消除双层外壳；
- 测试假宿主分页按 append-surface 计数（与真实宿主一致）；新增 prune 块回归、手动/失败卡、card.Seq、缓存 surface round-trip 用例。

**构建与测试验证（2026-11-14，VS 2026 环境，已通过）**：Core / App / Desktop / Android Debug APK 全部 **0 warning / 0 error**；单测 **Core 182 passed / App 86 passed，0 failed**（含本设计新增用例）。`dotnet test` 的 VSTest testhost 在本机受限父进程环境无法启动（Win32Exception 5），已由 `scripts/run-tests.ps1` + `scripts/test-fallback` 进程内 runner 绕过并接入 `e2e.ps1`；详见 [`docs/build-verification-vs2026.md`](build-verification-vs2026.md)。

**模拟器验证（Android 16 / emulator-5556，2026-11-14，已通过）**：`scripts/e2e.ps1 -Serial emulator-5556` **8/8 PASS**。压缩功能端到端：

- 宿主侧 `commands/execute` 触发真实 `/compact`，App 实时跟随 → 生成单张压缩卡（折叠：「手动压缩 · 已压缩 4 项 · 约 2.2k tokens」；展开：摘要 Markdown），trace `surfaceReplaced=1 suppressed=0 :: … | Compaction#23`；
- 原 14:18 错位回归（`session-127b430a` 加载更早历史读入 38 组 prune/replace）：trace `surfaceReplaced=38 pruned=38 suppressed=0`，时间线仅 turn 折叠 + 用户提示，最终答案/文件卡之后无裸工具卡。

证据：[`docs/evidence/20261114-compaction-support/emulator/`](evidence/20261114-compaction-support/emulator/README.md)（含截图与 trace）。

## 13. 参考与证据

### DSH 文档（安装版 DSH 0.2.2-rc.3，`$env:DSH_INSTALL_DIR/docs/`）

- `subsystems/compaction.zh.md`：压缩 seam、`compaction/*` 事件、工具结果裁剪产出（`PrunedEntry`）、`shadowedRange` 的 surface 位置语义。
- `subsystems/session.zh.md:254-364`：`SessionEvent` 信封、`ignorable`、surface 类型、`SurfaceOp` 语义、`SurfaceIntent`。
- `persistence-catalog.zh.md`：事件目录与 digest。
- `subsystems/session-query.zh.md`：`SessionSurfaceSnapshot` / `readSurface`（**进程内 Host 服务，不在 remote 契约**）。
- 打包源码：`dsh-session/src/surface.ts`（`isAppendSurfaceEvent` 定义与因由注释 `:84-106`、replace 校验 `:290-504`、`foldSurface` `:507-615`）、
  `dsh-session-log-export/src/markdown.ts:121/127/147`（导出只取 append）、
  `dsh-client-ui-chat/src/client/conversation-nodes/compaction.ts` 与 `register-node-renderers.ts`（压缩卡节点）、
  `session/events` 历史分组（append-surface 计数 + `sourceEventSeqs` 扩展 groupStart）。
- 复核报告：[`docs/evidence/20261114-compaction-support/dsh-compaction-semantics.md`](evidence/20261114-compaction-support/dsh-compaction-semantics.md)。

### 本仓文档

- `docs/issue-20261114-timeline-order-misplacement.md`：上一轮定位（根因/复现/证据）。
- `docs/protocol-v1.md §4.2–4.3`：SessionEvent 信封与 `session/events` 分页语义（本方案会补充 surface 小节）。

### 证据（目录 `docs/evidence/20261114-compaction-support/`）

- `dsh-compaction-semantics.md`：协议语义独立复核（DSH 0.2.2-rc.3，含源码/文档路径与行号：surface.ts、compaction.zh.md、session-query、dsh-session-log-export、ui-chat 节点）。
- `client-change-plan.md`：客户端逐文件改动核对（Core/App/Cache/UI/测试，含 file:line 与 P0/P1/P2）。
- `poc-appendonly-*.txt`：真实日志 append-surface 过滤后用**生产投影代码**回放的输出（37→0 等）。
- `real-log-compaction-inventory.txt`：本机日志压缩事件盘点（会话/seq/形态/计数）。
- `filter-append-surface.mjs`：POC 过滤脚本；`inventory-compaction.mjs`：盘点脚本。
- `repro-*.txt`（引用上一轮）：未过滤回放的错位证据。
