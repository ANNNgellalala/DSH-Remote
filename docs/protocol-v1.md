# DSH Remote 协议 v1（手机客户端子集）

本文只覆盖手机端 `DshRemote.Core` 实际使用的官方 Remote wire 子集：`/connect` 认证、一元 RPC、WebSocket 流 mux、以及
`session/list`、`session/events`、`session/state`、`session/prompt`、`session/cancel`、`session/observeEvent`、
`session/follow`、`session/updateQueue`、`$events`/`$events/result` 端点。

**依据**（全部只读核对）：

- `docs/research/dsh-internals.md` §3.2–§3.5、§4.1、§4.4；
- 桌面安装包内源码（`scripts/_extract/`）：`client_connection_host.js`（`authorizeConnect`/`authorizeApi`/`rpc-host`/
  `endpointFromPath`）、`client_node.js`（`createWebConnectionRpc` 信封）、`gateway_index.js`（`remoteRequest`/
  `assertExactArguments`/`endpointOf`）、`gw_stream_protocol.js`（mux 帧）、`sc_typert_host.js`（端点 strict schema 与
  `SessionSummary`/`SessionEventFrame` 声明）；
- 生成契约：`scripts/remote-contract.json`（`@deepseek-ai/dsh-api-session-controller`）；
- 真实运行时：relay 配对（`https://192.0.2.10:8443`，SPKI pin）与隔离 dev 实例（`http://127.0.0.1:18090`），
  2026-11-09 用 `app/tools/DshRemote.Smoke` 实跑验证。

---

## 0. 传输与配对

桌面进程内是 `dsh-host-webserver` 的 loopback HTTP 服务（端口随机）。**手机不直连 loopback**，而是经桌面内
`dsh-remote-relay` 插件：TLS 终止 + 反代 HTTP/WS，并把配对信息写到
`%USERPROFILE%\.dsh\remote\pairing.json`。

配对文件（schemaVersion 1，relay 原子写；热重载时有 1–3 s 文件缺失窗口，读取要可重试）：

```json
{
  "schemaVersion": 1,
  "active": true,
  "host": "192.0.2.10",
  "port": 8443,
  "token": "<launchToken>",
  "fp": "<证书 SPKI SHA-256 小写 hex，64 字符>",
  "pairingUri": "dshremote://pair?v=1&host=192.0.2.10&port=8443&token=…&fp=…&name=…",
  "name": "DESKTOP-EXAMPLE",
  "updatedAt": "2026-11-09T00:35:12.345Z",
  "protocol": "https",
  "loopbackPort": 8443,
  "upstreamPort": 54306
}
```

- 手机优先用 `pairingUri`（`DshPairingInfo.TryParse`），缺字段时回退 `host`/`port`/`token`/`fp`/`name`。
- **TLS pinning**：只接受证书 `SubjectPublicKeyInfo` DER 的 SHA-256 == `fp`。计算方式是
  `cert.PublicKey.ExportSubjectPublicKeyInfo()` → SHA-256 → 小写 hex；**不是**整证书哈希。
  `SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback` 与
  `ClientWebSocketOptions.RemoteCertificateValidationCallback` 都用同一回调；自签证书因此可被接受，链错误被忽略。
- `fp` 为空是 **dev-only 回退**（M0 本机联调）：loopback 主机走 `http` + 系统信任，非 loopback 仍走 `https` +
  系统信任并打印警告。生产配对必须带 `fp`。
- relay 反代时把上游 `Host`/`Origin` 改写成 `127.0.0.1:<desktopPort>`（桌面 `isTrustedApiRequest` 的信任栅栏），
  客户端无需也不应手工伪造 `Host` 头。

---

## 1. `GET /connect`：换取 API cookie（owner Peer）

```http
GET /connect?token=<launchToken>&interactions=answer HTTP/1.1
Host: <relay host:port>
```

- `interactions` 必须出现且只能出现一次，取值 `answer | none`（缺失按 `none`；重复或非法 → `400`）。
  **M2 起手机端固定用 `answer`**：M1 的 follow/observe 不需要它，但 M2 的 `$events` waterfall（审批/问答）只有
  `interactions:'answer'` 的 Peer 才会收到，`$events/result` 也要求 answer；重连后的重新 `/connect` 同样是 `answer`。
  M0 骨架曾用 `none`（只读），已废弃。
- 成功：`302`，`location: ./api/remote.mux`，并下发 API cookie：

```http
HTTP/1.1 302 Found
cache-control: no-store
location: ./api/remote.mux
referrer-policy: no-referrer
set-cookie: dsh-auth-<base64url(sha256(authority))>=v2.<payload>.<sig>; Max-Age=2592000; Path=/; HttpOnly; SameSite=Strict
```

- 失败：`401`（token 无效/过期）、`403`（Host/Origin 不在信任列表）、`400`（interactions 非法）。
- **不要自动跟随 302**（`AllowAutoRedirect=false`）；从响应头取 `name=value` 作为后续 Cookie。cookie 负载
  `{version:2, authority, id, issuedAt, expiresAt}` 由服务端 HMAC 签名，`authority` 绑定 host:port。
- **Peer 生命周期**：`/connect` 成功时同步创建并发布一个 owner API Peer（记录 `ownsSocket=false`）。
  `/api/remote.mux` 的 WebSocket upgrade 通过 `admitConnection` 认领该 Peer（`ownsSocket=true`）：
  - 同一 cookie 再次 upgrade → `409`；
  - **socket 关闭即销毁 Peer**（`release()` 从 peer 表删除并 dispose），此后带旧 cookie 的 HTTP 一律 `401`；
  - 没有 logout 接口：重连就是重新 `/connect` 换新 cookie + 新 Peer。

客户端重连语义（`DshClient` 状态机）：

```
Disconnected --ConnectAsync--> Connecting --首次成功--> Connected
Connected --WS 断开/HTTP 401--> Reconnecting --退避重试(重新 /connect + 新 WS)--> Connected
首次连接失败 / token 被 401/403 / 超过最大重试 --> Failed
用户 Disconnect/Dispose --> Disconnected
```

退避：`500ms × 2^(n-1)`，上限 `10s`，±20% jitter（`ReconnectPolicy`）；成功连接后计数归零。
RPC 在重连窗口内等待新一代连接；旧 cookie 收到 `401` 时主动断开 mux 并**重试一次**新代际请求。

---

## 2. 一元 RPC

### 2.1 路径与信封

```
POST /api/<namespace>/<method>
content-type: application/json

{"type":"client-request","rpcId":"<uuid>","method":"<namespace>/<method>","payload":{"args":{...}}}
```

- 路径 = `/api/` + endpoint。endpoint 由 `endpointOf(namespace, method)` 生成，恰为两段：`session/list` →
  `POST /api/session/list`（**不是** `/api/session/session/list`）。
- `payload` 必须**恰好一个** plain-object `args` 字段（`gateway_index.js` `remoteRequest()` 强制：
  `Remote payload must contain exactly one plain-object args field`）。
- `args` 的键是 Typert descriptor 的**参数名**：`session/list` 是 `_request`，其余端点是 `request`。
  缺少/多余都会在业务调用前被 `assertExactArguments()` 拒绝（`gateway/arguments-invalid`）。
- 内层参数对象走 strict codec（`additionalProperties:false`），多发字段会被拒（`gateway/input-invalid`）。

成功响应：

```json
{"type":"server-response","rpcId":"<同一 uuid>","result":{"ok":true,"value":<JSON>}}
```

失败响应（HTTP 仍是 200）：

```json
{"type":"server-response","rpcId":"<同一 uuid>",
 "result":{"ok":false,"error":{"code":"<code>","message":"<text>","details":{...}}}}
```

客户端校验 `type`、`rpcId` 回显；`ok:false` 统一映射为 `DshRemoteException(Code, Message, Details)`。

### 2.2 错误码族（节选）

| code | 含义 |
|---|---|
| `gateway/arguments-invalid` | `args` 字段名/数量与 descriptor 不符（如漏 `_request`） |
| `gateway/input-invalid` | 参数字段未过 strict codec（多余字段、类型错） |
| `gateway/result-invalid` | 结果不符合端点声明的 schema |
| `gateway/peer-disposed` | cookie 对应的 Peer 已被销毁（WS 断过）→ 需重新 `/connect` |
| `gateway/cancelled` | 调用方 AbortSignal 中止 |
| `gateway/transport` | 客户端本地：连接不可用/HTTP 传输失败 |
| `session/not-found` | 会话不存在 |
| `session/agent-busy` | 子代理会话必须用 parent 地址（见 §5 坑） |
| `session/invalid-time-zone` | `clientTimeZone` 不是 UTC/IANA 名 |
| `session/model-unavailable` | 模型不可路由 / 无凭据 / effort 不被该模型支持（details `{provider,model}`） |
| `session/writer-held` | 会话正被另一个 DSH 进程写（手机端提示「退出其他 DSH 再试」） |
| `gateway/endpoint-unavailable` | 客户端本地码：端点不存在（HTTP 404），用于区分旧版 DSH 与网络故障 |
| `remote-access/denied` | 非 owner principal 被策略拒绝（owner 不会遇到） |

### 2.3 端点请求/响应样例

**`session/list`**（args 名 `_request`；`{}` 表示全部会话）

```json
{"args":{"_request":{}}}
→ {"items":[SessionSummary...]}
```

**`session/events`**（历史分页，cold-safe）

```json
{"args":{"request":{
  "address":{"kind":"session","sessionId":"session-…"},
  "maxMessages":80,
  "beforeSeq":392
}}}
→ {"cursor":392,"records":[{"type":"event","event":{"type":"step/start","seq":392,"time":1794155408161,"data":{}}}],"hasMore":true}
```

`address` 亦支持 `{"kind":"subagent","parentSessionId","childSessionId","mode":"one-shot|continuable|unknown"}`；
`beforeSeq` 可选（反向分页游标），`maxMessages`/`turnWindow` 可选。

**`session/state`**

```json
{"args":{"request":{"sessionId":"session-…"}}}
→ {"cursor":392,"active":true,"running":true,"queue":[],"pendingInput":[]}
```

`queue[]` 项：`{id, placement:"queued|steering|context", rpcId?, content:ContentBlock[]}`；
`pendingInput[]`：`"approval"|"question"|"other"`。

**`session/prompt`**（`requestId` 由客户端生成，持久化到被接受的 user message 上，同 id 幂等）

```json
{"args":{"request":{
  "requestId":"<uuid>",
  "sessionId":"session-…",
  "mode":"queue",                  // 或 "steer"
  "content":[{"type":"text","text":"hello"}],
  "clientTimeZone":"Asia/Shanghai" // UTC 或 IANA Area/Location；Windows id 会被拒
}}}
→ {"accepted":true}
```

`content` 亦支持 `{type:"image",mediaType,data,name?}`、`{type:"file",receiptId}`；至少一个非空白 text 或附件。

**`session/cancel`**

```json
{"args":{"request":{"sessionId":"session-…"}}}
→ {"accepted":true}
```

**`session/updateQueue`**（编辑/移除/转 steer 一条仍排队中的 prompt）

```json
{"args":{"request":{
  "sessionId":"session-…",
  "itemId":"<queue[].id>",
  "action":{"kind":"edit","content":[{"type":"text","text":"改过的文本"}]}
}}}
→ {"accepted":true}
```

`action.kind` 取值：`edit`（必须带非空 text-only `content`）、`remove`、`steer`（后两者只有 `kind`）。
`itemId` 来自 `session/state.queue[].id`；`session/updateQueue` 会先恢复冷 Agent 再改。

### 2.4 模型目录 / 会话模型选择（v1.6.0）

**`session/modelCatalog`**（unary，无参；args 必须是空对象）

```json
{"args":{}}
→ {"default":{"provider":"opencode-go","model":"deepseek-v4.1-flash","reasoningEffort":"high"},
   "routableProviders":["deepseek-account","deepseek-official","opencode-go"],
   "groups":[{"id":"deepseek-official","name":"DeepSeek 官方",
              "models":[{"id":"deepseek-v4.1-flash","name":"DeepSeek V4.1 Flash",
                         "description":"…",
                         "reasoning":{"efforts":[{"id":"low","name":"低"},{"id":"high","name":"高"}],
                                      "defaultEffort":"high"},
                         "badge":{"text":"推荐","tone":"info"}}]}],
   "failures":[{"id":"deepseek-account","name":"DeepSeek 账号","message":"未配置 API key"}]}
```

- `groups[].id` 就是 `selectModel` 的 `provider`，`models[].id` 就是 `model`；`routableProviders` 只是组 id 派生集合。
- `default` 是**部署全局默认**（新会话默认），不是当前会话的值；当前会话看 `projections.values.modelSelection`。
- `badge.text` 可能是字面量字符串，也可能是 i18n 对象（`{en,zh,…}`，`en` 必填）；客户端两种都要吃。
- 某个 provider 加载失败只进 `failures`，不影响其它分组；无 API key 的 provider 可能整组消失。

**`session/selectModel`**（unary，args 恰好一个 `request`）

```json
{"args":{"request":{
  "sessionId":"session-…","provider":"deepseek-official",
  "model":"deepseek-v4.1-flash","reasoningEffort":"high"
}}}
→ {"selected":{"provider":"deepseek-official","model":"deepseek-v4.1-flash","reasoningEffort":"high"}}
```

- `reasoningEffort` 可省略：服务端按**该模型的** `defaultEffort` 解析并物化进 `selected`；非推理模型或非法 effort →
  `session/model-unavailable`（message 形如 `…does not support reasoning effort "x"`）。
- 运行中/队列非空也可调用：作用于**下一个尚未开始准备的 step**（当前进行中的 step 保持旧路由），并往会话日志追加
  durable 事件 `model/selection`（data = `{provider,model,reasoningEffort?}`）。
- 会 resume 冷会话；成功后**后台**把选择保存为桌面全局默认模型（与桌面 GUI 相同端点、相同行为）。
- `projections.values.modelSelection`：`lastUsed`=最近一次请求头实际使用，`next = pending ?? lastUsed`（可为 null）；
  follow 快照里同样带该字段，可用来校准打开会话时的 chip。

---

## 3. WebSocket 流 mux

```
GET /api/remote.mux  (HTTP/1.1 Upgrade: websocket)
Cookie: dsh-auth-<…>=v2.…
```

- 认证与 HTTP 同一 cookie（`connection.admitConnection`）。cookie 已被认领/失效 → 拒绝 upgrade（`409`/`401`）。
- Host 每 ~2 s 发 WebSocket Ping（opcode 9）；客户端必须回 Pong。`ClientWebSocket` 自动回 Pong，无需业务代码。
- 逻辑流共享同一条 WebSocket；一条流结束不影响其它流。

### 3.1 帧格式

客户端 → Host：

```json
{"type":"open","streamId":"1","endpoint":"<ns>/<method>","payload":{"args":{...}}}
{"type":"item","streamId":"1","value":<json>}
{"type":"end","streamId":"1"}
{"type":"cancel","streamId":"1"}
```

Host → 客户端：

```json
{"type":"item","streamId":"1","value":<json>}
{"type":"end","streamId":"1"}
{"type":"error","streamId":"1","error":{"code":"…","message":"…","details":{…}}}
```

- `open` 必须带 `payload`，且与一元 RPC 一样是 `{"args":{…}}`（stream 端点参数名同样是 `request`）。
- `streamId` 由客户端生成（字符串或数字均可，服务端原样回显）。
- 只有 `mode:'stream'` 的端点能走 `open`；一元端点走 WS 会 `gateway/signature-invalid`。

### 3.2 `session/observeEvent` 帧样例（本仓实测）

打开：

```json
{"type":"open","streamId":"1","endpoint":"session/observeEvent",
 "payload":{"args":{"request":{"address":{"kind":"session","sessionId":"session-62e7…"}}}}}
```

服务端首项（ready，其后才是增量事件）：

```json
{"type":"item","streamId":"1","value":{"type":"ready"}}
```

之后每个事件：

```json
{"type":"item","streamId":"1","value":{"type":"event","event":{
  "type":"assistant/message","seq":393,"time":1794155555000,"data":{"message":{"role":"assistant","content":[…]}}
}}}
```

注意：**event 在 `item.value.event` 里**；`observeEvent` 只给增量、不给快照（要快照用 `session/follow`）。
客户端按 `seq` 去重；断线重开后跳过 `seq <= lastSeq`。

其它流端点：`session/control`（全进程投影 baseline+增量）、`session/observeState`/`session/wait`（运行态）、
`$events`（进程级广播，需 `interactions=answer`）。

### 3.3 `session/follow`：baseline + 可重连续流（M1）

`open` 参数（`args` 里包 `request`）：

```json
{"type":"open","streamId":"1","endpoint":"session/follow",
 "payload":{"args":{"request":{
   "address":{"kind":"session","sessionId":"session-…"},
   "maxMessages":80,
   "assistantStream":true
 }}}}
```

`assistantStream` 是字面量 `true`（strict codec）；同理 `activate` 只在需要后台激活时出现。

流帧（均在 `item.value`）：

```json
{"type":"snapshot","header":{"version":1,"id":"session-…","createdAt":…,"cwd":"…","isSeeded":false},
 "cursor":392,
 "records":[{"type":"event","event":{"type":"…","seq":390,"time":…,"data":{}}}],
 "hasMore":true,
 "projections":{"asOfSeq":392,"values":{…}},
 "assistantStream":{"revision":1,"activeAttempt":{"attemptId":"…","startedAfterSeq":391,"turn":1,"step":2,"nextIndex":3,"stream":[…]}}}

{"type":"event","event":{"type":"assistant/message","seq":393,"time":…,"data":{…}}}

// 仅在 assistantStream:true 时出现、cursorless 的进程内增量（不是 durable SessionEvent）：
{"type":"assistant-stream","frame":{"type":"chunk","attemptId":"…","revision":1,"index":0,"time":…,
  "chunk":{"type":"text-delta","text":"…"}}}
```

- `snapshot` 是**首帧**，`cursor` 是这一页覆盖到的日志 cut；`records` 只含 durable `{type:'event',event}`。
- `session/follow` 请求**没有调用方 cursor 参数**：每次（重）open 都从新 snapshot 开始。因此断线补洞的基线就是
  新 snapshot，而不是旧的 follow 位置。
- `assistant-stream` 帧没有 seq，客户端不能当作 `SessionEvent` 处理（`DshRemote.Core` 在 M1 直接忽略，最终文本以
  `assistant/message` 为准）。

**手机端重连/补洞语义（`DshRemote.Core`）**：

1. 记录最后已产出的 `lastSeq`；WS 断开后重新 `/connect`（新 cookie/Peer），重新 open follow。
2. 新 snapshot 到达时：
   - `snapshot.cursor <= lastSeq`：无缺口，继续读增量；
   - snapshot 的 records 从 `lastSeq+1` 连续覆盖到 `cursor`：直接产出；
   - 否则用 `session/events`（`beforeSeq=throughSeq+1` 反向分页，直到覆盖 `lastSeq+1`）补齐，再产出；
   - 仍不连续（历史已裁剪）→ 抛 `SessionFollowResyncException(lastSeq, throughSeq)`：UI 重新
     `OpenSessionAsync` 拿新 baseline 并合并，然后重新订阅。
3. 增量帧若出现 `seq > lastSeq+1`（live gap，正常不该发生）同样走 `session/events` 补洞，补不上则 resync。
4. 全程按 `seq` 去重；同一会话只允许一个活跃 follow 订阅。

---

## 4. 结果数据形状

### 4.1 `SessionSummary`（`session/list`，真实运行时样本）

```json
{
  "sessionId": "session-82ed888f-…",
  "updatedAt": 1794154489599,
  "agentAvailable": true,
  "formatStatus": "current",
  "running": true,
  "blank": false,
  "cwd": "C:\\Users\\…",
  "parentSessionId": "session-…",     // 仅子代理会话
  "origin": "subagent",               // 仅子代理会话
  "projections": {
    "kind": "sequenced",
    "asOfSeq": 27,
    "values": {
      "title": "排查DSH运行系统卡顿",
      "modelSelection": {
        "lastUsed": {"provider":"…","model":"deepseek-v4.1-flash","reasoningEffort":"high"},
        "next": {"provider":"…","model":"deepseek-v4.1-flash","reasoningEffort":"high"}
      },
      "workingDirectory": "C:\\Users\\…",
      "sessionListMetadata": {"blank": false, "lastPromptAt": 1794154489599},
      "inbox": {...}, "permissions": {...}, "tokenUsage": {...}, "todos": null
    }
  }
}
```

类型声明（`sc_typert_host.js`）：`SessionSummary { agentAvailable; formatStatus?; sessionId; updatedAt; running;
blank; parentSessionId?; origin?:'subagent'; cwd?; projections?: SessionProjectionHints }`，
`SessionProjectionHints { kind:'cached'|'sequenced'; asOfSeq; values: Partial<SessionProjectionSummaryValues> }`。

`DshRemote.Core` 的映射：`Title ← projections.values.title`、`Model ← projections.values.modelSelection.lastUsed.model`
（回退 `next.model`）、`Cwd ← cwd ?? projections.values.workingDirectory`、
`LastPromptAt ← projections.values.sessionListMetadata.lastPromptAt`、`Blank ← blank ?? metadata.blank`、
`ParentSessionId ← parentSessionId`、`Origin ← origin`（二者缺省为 null；`Origin='subagent'` 或
`ParentSessionId != null` 的会话是子代理会话，**不能直接** `session/events`/`session/state`，UI 应过滤/标记）。
未知字段一律忽略。

### 4.2 `SessionEvent` 信封

```json
{"type":"<event type>","seq":392,"time":1794155408161,"data":{},
 "surfaceOp":"append","sourceEventSeqs":[391],"ignorable":true}
```

- `seq` 是会话日志内连续序号（gap 视为协议错误）；`time` 为 epoch ms。
- `surfaceOp` 只出现在 surface 事件（`user/assistant/system/developer/message`、`tool/result`）上。
- 常见事件：`turn/start`、`step/start`、`step/end`、`turn/end`（`data.reason.kind='error'` 表示失败）、
  `user/message`、`assistant/message`、`tool/call`、`tool/result`、`approval/asked`、`approval/decided`。
- 文本提取：`data.message.content[]` 中 `type:'text'` 的片段拼接；`assistant/attempt` 是进程内流式增量，
  重连不重放，正式文本以 `assistant/message` 为准。

**`surfaceOp` / `sourceEventSeqs` / `ignorable`（会话日志 v4 的 surface 语义，压缩依赖它）**

```json
{"type":"tool/result","seq":2270,"time":…,"data":{…},
 "sourceEventSeqs":[38],
 "surfaceOp":{"op":"replace","startSeq":38,"endSeq":38}}
```

- `surfaceOp` 是 surface 事件的必填字段（`system/developer/user/assistant/message`、`tool/result`）：
  - `"append"`：正常尾部追加（普通消息/工具结果）。
  - `{"op":"replace","startSeq":N,"endSeq":M}`：**替换**当前 surface 中从 `startSeq` 到 `endSeq`（含端点）的节点，
    并把本事件插入到该位置。端点是 **surface 位置**而非数值区间（此前发生过替换时 `startSeq` 可能大于 `endSeq`），
    且 `sourceEventSeqs` 必须包含所有被遮蔽节点。replace 后 surface 顺序与 seq 顺序分叉。
  - 缺失（旧版宿主）视为 append。
- `ignorable?: true`：未知事件类型是否可安全跳过；**缺失表示不可忽略**（严格读取器遇到未知且无 `ignorable`
  的事件应拒绝重建，而不是静默丢弃）。
- **聊天转录只渲染 append 的 surface 事件**（与 DSH UI 的 `isAppendSurfaceEvent` 一致）：replace 事件
  （工具结果裁剪副本、摘要检查点 user/message、系统提示替换）**不是新消息**，不得渲染成新的对话行。

**压缩相关事件（`compaction/*` 全部是 log-only，不进入 surface/转录）**

| 事件 | 载荷 | 说明 |
|---|---|---|
| `compaction/start` | `{compactionId, turn, sourceCommandId?}` | 压缩锁开始；`turn=null` 表示手动空闲压缩 |
| `compaction/summary` | `{compactionId, summary:[ContentBlock], shadowedRange, shadowedSeqs, shadowedTokenCount, provider, model, maxTokens?, usage?}` | 摘要结果与统计 |
| `compaction/prune` | `{shadowedRange, shadowedSeqs, shadowedTokenCount}` | 工具结果裁剪计价；与紧随其后的 replace `tool/result` 成对 |
| `compaction/end` | `{compactionId, turn, error?, sourceCommandId?}` | 锁结束；`error` 表示失败尝试 |

摘要压缩的 surface 变化由一条 replace `user/message`（检查点）承载：
`data.source.kind="compact-checkpoint"`、`data.source.compactionId`、`data.content` 为文本块（前导 + 摘要 + 结尾）。
客户端应把它隐藏为普通用户消息，并可选地渲染一张「上下文已压缩」卡片（锚点 = 该检查点事件 seq/time）。
详见 [`docs/session-compaction-support.md`](session-compaction-support.md)。

### 4.3 `session/events` 分页语义与打开会话的懒加载（M6）

**实测语义**（真实桌面 relay，2026-11-09）：

1. `beforeSeq` 是**排他**上界：`beforeSeq=1639` 的页返回 `1606..1638`，不含 1639（不会重复上一页边界）。
2. 返回的 `cursor` 是**读取时的日志 cut（log end）**，不是本页最大 seq；older 页的 `cursor` 可能远大于本页 seq 上界
   （例：older 页 1606..1638，`cursor=1664`）。分页请用 `BeforeSeq`（见下），不要用 `cursor` 当下一页游标。
3. `hasMore=true` 表示还存在更老的记录；`hasMore=false` 时继续用 `beforeSeq` 会得到空页。
4. **`maxMessages` 是消息预算，不是事件条数上限**：页按消息/Turn 对齐并包含其间的所有事件，因此返回的
   `records` 可能远多于 `maxMessages`（实测 `maxMessages=5` → 33 条事件；`maxMessages=30` → 29~209 条事件；
   一个 1665 事件的历史在 `maxMessages=30` 下 9 页翻完）。首屏渲染应按「事件条数」限制显示，不要把
   `maxMessages` 当渲染上限。
5. 页内 `seq` 升序、跨页连续（日志内 seq 连续）；客户端仍应防御性去重/排序。

**Core API（`IHistoryPagingClient`，`DshClient` 实现）**

```csharp
// 首屏：只读最新一页（beforeSeq 省略），立即渲染
SessionHistoryPage latest = await client.GetLatestHistoryAsync(sessionId, maxMessages: 30);
// latest.Records（升序）, latest.Cursor（日志 cut）, latest.HasMoreOlder, latest.BeforeSeq（下一页入参）

// 向上翻页（滚动加载），无重复/无空洞
SessionHistoryPage older = await client.GetOlderHistoryAsync(sessionId, latest.BeforeSeq!.Value, maxMessages: 30);

// 更快首屏：latest 页 + session/state 并行读，不等 session/follow snapshot
SessionLiteSnapshot lite = await client.OpenSessionLiteAsync(sessionId, maxMessages: 30);
```

- `GetLatestHistoryAsync` 把服务器 `hasMore` 暴露为 `HasMoreOlder`，`BeforeSeq` = 本页最老 seq（下一页的 `beforeSeq`），
  页空则为 `null`。
- `GetOlderHistoryAsync` 严格返回 `seq < beforeSeq`（对「inclusive 服务器」也安全），升序去重；`BeforeSeq` 为下一页入参。
- 现有 `GetHistoryAsync(sessionId, beforeSeq, maxMessages)` 保留并委托：`beforeSeq=null` → latest，否则 → older。
- `OpenSessionLiteAsync` 用 latest + state 两个 unary 并行（不打开 follow），并把 baseline 写入 follow 状态：
  随后后台 `FollowEventsAsync` 的 snapshot 会按 seq 跳过 latest 已覆盖的事件（实测 10..12 已显示、follow snapshot
  10..13 只产出 13）。

**UI 建议（M6 首屏）**：先用 `OpenSessionLiteAsync`（或 `GetLatestHistoryAsync`）+ 骨架屏渲染；随后后台
`FollowEventsAsync` 接收实时增量（按 seq 合并去重）；滚动到顶部时用 `page.BeforeSeq` 调 `GetOlderHistoryAsync`
并把老页 prepend（记录按 seq 去重）。不要等 `OpenSessionAsync`（follow snapshot）再显示首屏。

**可观测耗时**：Core 内置 EventSource `DshRemote.Core`（`dotnet-trace collect --providers DshRemote.Core:0xFFFFFFFF:5`）：
- `HistoryPage(kind=latest|older, sessionId, records, cursor, hasMore, elapsedMs)`
- `SessionOpen(kind=follow|lite, …)`、`Connect(host, port, elapsedMs, ok)`
- 同时 `DshClientOptions.Log` 会打印 `history latest: …ms`、`open lite: …ms`、`connect: …ms`（Smoke `-v` 可见）。

**实测打开耗时（relay TLS pin，本地 loopback）**

| 会话 | 旧路径（`OpenSessionAsync` = follow snapshot + state） | 新路径（`OpenSessionLiteAsync` = latest30 + state 并行） | 改善 |
|---|---|---|---|
| 中型（1665 事件） | 112 ms（snapshot 519 条） | **33 ms**（latest 209 条） | 79 ms（71%） |
| 冷/大历史（`session-b185…`，日志需 hydration） | 8814 ms（snapshot 290 条） | **1224 ms**（latest 158 条）※ | 7590 ms（86%） |

※ 顺序执行时 latest/state 的第二次读命中了服务端已加载的日志缓存；冷启动首读本身约 2.8 s（服务端日志 hydration），
换页大小无法消除，但新路径不再付 follow snapshot 的额外成本，且首屏不必等 follow，UI 可先画骨架。

分页校验（真实 relay，`--page-test`）：

| 会话 | 页大小 | 页数 | 记录数 | duplicates | overlaps | boundaryGaps | 终态 |
|---|---|---|---|---|---|---|---|
| `session-62e7…`（1665 事件） | 30 | 9 | 1665 | 0 | 0 | 0 | `hasMoreOlder=false`，oldest=0 |
| 同上 | 200 | 2 | 1665 | 0 | 0 | 0 | 同上 |

---

## 5. M2：`$events` waterfall（审批 / 用户问答）

### 5.1 打开流

`open` endpoint 固定 `$events`，`payload` 必须恰为空 `args`：

```json
{"type":"open","streamId":"1","endpoint":"$events","payload":{"args":{}}}
```

前提：该 WebSocket 的 `/connect` Peer 是 `interactions=answer` 且 principal 为 owner。Host 在流打开后先发
`ready`，并把**尚未结算**的 waterfall 请求重放给每个新开的 answering 流（重连补洞的关键）。

### 5.2 帧（`item.value`）

```json
{"type":"ready","clientId":"<uuid>","host":{"home":"C:\\Users\\…"}}

{"type":"emit","event":"api-session/status","args":["session-…", true]}           // 进程级广播，M2 不消费

{"type":"waterfall","event":"approval/request","eventId":"<uuid>","agentId":"session-…",
 "request":{"toolName":"pwsh","callId":"call_…",
   "reason":"escalate sandbox to danger-full-access: …",
   "displayReason":{"en":"…","zh":"允许本次操作…","ja":"…"}}}

{"type":"waterfall","event":"user-questions/request","eventId":"<uuid>","agentId":"session-…",
 "request":{"questions":[
   {"id":"ab-choice","question":"你喜欢 A 还是 B？","header":"选择",
    "options":[{"label":"A","description":"选择 A。"},{"label":"B","description":"选择 B。"}]}]}}

{"type":"cancel","eventId":"<uuid>"}    // 该请求已在别处结算/取消
```

- `waterfall` 没有 `sessionId` 字段：**`agentId` 就是 sessionId**；`eventId` 是本次请求的关联 id（应答要回传）。
- `displayReason` 是 `locale → string` 的映射（没有 `displayReason` 的老版本只有 `reason`）；手机端显示优先取
  `zh`，否则任取一种语言，再回退 `reason`。
- `request` 里没有 risk/arguments 字段；审批展示不要依赖它们（`DshApprovalRequest.ArgumentsJson` 允许为 null）。
- 请求错过不会重放：断线期间产生的 waterfall 若在重连前已被别处结算，就永久消失；若仍 pending，重连后 host 会重放。

### 5.3 应答：`POST /api/$events/result`

```json
{"type":"client-request","rpcId":"<uuid>","method":"$events/result",
 "payload":{"args":{
   "clientId":"<ready 帧的 clientId>",
   "eventId":"<waterfall 的 eventId>",
   "outcome":{"kind":"result","value":<JSON>}
 }}}
→ {"type":"server-response","rpcId":"<uuid>","result":{"ok":true,"value":null}}
```

- `clientId` 必须来自**当前** `$events` 流的 `ready` 帧；`observeEvent` 的 ready 没有 clientId，不要混用。
- `outcome` 的其他形态：`{"kind":"next"}`（交给下一个应答者，如桌面 UI）、
  `{"kind":"rejected","error":{name,message,code?,details?}}`（应答者出错）。
- 审批的 `value` 是字符串 outcome：`"allowed-once"`（唯一放行）/ `"rejected"` / `"cancelled"` /
  `"unavailable"`。手机端映射 `DshApprovalOutcome.AllowedOnce|Rejected|Cancelled`。
- 问答的 `value` 是答案批次：

```json
{"answers":[{"id":"ab-choice","selected":["A"]}]}
{"answers":[{"id":"q2","selected":[],"custom":"自由输入"}]}
{"cancelled":true}                                   // 整批关闭
```

- 同一 `eventId` 二次应答是客户端 no-op（Core 在本地按 requestId 去重）；host 收到首个结算后会向其它客户端发
  `cancel`。缺少显式过期字段时 `ExpiresAt` 为 null，UI 用本地超时兜底。

### 5.4 手机端语义（`IInteractionClient`）

- `ApprovalsAsync` / `QuestionsAsync` 是应用级长生命周期流：首次订阅时启动唯一的 `$events` pump，连接切换时自动
  重开并**按 requestId 去重**（host 重放的旧请求不会二次产出）；`cancel` 帧到达后该请求不再产出、之后应答为 no-op。
- 重连期间丢失的 waterfall 不可重放：UI 可观察 `DshClient.State` 从 `Reconnecting` 回到 `Connected`，并提示
  “待审批/待回答可能已在桌面处理”。
- M2 的 `$events` pump 与 M1 的 follow 订阅相互独立，可同时开启。

**如何手动触发审批/问答做 E2E**（本仓实测方法）：

1. 审批：在一个 `workspace-write` 会话里 prompt agent 用 `pwsh` 往 **cwd 之外**写文件（如
   `C:\Users\<user>\Downloads\m2-approval-live.txt`）→ 出现 `approval/request`（`toolName:pwsh`）；应答
   `allowed-once` 后会话里会出现 `approval/asked` → `approval/decided` → `tool/result` → `turn/end`。
2. 问答：prompt agent“用 ask_user_question 问一个 A/B 选择题” → 出现 `user-questions/request`；应答
   `{"answers":[{"id":"<question id>","selected":["A"]}]}` 后 agent 会收到答案并继续。

Smoke 命令：

```powershell
# 订阅 $events 打印审批/问答（-v 会打印 ready clientId）
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --events --watch-timeout 30 -v

# E2E：prompt 触发审批并在本客户端回答 allowed-once
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --session <id> \
  --prompt "use pwsh to write X outside cwd" --events --approve-any --outcome allowed-once --watch-timeout 70

# E2E：回答一个已知 requestId 的审批 / 问答
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --approve <requestId> --outcome rejected
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --answer <requestId> '[{"id":"q1","selected":["A"]}]'
```

---

### 5.5 relay 侧自动审批（v0.7.1+，非 DSH 官方协议）

自动审批不再由手机客户端代答，而是由桌面 relay 在**宿主进程内**注册
`approval/request` waterfall 监听器（`{ prepend: true }`）：命中规则直接返回 `'allowed-once'`，
不命中 `next()` 交回原链路。命中时 `api-remotes` 收不到该请求，桌面 UI 与手机都不会弹审批。

规则与状态走 relay 自有 HTTP API（不是 DSH Remote RPC）：

```
GET  /__relay/auto-approve
PUT  /__relay/auto-approve        # POST 等价
```

- 鉴权同 `/__relay/acl/*`：`Authorization: Bearer <pairing.Token>`；经公网网关时附带
  `dsh-auth-*` Cookie。loopback 免令牌只对公开 TLS 监听生效，网关内部明文代理不享受。
- `GET` 返回 `{ enabled, all, workspaces, sessions, revision, updatedAt, stats, recent, error }`；
  `PUT` body 为完整规则文档（`schemaVersion:2`），relay 校验后原子写 `~/.dsh/remote/auto-approve.json`。
- 树形勾选映射：`all:true` = 全部工作区；`workspaces[]` = 完全勾选的工作区（覆盖以后新建会话）；
  `sessions[]` = 部分勾选工作区中被单独勾选的会话（按 session id，沿 `parentSession` 覆盖所有子代理）。
  旧 `schemaVersion:1` 的 `mode` 在加载时自动迁移。
- 只自动 `allowed-once`，不自动拒绝、不碰 `user-questions/request`；规则损坏/匹配异常退回人工。
- 旧 relay 无此路由 → `404`；App 提示需要 relay 0.7.1+。

详见 [auto-approve.md](auto-approve.md)。

---

## 6. M5：工作区 / 归档 / 新建会话 / 图片

### 6.1 `workspace/follow`（stream，无参数）

`open` 的 `args` 必须是空对象（descriptor 没有参数，不像 `session/follow` 有一个 `request`）：

```json
{"type":"open","streamId":"1","endpoint":"workspace/follow","payload":{"args":{}}}
```

首帧是完整 baseline（`item.value`）：

```json
{"type":"baseline","value":{
  "items":[{
    "workspaceId":"ws-uuid",
    "path":"D:\\Code\\VibeCoding\\DSHRemote",
    "title":"DSHRemote",
    "sessionIds":["session-…","session-…"],
    "createdAt":"2026-11-08T10:00:00.000Z",
    "updatedAt":"2026-11-09T02:30:00.000Z"
  }],
  "archivedSessionIds":["session-…"],
  "pinnedSessionIds":[]
}}
```

注意：**`createdAt`/`updatedAt` 是 ISO-8601 字符串**（不是 epoch 数字）；`DshRemote.Core` 归一化为 epoch ms。
后续增量帧：

```json
{"type":"upsert","workspace":{…同一 Workspace 形状…}}
{"type":"remove","workspaceId":"ws-uuid"}
{"type":"order","workspaceIds":["ws-a","ws-b"]}
{"type":"archived","archivedSessionIds":["session-…"]}
{"type":"pinned","pinnedSessionIds":["session-…"]}
{"type":"replace","value":{…完整 baseline 形状…}}   // 权限过滤/重建时的整表替换
```

手机端只需要快照：`GetWorkspaceStateAsync` 打开该流、读第一条 `baseline`（或 `replace`）后关闭；
后续增量帧解析器仍保留（`WorkspaceParsing`，供测试/后续实时侧栏使用）。

### 6.2 归档 / 恢复 / 新建会话

```json
// workspace/archiveSession（running 会话要么报 workspace/session-active，要么传 stopActivity:true）
{"args":{"request":{"sessionId":"session-…","stopActivity":true}}}
→ {"archivedSessionIds":["session-…"]}

// workspace/unarchiveSession
{"args":{"request":{"sessionId":"session-…"}}}
→ {"archivedSessionIds":[]}

// session/create（workspaceId 与 cwd 互斥；都省略时用服务端默认）
{"args":{"request":{"workspaceId":"ws-uuid"}}}          // 或 {"cwd":"D:\\scratch\\x"}
→ {"sessionId":"session-…","agentPreset":"standard"}
```

- 归档成功返回的是「归档集合」而不是完整状态；`IWorkspaceClient` 的 `ArchiveSessionAsync`/`UnarchiveSessionAsync`
  在变更后再读一次 baseline，返回完整 `DshWorkspaceState`。
- 错误码原样保留（`workspace/session-active` 表示会话仍在运行，需 `stopActivity:true` 或先 stop）。

### 6.3 图片发送（prompt content）

`session/prompt` 的 `content` 支持图片块（`data` 是 base64，支持 png/jpeg/webp/gif，`name` 可选）：

```json
{"args":{"request":{
  "requestId":"<uuid>","sessionId":"session-…","mode":"queue",
  "content":[
    {"type":"text","text":"这张图写了什么？"},
    {"type":"image","mediaType":"image/png","data":"iVBORw0KGgo…","name":"shot.png"}
  ],
  "clientTimeZone":"Asia/Shanghai"
}}}
→ {"accepted":true}
```

- 服务端把内联 `data` 落成持久附件，**回显事件里变成 `attachment` 引用**（不再有 base64）。
- `DshRemote.Core`：`SendPromptAsync(sessionId, text, images, steer)`（text 可空但至少要有文字或一张图；
  文字块在前、图片按传入顺序在后；仍用 requestId 幂等 + clientTimeZone）。

### 6.4 图片读取（session 事件 → `session/attachment`）

`user/message`、`assistant/message`、`tool/result`（`read_image`）的 content 里图片块形如：

```json
{"type":"image","attachment":{
  "attachmentId":"sha256:512da3…","mediaType":"image/png",
  "bytes":2541,"width":320,"height":200,"name":"m5-image.png"
}}
```

读取用 unary `session/attachment`（`attachment` 是 strict 对象，只回传引用字段，绝不再带 data）：

```json
{"args":{"request":{
  "sessionId":"session-…",
  "attachment":{"attachmentId":"sha256:512da3…","mediaType":"image/png","bytes":2541,"width":320,"height":200,"name":"m5-image.png"}
}}}
→ {"attachment":{…同上（可能带 originalDimensions）…},"data":"iVBORw0KGgo…"}
```

- `data` 是 base64 字节；`DshRemote.Core.GetImageAsync` 解码为 `byte[]`（UI 建议自行缓存，Core 不缓存）。
- `sessionId` 只作为 gateway 权限 scope，服务端不校验该会话是否引用过此附件。
- 内容解析统一走 `DshContent.PartsOf/TextOf/ImagesOf`（非 text/image 块跳过；畸形 attachment 返回 null 不抛）。

Smoke 命令：

```powershell
# 工作区/归档/新建/图片 E2E
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --workspaces
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --create-session "D:\Code\VibeCoding\DSHRemote\_scratch\m5-e2e"
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --archive <sessionId> --unarchive
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --session <id> --prompt "描述这张图" --image .\shot.png
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --images <id> --get-image <id>

# M6：历史分页/首屏耗时（真实 relay）
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --page-test <sessionId> --page-size 30 -v
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --perf-open <sessionId> -v

# M6：v2 网关配对（正式 gateway.fp/remoteToken 就绪后直接连公网网关）
dotnet run --project app/tools/DshRemote.Smoke -- --pairing <v2-pairing.json> -v
dotnet run --project app/tools/DshRemote.Smoke -- --uri "dshremote://pair?v=2&mode=gateway&host=…&port=9443&token=…&fp=…&name=…"
```

---

## 7. 实机/本机 E2E 结果（2026-11-09）

| 场景 | 结果 |
|---|---|
| relay pairing（`https://127.0.0.1:8443`，SPKI pin）`ConnectAsync` | ✅ 302 + cookie，WS 101，`Connected` |
| `session/list` | ✅ 219→227 条，标题/模型/cwd/updatedAt 解析正确 |
| `session/events`（活跃会话） | ✅ 21 条记录，`cursor=392`、`hasMore=true`，文本提取正确 |
| `session/state` | ✅ `cursor=392 active=true running=true queue=0 pendingInput=[]` |
| `session/observeEvent`（live） | ✅ 依次收到 `assistant/message`(393)、`tool/call`(394)、`tool/result`(395)、`step/end`(396) |
| dev 实例（`http://127.0.0.1:18090`，无 TLS） | ✅ `session/list` 1 条、history/state 正常 |
| `session/prompt` | ✅ `{"accepted":true}`；`user/message`(seq 23) 落盘，随后 `assistant/attempt`→`step/end`→`turn/end`(seq 27) |
| `session/follow`（M1） | ✅ snapshot `records=6 cursor=53 state.running=true`，随后 live `step/start(54)`→`user/message(55)`→`assistant/message(56)`→`step/end(57)`→`turn/end(58)`，无重复无丢失 |
| `$events`（M2） | ✅ `ready {clientId, host.home}` 正常 |
| `approval/request` 全链路（M2） | ✅ 收到 `toolName=pwsh`/`callId`/`displayReason`，应答 `allowed-once`；文件写出 `M2-APPROVAL-OK`，会话日志 `approval/asked`→`approval/decided`→`tool/result`→`assistant/message("DONE")`→`turn/end` |
| `user-questions/request` 全链路（M2） | ✅ 收到 `choice_ab`/文本/选项 `[A,B]`；应答 `{"answers":[{"id":"choice_ab","selected":["A"]}]}`；会话里 `tool/result` 记录该批次，assistant 回复 `"A"` |
| `workspace/follow` baseline（M5） | ✅ 8 workspaces / 45 archived / 0 pinned；ISO `createdAt/updatedAt` 正确归一化 |
| `workspace/archiveSession` + `unarchiveSession`（M5） | ✅ scratch 会话 archive 后 archived 45→46 且包含目标，unarchive 后 46→45 且目标消失 |
| `session/create`（M5） | ✅ 按 cwd（`_scratch\m5-e2e`）与按 workspaceId 各创建一个会话，返回 sessionId |
| `session/prompt` 图片（M5） | ✅ 320×200 PNG（2541B）发送 `accepted`；`user/message` 回显 `attachment sha256:512da3… image/png 2541B 320×200`；agent 回复 `"M5 IMAGE E2E"` |
| `session/attachment`（M5） | ✅ `GetImageAsync` 取回 2541B，mediaType=image/png、尺寸 320×200、magic `89504E470D0A1A0A` |
| 历史分页（M6） | ✅ `beforeSeq` 排他、`cursor`=日志 cut、`maxMessages` 按消息对齐（5→33 条事件）；1665 事件会话 9 页翻完：duplicates/overlaps/boundaryGaps 全 0，终态 `hasMoreOlder=false` |
| 首屏性能（M6） | ✅ 中型会话 112ms→33ms（-71%）；冷/大历史 8814ms→1224ms（-86%），见 §4.3 |
| 配对 v2 解析（M6） | ✅ v2 网关 URI/pairing.json（gateway{url,online,fp}+local fallback）解析；v1 兼容；`--pairing v2.json --loopback` 打印 `mode: gateway online=true localFallback=…` 并连接成功 |

已知坑（实测）：

1. **`args` 参数名**：`session/list` 必须 `{"_request":{}}`；缺了服务端返回 200 但 `result.ok=false` +
   `gateway/arguments-invalid: missing "_request"`。其它端点用 `{"request":{…}}`。
2. **`clientTimeZone` 必须是 UTC 或 IANA 名**。Windows 的 `TimeZoneInfo.Local.Id` 是 `China Standard Time`，
   会被拒（`session/invalid-time-zone`）。Core 用 `TimeZoneInfo.TryConvertWindowsIdToIanaId` 转换，失败则省略该字段。
3. **子代理会话**（`origin:'subagent'`）对 `session/events`/`state` 直接调用会报 `session/agent-busy`：
   必须用 parent 的 durable address。手机 UI 应过滤或不直接打开这类会话。
4. **WebSocket 帧区分**：`end/cancel` 没有 `value`；`item` 的 `value` 才是内层业务帧；`error` 在帧顶层。
5. **重连必须重新 `/connect`**：WS 一断，旧 cookie 的 Peer 就被销毁（HTTP 401），不能只重开 WS。
6. relay 热重载/桌面重启时 `pairing.json` 会短暂消失 1–3 s，且 `token`/`port` 会变：客户端应重读配对文件，
   把“文件不存在”当可重试，而不是致命错误。
7. **`session/follow` 没有调用方 cursor 参数**：每次重开都从新 snapshot 开始，补洞靠 snapshot +
   `session/events` 反查；`assistant-stream` 帧没有 seq，不是 durable 事件。
8. **`clientId` 只来自 `$events` 的 `ready` 帧**；`session/observeEvent` 的 ready 没有 clientId，不能用它应答。
9. **waterfall 请求不重放**：断线期间产生且已被别处结算的审批/问答会永久消失；重连后只重放仍 pending 的请求。
   UI 看到 `Reconnecting` 后应提示“待审批可能已在桌面处理”。
10. **`session/updateQueue` 是 strict schema**：`edit` 必须带非空 text-only `content`；`remove`/`steer` 只带 `kind`。
11. 同一 `sessionId` 同时只支持一个 `FollowEventsAsync` 订阅；切换会话前先 Dispose 旧枚举。

---

## 8. 实现对应关系

| 协议要素 | 实现文件 |
|---|---|
| 配对解析/`dshremote://` URI | `app/src/DshRemote.Core/Contracts.cs`、`DshPairingFile.cs` |
| SPKI pinning | `app/src/DshRemote.Core/TlsPinning.cs` |
| RPC 信封/错误映射 | `Protocol/RpcWire.cs` |
| 端点 args 构造（`_request`/`request`/`$events/result`） | `Protocol/DshArgs.cs` |
| 结果解析（list/events/state/observe） | `Protocol/DshParsing.cs` |
| follow 帧/快照/连续性 | `Protocol/FollowParsing.cs` |
| `$events` waterfall 帧解析 | `Protocol/InteractionParsing.cs` |
| mux 帧与 WebSocket 传输 | `Protocol/MuxWire.cs`、`MuxConnection.cs` |
| `/connect` + WS upgrade | `ConnectionLauncher.cs` |
| 状态机/重连/等待代际 | `DshClient.cs`、`ReconnectPolicy.cs` |
| M1 follow + 补洞 + steer/updateQueue | `DshClient.Follow.cs`、`SessionFollowResyncException.cs` |
| M2 审批/问答（`$events` + `$events/result`） | `DshClient.Interactions.cs` |
| workspace 帧/归档/创建结果解析 | `Protocol/WorkspaceParsing.cs` |
| M5 工作区/归档/新建/图片读取 | `DshClient.Workspace.cs` |
| M6 分页/懒加载 API | `DshClient.History.cs`（`IHistoryPagingClient`）、`SessionHistoryPage.HasMoreOlder/BeforeSeq` |
| M6 耗时观测 | `Protocol/DshDiagnostics.cs`（EventSource `DshRemote.Core`） |
| 配对 v2（网关模式）解析 | `Contracts.cs`（`DshPairingMode`/`DshPairingInfo`）、`DshPairingFile.cs`；文档 `docs/remote-v2.md` |
| 图片 content 解析（共享给 UI） | `Contracts.cs` 的 `DshContent` |
| E2E 工具 | `app/tools/DshRemote.Smoke/` |

用法：

```powershell
# relay TLS（SPKI pin，默认读 %USERPROFILE%\.dsh\remote\pairing.json）
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --sessions 5 --history 3

# 本机 dev 实例（无 TLS，仅调试用）
dotnet run --project app/tools/DshRemote.Smoke -- --dev "http://127.0.0.1:18090/?token=<token>" --session <id>

# 订阅实时事件（Ctrl+C、--watch-limit 或 --watch-timeout 停止）
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --watch <sessionId> --watch-limit 20 --watch-timeout 60

# M1：snapshot + 断线补洞的 follow 流
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --follow <sessionId> --watch-limit 10 --watch-timeout 60

# M2：$events 审批/问答（-v 打印 ready clientId；--events 打印请求）
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --events --watch-timeout 30 -v
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --approve <requestId> --outcome allowed-once
dotnet run --project app/tools/DshRemote.Smoke -- --loopback --answer <requestId> '[{"id":"q1","selected":["A"]}]'
```
