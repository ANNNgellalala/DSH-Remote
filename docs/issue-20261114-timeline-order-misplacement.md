# 问题定位：最终答案与「本轮文件」之后仍排布折叠过程内容（工具结果卡片）

- 日期：2026-11-14
- 状态：已完成定位（根因确认；含真实会话日志 + 生产代码回放复现）
- 触发会话：`session-127b430a-1286-4301-ae18-68b8a91ad54d`（标题「超长会话渲染卡死分析」，cwd `D:\Code\VibeCoding\PuraMarkdown`）
- 涉及代码：`app/src/DshRemote.Core/Protocol/DshParsing.cs`、`Contracts.cs`；`app/src/DshRemote.App/ViewModels/SessionViewModel.cs`、`ChatMessageViewModel.cs`、`ChatTurnViewModel.cs`；`app/src/DshRemote.App/Views/ChatView.axaml`
- 独立复核：`_work/analysis/event-trace.md`（log-analyst）、`_work/analysis/client-audit.md`（client-auditor）

## 1. 结论

问题由**四层原因叠加**造成，缺一不可：

1. **事件语义层（真正起点）**：DSH 会话日志 v4 是 append-only 的“surface 日志”。上下文压缩时，宿主对旧的大体积 `tool/result` 做**替换（replace）**：先追加 `compaction/prune`（声明 `shadowedSeqs`），再追加一条携带
   `"sourceEventSeqs":[旧seq]` + `"surfaceOp":{"op":"replace","startSeq":旧seq,"endSeq":旧seq}`
   的 `tool/result`（同一 `callId`、同一 `message.id`、同一 `turn/step`，正文被确定性裁剪为「前 4096 字符 + `[... tool result middle pruned ...]` + 后 1024 字符」= 5159 字符）。
   **客户端没有建模这两个语义字段**：`DshParsing.ParseEvent`（`DshParsing.cs:116-136`）只取 `type/seq/time/data`，`SessionEventRecord`（`Contracts.cs:200`）只有 5 个字段，`app/src` 与 `relay/src` 中 `surfaceOp|sourceEventSeqs|compaction` 引用数为 **0**。于是“替换”被当成“新增一条结果”。
2. **匹配层**：替换事件带**新 seq**，任何按 seq 的去重/排序都拦不住（`compact 重放` 是“合法的新事件”）；而它对应的原始 `tool/call` 在手机端当前历史窗口之外（14:18 时刻 38 条替换中仅 2 条的 call 在窗口内），`FindToolCallMessage`（只扫当前 `Messages`，`SessionViewModel.cs:3127-3139`）按 callId 找不到 call。
3. **兜底分支缺陷（直接 bug）**：`tool/result` 匹配失败时创建的占位工具行
   `ChatMessageViewModel.CreateTool("结果", null, null, …)`（`SessionViewModel.cs:3327-3334`）
   **没有设置 `Turn` / `Step`**——而紧邻的 `tool/call` 分支是显式设置的（`SessionViewModel.cs:3305-3313`）。`Turn` 默认 0（`ChatMessageViewModel.cs:83`），而 0 在时间线投影里表示“折叠外”。
4. **投影层**：`RebuildTimeline` 把 `Turn <= 0` 的非 user/system 行当作顶层转录行直接插入 `Timeline`（`SessionViewModel.cs:3523`、`3596-3600`）。这些行在 `Messages` 里的位次晚于当前 turn 的折叠锚点，于是稳定渲染在**当前 turn 折叠块之后**；而折叠块模板的内部顺序是「折叠进程块 → 最终答案 → 本轮文件卡」（`ChatView.axaml:386-502`）。

最终可见症状即用户所述：**最终答案气泡与「本轮文件」卡之后，又出现「工具 · 结果」卡片**（时间显示为替换事件的 14:18），日志中的思考/工具过程也一并脱离折叠块排到末尾。

## 2. 截图与事件的逐项对应（唯一匹配，非推测）

截图底部两张卡片与日志事件一一对应（38 条替换中只有这两条的表头是目录列表）：

| 截图 | 日志事件 | turn/step | 时间 | callId | 输出首行 |
| --- | --- | --- | --- | --- | --- |
| 「工具 · 结果 14:18」`FullName / Length / LastWriteTime` + `D:\Code\VibeCoding\PuraMarkdown\docs\…` | `tool/result` **seq 2270**（替换 seq 38） | turn 1 / step 2 | 2026-11-13 **14:18:15** | `call_1f9cda5216044d4285191540` | `FullName … Length LastWriteTime` |
| 「工具 · 结果 14:18」`Mode / Length / LastWriteTime / Name` + `_scratch 2026/11/12 21:38:32` | `tool/result` **seq 2272**（替换 seq 49） | turn 1 / step 3 | 2026-11-13 **14:18:15** | `call_a93fd75ad77e4b37b7b8b2d5` | `Mode Length LastWriteTime Name` |

- 这两条正是 38 条替换事件中**按 seq 顺序的前两条**，也是 14:18 场景下 36 条未匹配替换卡片中的**前两条**（其后还有 34 条同类卡片，截图只截到视口开头）。
- 「本轮文件」三项（`ArcMarkdownPipeline.cs +60 −31`、`ArcMarkdownView.cs +743`、`ArcMarkdownStreamingSession.cs +258`）与本会话 **turn 6** 的 3 个文件写调用完全一致（seq 2196–2216 `edit`×8、2247 `write`、2252 `write`）。
- 截图中的最终答案（Arc 迁移总结）是 turn 6 的 final（seq 2650，14:44:19），turn 7 在 16:14:57 才开始 → 截图取证在 **14:44:19–16:14:57** 之间，14:18 的卡片是当时实时插入后一直留在页面上的。

## 3. 证据链

### 3.1 DSH 侧：14:18:15 的压缩块

`2026-11-13 14:18:15` 一秒内共 **81 条**记录，结构如下（`event-trace.md` §1.1）：

```
#2268 step/end  turn=6 step=42                 ← 压缩前最后一条普通事件
#2269 compaction/prune  shadowedSeqs:[38]      ┐
#2270 tool/result       seq=38 的 replace 副本 ┘ 第 1 组（同 time）
#2271 compaction/prune  shadowedSeqs:[49]      ┐
#2272 tool/result       seq=49 的 replace 副本 ┘ 第 2 组
…
#2343 compaction/prune  shadowedSeqs:[2139]    ┐
#2344 tool/result       seq=2139 的 replace 副本┘ 第 38 组
#2345 step/start turn=6 step=43
#2346 request/header reason="series"           ← 压缩后重建上下文序列的旁证
```

- 38 组 `prune ↔ replace` 严格交替相邻；`shadowedSeqs` 全部指向旧 `tool/result`（38/38），合计 143,561 token。
- 替换副本覆盖 **turn 1（22 条）+ turn 5（9 条）+ turn 6（7 条）**；原始 result seq 38…2139、call seq 35…2136。
- 副本除正文裁剪外与旧事件一致：`callId`、`message.id`、`turn`、`step`、`meta` 全同；正文长度统一 **5159**（4096 + 38 + 1024）。
- 日志 seq 无缺号/重号、time 严格不减 → **日志本身没有乱序**，错位发生在客户端投影。

原始 JSON 尾部（seq 2270，注意 `data` 之后的 envelope 字段）：

```json
{"type":"tool/result","seq":2270,"time":1794550695865,
 "data":{"turn":1,"step":2,"message":{"role":"tool","source":{"kind":"tool","callId":"call_1f9cda…"},
   "content":[{"type":"text","text":"…[… tool result middle pruned …]…"}]}},
 "sourceEventSeqs":[38],
 "surfaceOp":{"op":"replace","startSeq":38,"endSeq":38}}
```

### 3.2 客户端丢失了 replace 语义

- `SessionEventRecord(Seq, Time, Type, Data, RawJson)`：只有 5 个字段（`Contracts.cs:200-240`）。
- `DshParsing.ParseEvent`（`DshParsing.cs:116-136`）：只读 `type/seq/time/data` 组装记录；`RawJson` 虽然保留了整行文本，但没有任何代码解析 `sourceEventSeqs` / `surfaceOp`。
- 全仓检索：`app/src`、`relay/src` 中 `surfaceOp`、`sourceEventSeqs`、`compaction`、`shadowed` 的引用数均为 **0**；`ApplyRecord` 对 `compaction/prune` 走 `default: CountHidden`（`SessionViewModel.cs:3460-3463`）。
- 后果：替换事件 = “全新的一条 tool/result”，同一 callId 出现第二条结果（全日志 38 个 callId 各有 2 条结果，且重复集合与替换集合完全相等）。

### 3.3 为什么按 callId 匹配不到 tool/call

- 客户端首屏：`GetLatestHistoryAsync(maxMessages=30)`（消息预算）→ `NormalizeHistory` → `TrimNewestPage(…, FirstPageEventRows=90)` 再裁成最后 **180 条事件**（`SessionViewModel.cs:1817-1846`、`1969`、`3171-3182`）。
- 以 14:18:15 为“当前时刻”：30 条消息预算的页起点 = **seq 2064**；页内 205 条事件；裁剪后窗口 = **seq 2089…2268**。
- 38 条替换的原始 `tool/call`（seq 35…2136）中**只有 2 个在窗口内**（#2107、#2136）→ 这 2 条能按 callId 命中并原地更新；
  **其余 36 个在窗口外** → `findToolCall` 返回 null → 走兜底。
- （若会话在压缩之后才冷开，30 条消息页起点已到 seq 2458/2576，整个替换块不在窗口内 → 不出现该现象；这与「只有部分情况」一致。）

### 3.4 兜底行为什么会“逃出折叠块”

```csharp
// SessionViewModel.cs:3316-3337
case "tool/result":
    var target = string.IsNullOrEmpty(callId) ? null : findToolCall(callId);
    if (target is not null) { target.ApplyToolResult(...); AttachImageParts(...); }
    else
    {
        var tool = ChatMessageViewModel.CreateTool("结果", null, null, record.Time, record.Seq);
        tool.MarkStarted(record.Time);
        tool.ApplyToolResult(output, record.Time, isError);
        AttachImageParts(tool, record);
        add(tool);          // ← 没有 tool.Turn = turn; 也没有 tool.Step
    }
```

`ChatMessageViewModel.Turn` 默认 0（`ChatMessageViewModel.cs:83`，注释明确 0 = 折叠外）。投影端：

```csharp
// SessionViewModel.cs:3596-3599
if (message.Turn <= 0 || message.IsUser || (message.IsSystem && message.IsError))
{
    desired.Add(message);   // 顶层转录行，位置 = Messages 位次
    continue;
}
```

而 turn 折叠块（`AppendFold`，`SessionViewModel.cs:3541-3586`）在遇到该 turn **第一条消息**时就已整体插入；折叠块内部顺序为「折叠进程块 → Final → 文件卡」。因此替换事件（追加在 `Messages` 末尾、且 `Turn=0`）必然排在折叠块之后——即最终答案与文件卡之后。

### 3.5 生产代码回放复现（决定性证据）

回放脚本：`_work/repro/Program.cs`（读取真实日志 → 伪 `IDshClient`/`ISessionFollowClient`/`IHistoryPagingClient` → 生产 `SessionViewModel.LoadAsync()` → 反射驱动 `OnLiveEvent`/`FlushLiveEvents`，与真实 follow 循环同一条代码路径）。输出：

| 场景 | 打开/回放方式 | 逃逸顶层工具行 | 说明 |
| --- | --- | --- | --- |
| **A. 14:18 实时跟随**（日志截止 2268 后按实时应用 2269–2774） | `repro-1418-live.txt` | **37 条** | 36 条来自 38 条替换中未匹配的（14:18:15，前两条 = 截图两张），**+1 条来自页边界拆对**（seq 2089，14:07:59，见 §3.6）；TURN#6 折叠块的 Final 与 3 个文件与截图一致 |
| B. 14:09 实时跟随（更早打开，窗口不同） | `repro-live.txt` | 31 条 | 同一机制，窗口边界不同 → 数量不同 |
| C. 压缩后重开（窗口跨过替换块） | `repro-reopen-2350.txt` | 38 条 | 纯历史加载路径同样复现（含 TURN#6 文件卡） |
| D. 加载到会话末尾（窗口起点在替换块之后） | `repro-reopen-final.txt` | **0 条** | 对照组：解释“部分情况下” |

场景 A 输出节选：

```
[1] TURN#6 process=142 final=1 files=3
      FINAL: 已完成 Arc.Avalonia 的 Markdown 迁移，并在 Arc 仓验证全绿。…
      FILE: ArcMarkdownPipeline.cs +60 -31
      FILE: ArcMarkdownView.cs +743 -0
      FILE: ArcMarkdownStreamingSession.cs +258 -0
[2] Tool turn=0 tool=结果 seq=2270 time=14:18:15 preview=FullName …
[3] Tool turn=0 tool=结果 seq=2272 time=14:18:15 preview=Mode  Length LastWriteTime       Name
…（共 36 条 14:18:15 的替换卡片）
```

### 3.6 第二触发路径（与压缩无关）：页边界拆对（同一回放中实测）

场景 A 除了 36 条替换卡片外，还有 **1 条**独立逃逸行：

```
[0] Tool turn=0 tool=结果 seq=2089 time=14:07:59 preview==== Pura RenderContext ===
```

窗口被 `TrimNewestPage` 裁到 seq 2089 起，恰好落在某对 `tool/call` 与 `tool/result` 之间：call 被裁掉、result 留下 → 同样走兜底、`Turn=0`。它出现在 `Timeline[0]`（时间线最顶端），是同一缺陷的另一独立触发。相关代码：`TrimNewestPage`（`SessionViewModel.cs:1844, 3171-3182`）、`LoadOlderAsync` 的截断（`:2954-2958`）、`BuildMessages` 只在“本次构建的页内”匹配（`:3141-3164`）、旧页 `Messages.Insert(0, …)`（`:2966-2972`）。
`SessionCacheStore` 只持久化原始事件（无 Turn），缓存恢复会重新投影 → 冷启动可离线复现（放大器，不是根因）。

## 4. 代码定位索引

| # | 位置 | 问题 |
| --- | --- | --- |
| 1 | `DshParsing.cs:116-136`、`Contracts.cs:200-240` | 事件 envelope 的 `surfaceOp`/`sourceEventSeqs`（replace 语义）未被建模；替换被当作新增。 |
| 2 | `SessionViewModel.cs:3460-3463` | `compaction/prune` 等压缩事件走 `default: CountHidden`，未消费 shadow/replace 语义。 |
| 3 | `SessionViewModel.cs:3316-3337`（兜底 3327-3334） | 兜底工具行未设置 `Turn`/`Step`（对比 `tool/call` 的 3305-3313）。 |
| 4 | `SessionViewModel.cs:3523, 3596-3600` | `Turn <= 0` 的行被当作顶层转录行插入 `Timeline`。 |
| 5 | `SessionViewModel.cs:3541-3586` + `ChatView.axaml:386-502` | 折叠锚点 = 该 turn 首行在 `Messages` 的位次；折叠块内部顺序 = 进程块 → Final → 文件卡。 |
| 6 | `SessionViewModel.cs:3127-3139` | `FindToolCallMessage` 只扫当前窗口，窗口外 callId 必 miss。 |
| 7 | `SessionViewModel.cs:1844, 1969, 3171-3182, 2954-2958, 3141-3164` | 首屏 180 条/旧页 320 条裁剪 + 页内匹配 → 页边界拆对产生孤儿 result（第二触发）。 |
| 8 | `SessionCacheStore.cs:786-800, 804-919`；`SessionViewModel.cs:1743-1808` | 缓存恢复重放原始事件 → 冷启动复现/固化。 |
| 9 | `relay/src/proxy.mjs:716-834`、`gateway.mjs:557-618` | 中继是透明字节/帧转发，不去重、不排序、不解析事件 → **中继无责**（替换是新 seq，任何 seq 去重都拦不住）。 |

## 5. 为什么是“部分情况下”

出现条件（三者同时满足）：

1. 会话发生过上下文压缩，且压缩替换的旧结果的原始 `tool/call` **不在当前已加载窗口**；
2. 替换事件确实到达了客户端（实时 follow 在线期间；或重开/上翻历史时窗口跨越替换块）；
3. 该结果进入 `Messages` 时晚于当前 turn 折叠块的锚点（实时追加 → 必然成立）。

因此：
- 短会话 / 窗口覆盖原始 call（callId 能命中）→ 只在原卡片上原地更新，不产生裸卡（本会话 38 条中 2 条即属此类）；
- 压缩后很久才冷开、或窗口起点在替换块之后 → 不出现（场景 D）；
- 页边界裁剪拆对 call/result（与压缩无关）→ 也会出现（§3.6）。

## 6. 修复建议

**P0（消除主症状，最小改动）**

1. **兜底行必须归属 turn**：`tool/result` 兜底创建行后补 `tool.Turn = turn; tool.Step = ReadInt(record.Data, "step") ?? 0;`。
2. **兜底行不得成为顶层行 / 不得制造无锚点折叠**（只补 Turn 不够，见下）：
   - 匹配失败时先跨已加载窗口再查一次（`FindToolCallMessage`）；
   - 仍失败且该 `turn` 在窗口内没有任何锚点行（无 turn/start、无该 turn 的 assistant/tool-call 行）→ **不投影该行**（`CountHidden` 或暂存），避免“末尾多出一个只有 Process 的折叠块 / 裸卡”。
   - 若该 turn 有锚点，行进入对应折叠块内部（位置正确）。
3. **消费 replace 语义（根治）**：
   - 在 `SessionEventRecord` 增加 `SourceEventSeqs`/`SurfaceOp`（从 envelope 解析；`RawJson` 中已有），或直接解析 `compaction/prune.shadowedSeqs`；
   - 对 `surfaceOp.op == "replace"` 的 `tool/result`：**不视为新事件**；按 `callId` 找到已加载行则幂等更新，找不到则忽略（它是窗口外历史的替换副本，用户可通过分页查看原始上下文）；
   - `compaction/prune` 命中的行同步隐藏/替换，保持“有效转录”与宿主语义一致。
4. **幂等/去重守卫**：`ApplyToolResult` 增加“已有结果”标记，重复 result 不重复应用；`AttachImageParts` 按 attachmentId 去重（否则同 callId 多次替换会让图片翻倍）。

**P1（结构）**

1. `RebuildTimeline` 锚点改为按 turn 顺序 + 用户提示位置排序，而不是“该 turn 在 `Messages` 中首次出现的位置”；并增加不变量：**顶层行只允许 user 或 system-error**。
2. 把“事件顺序 ≠ 对话顺序”的语义（surface/replace/prune）做成 Core 层的一步“effective transcript”投影，UI 层只消费投影结果。
3. 为“跨页迟到”的 `tool/result` 增加暂存表（callId → result），`tool/call` 到达时回填；这与页边界拆对（§3.6）一并解决。

**回归测试（`app/tests/DshRemote.App.Tests/TranscriptTimelineTests.cs`）**

1. 构造“turn=2 运行中窗口 + 窗口外旧 turn 的替换 tool/result（surfaceOp=replace, sourceEventSeqs 指向窗口外 seq）”→ 断言 Timeline 无 Turn<=0 顶层行、无多余折叠块；
2. `tool/call` 与 `tool/result` 被页边界裁开（call 裁掉/result 保留）→ 断言不产生顶层卡，上翻加载到 call 后能回填；
3. 同一 callId 的 result 重复（替换语义）→ 只应用一次、图片不翻倍；
4. 通用不变量：`Assert.DoesNotContain(Timeline.OfType<ChatMessageViewModel>(), m => !m.IsUser && !m.IsSystem && m.Turn <= 0)`。

## 7. 明确排除项（避免过度归因）

- **中继层无责**：`relay/src/proxy.mjs` 对 HTTP/WS 是字节级 `pipe` 转发（`:716-834`），`gateway.mjs` 只做帧的 base64 包装/还原（`:557-618`），不解析/不去重/不排序/不合并快照；`compaction/prune` 与 replace 事件原样下发。
- **不是日志乱序**：seq 连续、time 单调不减；替换事件的时间就是真实追加时间 14:18:15。
- **不是无 turn 事件造成**：`agent/inbox/spliced`、`job/*`、`team/*`、`request/header` 等都走 `default: CountHidden`，不产生行；目标日志全部 `tool/result`/`tool/call`/`assistant/message` 都带 turn。
- **`assistant/live-chunk` 路径本次不可达**：目标日志 0 条；follow 只处理 `event` 帧（`DshClient.Follow.cs`），`assistant-stream` 帧被忽略。代码级存在“live 行可能 Turn=0”的隐患，但不是本次原因。
- **不是渲染/样式问题**：卡片内容、时间、标题（`工具 · 结果` 来自兜底行 ToolName="结果"，`ChatMessageViewModel.cs:118-125`）均与事件精确对应。

## 8. 复现方式

```powershell
# 日志：从 DSH 会话目录解压（zstd）
#   %USERPROFILE%\.dsh\sessions\--D-Code-VibeCoding-PuraMarkdown--\session-127b430a-…\session.v4.jsonl.zstd
#   → _work/session-dump/session-127b430a-1286-4301-ae18-68b8a91ad54d.jsonl

# 生产代码回放（_work/repro；无需仓库内测试工程，避开本机 SDK 嵌套构建问题）
dotnet run --project _work/repro -- <log.jsonl> 2268 live   # 场景 A：14:18 实时（37 条逃逸行，前两条=截图）
dotnet run --project _work/repro -- <log.jsonl> 2102 live   # 场景 B：14:09 实时（31 条）
dotnet run --project _work/repro -- <log.jsonl> 2350 load   # 场景 C：压缩后重开（38 条）
dotnet run --project _work/repro -- <log.jsonl> 3000 load   # 场景 D：末尾加载（0 条，对照）
```

## 9. 证据文件清单

（以下已归档到 `docs/evidence/20261114-timeline-misplacement/`；`_work/` 下为同一份工作副本/生成脚本。）

| 文件 | 内容 |
| --- | --- |
| `docs/evidence/20261114-timeline-misplacement/README.md` | 证据索引与复跑命令 |
| `docs/evidence/20261114-timeline-misplacement/event-trace.md` | 事件链独立复核（38 组 prune↔replace、surfaceOp、窗口起点、截图对应） |
| `docs/evidence/20261114-timeline-misplacement/client-audit.md` | 客户端/中继代码审计（逃逸路径清单、页边界拆对、修复候选、测试缺口） |
| `docs/evidence/20261114-timeline-misplacement/repro-1418-live.txt` | 场景 A：37 条逃逸行（36 条 14:18:15 + 1 条页边界孤儿），前两条与截图一致 |
| `docs/evidence/20261114-timeline-misplacement/repro-live.txt` | 场景 B（31 条） |
| `docs/evidence/20261114-timeline-misplacement/repro-reopen-2350.txt` | 场景 C（38 条） |
| `docs/evidence/20261114-timeline-misplacement/repro-reopen-final.txt` | 场景 D（0 条，对照） |
| `docs/evidence/20261114-timeline-misplacement/replay-probe.cs.txt` | 回放脚本源码（真实日志 → 生产 `SessionViewModel` 投影） |
| `_work/session-dump/session-127b430a-1286-4301-ae18-68b8a91ad54d.jsonl` | 目标会话事件日志（2775 条有效事件；由 DSH 会话目录 zstd 解压） |
| `_work/repro/Program.cs` | 回放脚本工作副本（可直接 `dotnet run`） |
