# DSH 宿主内部调研：插件注入面、网络面、事件模型与加载机制

> 调研任务：`task-1`（只读调研，不改产品代码）
> 调研对象：`D:\Program Files\DeepSeek Harness\resources\app.asar`（Electron asar，213 MB，22 102 个文件）
> 运行实例：`DeepSeek Harness.exe --expose-internals ...dsh-desktop-host\lib\index.js`，profile `C:\Users\user\.dsh\profiles\desktop`，HTTP 监听 `127.0.0.1:54306`
> 运行时版本：`@deepseek-ai/dsh-desktop-runtime 0.2.2-rc.1`，`@deepseek-ai/dsh 0.2.2-rc.1`，`@deepseek-ai/cordis 4.1.0`，Node v24.14.0

**约定**：`[已验证]` = 有代码/运行时证据，附 asar 内路径与行号；`[推测]` = 由文档/结构推断，未取到直接证据。

**复现工具（本仓库）**

| 文件 | 作用 |
|---|---|
| `scripts/asar-extract.mjs` | 零依赖 asar 读取器：`list / read <innerPath> / pkgs / stat`（处理本机 asar 的“JSON 头从偏移 16 开始”变体） |
| `scripts/dump-remote-contract.mjs` | 从 34 个 `lib/typert.host.js` 解析出全部 Typert Remote 端点契约（参数/结果 JSON Schema、事件签名） |
| `scripts/remote-contract.json` | 上述脚本产物：34 namespace / 193 endpoint（机器可读契约，供 Android 端生成 DTO） |

---

## 0. 结论速览（TL;DR）

1. **DSH 已经有一套完整的“远程客户端”协议，并且原生对外暴露**：`POST /api/<ns>/<method>`（JSON 一元 RPC）+ WebSocket `/api/remote.mux`（逻辑流 mux）+ `$events` 逻辑流（Host 事件广播/审批 waterfall）。它不是一个网页遥控器专用接口，而是 Web GUI/未来 TUI/内置客户端共享的协议层。`[已验证]`
2. **鉴权是“进程启动 token → 签名 cookie”**：`GET /?token=<launchToken>` 换浏览器 cookie；`GET /connect?token=...&interactions=answer|none` 换 **API cookie + 302 → ./api/remote.mux**，并创建一个 `principal.kind='owner'` 的 Peer。手机端可以作为“API 客户端”拿 owner 身份，走同一套权限模型。`[已验证]`
3. **审批（approval）可以被插件/host 注入，而且已经通过官方事件通道对外转发**：`ctx.approval` 是 Cordis 服务（`ApprovalService`，ctx key `approval`），应答者 = `approval/request` waterfall 监听器；`dsh-api-remotes` 的转发白名单里 `approval/request` 就是 `waterfall` 模式，`$events` 流上的 `{type:'waterfall'}` 帧可直接被远程客户端应答（`$events/result`）。`[已验证]`
4. **`ctx.sessionController` 的方法签名已全部取到**（含 `list/prompt/events/page/observeEvent/observeState/wait/follow/projections/control/fork/cancel/updateQueue/selectModel/...`），且其内部件 `historyController / stateController / webController / agents / commands / controlState / listState` 对插件可见（运行时自省证实）。`[已验证]`
5. **插件加载**：profile 目录 + bundle 层 + `$DSH_HOME/profiles/<p>/cordis.patch.yml`；`insert` 支持包标识符/绝对路径/文件 URL/相对 `./`，支持 `!!js` 表达式；HMR 监听 profile manifest 与两份 user patch，并对 profile `node_modules`（含链接包目标）做模块热替换。`[已验证]`
6. **选型建议**：**以“官方 Remote 协议”为传输与鉴权底座（自建 Android 客户端实现协议子集），DSH 侧只保留一个极薄的“配对/发现/归一化补充”插件**；不建议继续把“自建 HTTP 网关”当长期主干（现状原型适合继续做契约验证）。理由与风险见 §7、§8。

---

## 0.5 Lead 四问速答（确定性答案）

### Q1. 桌面 runtime 有 `ctx.webServer` 吗？54306 谁提供、token/cookie 谁签发、能否靠 patch 配置对外暴露？

**有。** 桌面版不是“file:// + IPC”老形态，而是**在同一进程内跑 Web 应用组合**：

- `dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js` L441-465：`runProfile({ profile:'desktop', args:['--no-open','--host','127.0.0.1','--port','0'] , patchFiles: [] })`；L605 `ctx.connection.authenticatedUrl(`${ctx.webServer.protocol}//127.0.0.1:${ctx.webServer.port}`)` 把带 token 的 URL 交给 Electron。`[已验证]`
  → 本机 `127.0.0.1:54306` = PID 29432（该 desktop-host 进程）里的 **`@deepseek-ai/dsh-host-webserver`（`webserver` 行）**；port 由 `--port 0` 随机分配。
- 该监听上的路由归属：`@deepseek-ai/dsh-client-connection` 拥有 `/api`（RPC 桥）、`/connect`（token 换 cookie）、`/api/remote.mux`（WS 升级）；`@deepseek-ai/dsh-api-gateway` 在 `/api` 上认领 Remote 端点；`frontend-static`/`web-runtime` 拥有 SPA dist 与 index 渲染；`dsh-api-session-controller` 等业务包注册 `session/*` 等端点。`[已验证]`（`web-app/cordis.patch.yml` L175-245）
- **token 签发**：`dsh-client-connection` 的 `BrowserAuth` 持有**进程级随机 `launchToken`**；`authenticatedUrl()`（`lib/index.js` L440-444）把它拼成 `?token=`。`[已验证]`
- **cookie 签发**：同一 `BrowserAuth`，HMAC 密钥来自 `ctx.credentials` 的 `client-connection/browser-session` grant（持久化在 `$DSH_HOME/.credentials.yaml`）；cookie 载荷 `{version:2, authority, id, issuedAt, expiresAt}`，authority 绑定规范化 **hostname+port**（HTTPS 另带 scheme audience），默认 30 天，`HttpOnly; SameSite=Strict; Path=/`，仅 TLS 监听器加 `Secure`（`lib/index.js` L335/L440/L505-570；README「浏览器认证与请求信任」）。`[已验证]`

**“复用官方 Web 服务对外暴露”能不能只靠配置？——分两种宿主：**

| 宿主 | 结论 | 证据 |
|---|---|---|
| `dsh --profile web` | **可以，一等公民**：`--host <具体IPv4/IPv6> --trusted-host <authority> [--tls-cert/--tls-key] [--public-url]`；通配地址（`0.0.0.0`/`::`）被**拒绝**；非 loopback 未启 TLS 会警告明文 | web-app README L62-77；host-webserver README |
| **desktop（本机）** | **只能靠 patch 硬改组合，且不划算**：(a) `--host 127.0.0.1 --port 0` 是 desktop-host **代码写死的 CLI 参数**，不是 profile 配置；(b) 用户 patch 可以覆盖 `webserver` 行的 `config`（host/port/tls）与 `connection` 行的 `trustedHosts`（bundle 里是 `!!js ctx.webStartup.host` 等表达式，patch 会**整体替换** config），但 desktop-host 只传 `--host/--port/--no-open`，**从不传 `--trusted-host`**；(c) Electron renderer 加载的是 `authenticatedUrl(127.0.0.1:port)`，一旦把监听改到 LAN IP，**自带 renderer 会失联** | desktop-host L449-455；web-app patch L175-184、L230-237 |

**建议**：桌面端保持 loopback 监听 + **隧道/反向代理**（原型 18082 已这么跑；本机可见 frp/ssh 隧道痕迹）；真要“对外 Web 实例”，用 `dsh --profile web --host <LAN IP> --trusted-host <域名>` 单独起，但要意识到**同一 `$DSH_HOME` 下两个进程会争会话写锁**（`session/writer-held`）`[推测：需实测]`。

### Q2. 审批接缝：waterfall 形态、宿主插件如何注册应答者、如何与桌面 UI 竞争、取消、手机侧怎么结算

- **事件键**：`approval/request`，**Cordis agent-scoped waterfall**（不是普通 emit）。派发点：`dsh/node_modules/@deepseek-ai/dsh-user-approval/lib/types/index.js` L197
  `this.ctx.waterfall(scopeTarget(req.agent, req.agent), 'approval/request', req, () => Promise.resolve('unavailable'))`。
- **监听器签名**：`ctx.on('approval/request', function (request, next) { … })`，`request = { agent, toolName, callId?, reason?, displayReason?, signal?: AbortSignal }`；返回值必须是 `'allowed-once' | 'rejected' | 'cancelled'`（其它值/抛错 → 归一化 `'unavailable'`，fail-closed）；**返回即结算**，**不返回而 `return next()` 即委托**给链上后续监听器；链尾缺省 `'unavailable'`。`[已验证]`（同文件 L182-219）
- **注册方式**：全局应答者 = 普通 `ctx.on(...)`；只服务某个 agent = 在该 agent 的 scope 里注册（`scopeTarget(agent, agent)` 语义，与 liveshare 的 `share-viewer` policy 同理）。宿主插件若把“问手机”实现在 Host 侧，**注意 waterfall 是顺序语义：你的监听器 await 手机回答期间，后面的桌面 UI 监听器收不到该请求**；因此推荐**不要新增 Host 应答者**，让手机作为**客户端**参与（见下）。
- **桌面的“自带应答者”其实是客户端，不是 Host 监听器**：`dsh-api-remotes` 把 `approval/request` 作为 waterfall 转发（白名单 `lib/index.js` L26-29）→ Gateway 对每个 `interactions:'answer'` 的 Peer 投递 `{type:'waterfall', event, eventId, agentId, request}`（`gateway/lib/index.js` L1117-1145），Web UI 用 `$events/result` 回答。**手机与桌面因此是并列竞争，先答者胜**；`receiveRemoteEventResult` 首个结果即 settle，并向其它投递客户端推 `{type:'cancel', eventId}`（L1146-1156、L1175-1186）。`[已验证]`
- **取消（AbortSignal）**：`request.signal` 中止 → 服务侧 `decide()` 直接判 `'cancelled'`（L184-218），Gateway 侧 `cancelRemoteEvent` → 给所有投递方推 `{type:'cancel', eventId}`；**迟到应答被丢弃**（pending 已移除）。手机端收到 `cancel` 帧即关闭弹窗即可。`[已验证]`
- **手机侧“批准/拒绝一次”**：`POST /api/$events/result`，payload
  `{ clientId, eventId, outcome: { kind:'result', value:'allowed-once' | 'rejected' } }`
  （`clientId` 来自 `$events` 首个 ready 帧；委托下一个应答者用 `{kind:'next'}`；想让 Host 侧抛错用 `{kind:'rejected', error:{name,message,code?,details?}}`）。`[已验证]`（`stream-protocol.js` L18-54、L111-130）
- **`ctx.approval`（`ApprovalService`，服务键 `approval`）公开方法**：`request(req)`（要求 turn 未结束，写 `approval/asked`+`approval/decided` 审计）、`setPolicy(agent, 'ask'|'never')`、`effectivePolicy(session)`、`overrideOf(session)`（另有模块级 `setApprovalPolicy(session, policy)`、常量 `APPROVAL_POLICIES`、`ApprovalRequestId` brand）。`[已验证]`（`user-approval/lib/types/index.js` L54-118、L137-222）
- **策略拦截**：`policy==='never'` 在分发**之前**返回 `'rejected'`（L191-192），任何应答者都无法绕过 → 手机端必须把“当前策略”展示出来（本机现在就是 `danger-full-access → never`）。

### Q3. 用户问答接缝：`ctx.userQuestions` 的签名与 answerer 注册

服务实现：`dsh/node_modules/@deepseek-ai/dsh-user-questions/lib/types/index.js`；服务键 **`userQuestions`**，`static Config = z.object({})`（L81-95）。

| 方法 | 签名（L 行号） | 说明 |
|---|---|---|
| `ask(request)` | `{ agent?, questions: Question[], signal?, wait? }` → `Promise<answer>`（L300-364） | 内部 `ctx.waterfall(scopeTarget(agent,agent), 'user-questions/request', {...request, agent}, noAnswerer)`；无 agent 时用未限定 scope 的 waterfall；无人应答 → `UserQuestionError('NO_PROVIDER')` |
| `askTimed(request, callId, timeoutMs)` | L245-283 | 前台限时；超时返回 `{ pending: true, callId }`（问题转入 `continued`），`timeoutMs ∈ [1, 2^31-1]` |
| `attachWait(agent, callId, signal)` | `@Remote({mode:'stream'})`，L227-232 | 回答 UI 接手倒计时，产出 `{ remainingMs }`；关闭流即释放 claim |
| `answer(agent, callId, answer)` | `answer = { answers: [{ id, selected: string[], custom? }] }` → `boolean`（L179-219） | 只对 **continued** 问题；把回复作为 `user-question-reply` 消息 `agent.steer()` 进会话；重复排队 → `REPLY_QUEUED`，批次不匹配 → `BAD_ANSWER` |

- **answerer 注册**：与审批完全同构 —— `ctx.on('user-questions/request', (request, next) => …)`（agent-scoped waterfall；`next()` 委托）；应答返回值即 `answer` 对象结构（`{answers:[…]}`），用户关闭面板 → `{cancelled:true}`（SDK 协议里映射 `ASK_CANCELLED`）。
- **回答 RPC 是否同一套**：是。同一条 `$events` waterfall 通道（`api-remotes` 白名单 L143-146）；如果是 **continued** 问题（工具已返回 pending），走一元 Remote `userQuestions/answer`；`userQuestions/attachWait` 是配套 stream。两者都在 `session` 资源授权下（`agent` 为 lookup 参数）。`[已验证]`（`scripts/remote-contract.json` → `@deepseek-ai/dsh-user-questions`）
- 线上形状（wire）：`answer` 参数 = `{ agent:<agentId>, callId, answer:{answers:[{id,selected[],custom?}]} }`；`attachWait` 参数 = `{ agent:<agentId>, callId }`，结果 `{remainingMs:number}`。`[已验证：strict JSON Schema]`

### Q4. `sessionController` 控制方法（签名 + 路径）

文件：`dsh/node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js`；Remote namespace = `session`，Host 服务键 = `sessionController`（L264）。

| 用途 | 方法 | 签名/参数 | 返回 | 行 |
|---|---|---|---|---|
| 发消息 | `prompt(request, signal?)` | `{ requestId, sessionId, mode:'queue'\|'steer', content:[{type:'text',text}\|{type:'image',mediaType,data,name?}\|{type:'file',receiptId}], clientTimeZone? }`；`requestId` = 幂等键（进 queue/日志后重试返回原接受结果） | `{ accepted: true }` | L535-555 |
| 无独立 `steer` 方法 | — | 用 `prompt({mode:'steer'})`（插队进当前轮） | 同上 | L535 |
| 取消当前轮 | `cancel(request)` | `{ sessionId }`；**不清空 pending inbox** | `{ accepted: true }` | L577 |
| 队列编辑 | `updateQueue(request)` | `{ sessionId, … }`（Edit/Remove/QueueDock Steer；仅当前 projection 满足条件的在线 child 例外可写） | `{ accepted: true }` | L569 |
| 列表 | `list(_request, signal?)` | `{}` | `{ items: SessionSummary[] }`（字段见 §2.1） | L371 |
| 历史分页 | `events(request, signal?)`（`page` 同义、已弃用） | `{ address, throughSeq?, beforeSeq?, maxMessages?, turnWindow?:{minMessages,minTurns} }` | `{ cursor, records:[{type:'event',event}], hasMore }`；`cursor` 为含边界末项 seq | L596 / L587 |
| 订阅增量 | `observeEvent(request, signal?)` | `{ address }`，stream | `{type:'ready'}` → `{type:'event',event}` | L605 |
| 订阅+快照 | `follow(request, signal?)` | `{ address, activate?, assistantStream?, maxMessages?, turnWindow? }`，stream | 首帧 `{type:'snapshot',header,projections:{asOfSeq,values},cursor,records,hasMore,assistantStream?}`，后续 `{type:'event',event}` | L651 |
| 被动状态 | `state(request, signal?)` | `{ sessionId }` | `{ cursor, active, running, queue:[{id,placement,rpcId?,content}], pendingInput:['approval'\|'question'\|'other'] }` | L623 |
| 等待静默 | `wait(request, signal?)` | `{ sessionId, timeoutMs?, afterSeq?, maxMessages? }` | `{ reason:'idle'\|'input-required'\|'timeout', state, output:{records,truncated} }` | L641 |
| 其余 | `create/list/search/fork/rename/selectModel/modelCatalog/attachment/projections/observeProjections/observeState/control` | 见 §2.1 表 | | L383-686 |

`address` 两种：`{kind:'session', sessionId}` 或 `{kind:'subagent', parentSessionId, childSessionId, mode:'one-shot'|'continuable'|'unknown'}`（`session/observeEvent` 的 strict schema 原文见 `scripts/remote-contract.json`）。

---

## 1. 运行形态与版本

### 1.1 桌面版 = 内嵌 Web 载体 `[已验证]`

- `dsh-desktop-host` 依赖 `@deepseek-ai/dsh-host-webserver` 与 `@deepseek-ai/dsh-client-connection`
  （`dsh/node_modules/@deepseek-ai/dsh-desktop-host/package.json` L20-21）。
- 启动逻辑：`dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js` L427-465 `main()` 调
  `runProfile({ profile: 'desktop', args: ['--no-open','--host','127.0.0.1','--port','0'] })`，
  L605 `ctx.connection.authenticatedUrl(`${ctx.webServer.protocol}//127.0.0.1:${ctx.webServer.port}`)` 并把该 URL 交给 Electron。
- 因此**端口是随机的**（`--port 0`）：本次实例 `netstat` 显示 `127.0.0.1:54306` 属于 PID 29432（即 desktop host 进程），不是固定 54306；`dsh --profile web` 的默认端口是 `3080`（`web-app/cordis.patch.yml` L180 `port: !!js ctx.webStartup.port ?? 3080`）。
  → **手机端不能写死端口，必须做发现/配对**（风险 R2）。

### 1.2 Web GUI 宿主的 host 组合 `[已验证]`

`dsh/node_modules/@deepseek-ai/dsh-web-app/cordis.patch.yml`（bundle patch，按 `dsh.bundle.patch` 应用）：

| 行 id | 包 | 作用 |
|---|---|---|
| `web-startup` | `@deepseek-ai/dsh-web-app/startup` | `--host/--port/--tls-cert/--tls-key/--public-url/--trusted-host/--no-open` |
| `webserver` | `@deepseek-ai/dsh-host-webserver` | 具名 HTTP 路由 + upgrade + index 注入 + fallback 席位（L175-184） |
| `web-runtime` | `@deepseek-ai/dsh-web-app` | dist 解析、启动 URL 行、浏览器交接（L193-200） |
| `connection` | `@deepseek-ai/dsh-client-connection` | `/api` 桥、认证、Host/Origin 栅栏、WebSocket 升级（L230-237） |
| `api-remotes` | `@deepseek-ai/dsh-api-remotes` | 转发 `$events` 事件白名单（L244-245） |
| `session-controller` | `@deepseek-ai/dsh-api-session-controller` | `ctx.sessionController` + `session` Remote namespace（L116-118） |

`base` 层（`dsh/node_modules/@deepseek-ai/dsh-base/cordis.patch.yml`）：L46-58 `typert` / `typert-loader` / `typert-gateway`（`@deepseek-ai/dsh-api-gateway`）/ `remote-access`；L259-262 审批服务；L264-276 权限预设（`read-only`/`workspace-write`/`danger-full-access`）。

---

## 2. 插件注入面（Cordis host 侧）

### 2.1 `ctx.sessionController` —— 服务键、类、签名 `[已验证]`

定义文件：`dsh/node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js`

- L264 `super(ctx, 'sessionController', { namespace: 'session' })` → Host 服务键 **`sessionController`**，Remote namespace **`session`**。
- L225-239 `static inject = ['agentDefaultModel','agents','attachments','fileUploads','fs','llm','remoteAccess','sessions','sessionProjections','sessionQuery','typert','typertGateway','workspaceRegistry']`
- L240-243 `static Config = z.object({ nativeOpen: z.boolean(), listWorkSliceMs: z.natural().min(1).default(16) })`（zod 配置）

方法签名（`(request, signal?) =>`，逐一来自该类）：

| 方法 | 签名要点 | 返回 |
|---|---|---|
| `list(_request, signal)` | `{}`（预留空请求） | `{ items: SessionSummary[] }`（L371） |
| `search(request, signal)` | `{ query }` | `{ items, hasMore }`（L383） |
| `create(request)` | `{ sessionId?, cwd?, agentPreset?, ... }` | `{ sessionId, agentPreset? }`（L392 → `commands.create`） |
| `prompt(request, signal)` | `{ requestId, sessionId, mode:'queue'\|'steer', content:[text\|image\|file], clientTimeZone? }` | `{ accepted: true }`（L535；schema 见 §5） |
| `selectModel(request)` | `{ sessionId, provider, model, reasoningEffort? }` | 归一化选择（L400） |
| `modelCatalog()` | 无参 | provider 分组模型目录 + 默认值 + 失败项（L424） |
| `rename(request)` | `{ sessionId, title }` | `{ title, seq }`（L508） |
| `fork(request)` | `{ sessionId, atSeq?, allowMigration? }` | `{ sessionId }`（L518） |
| `attachment(request)` | `{ sessionId, attachment:{attachmentId,mediaType,bytes,width,height,...} }` | `{ attachment, data(base64) }`（L561） |
| `updateQueue(request)` | 队列项变更（Edit/Remove/QueueDock Steer） | `{ accepted: true }`（L569） |
| `cancel(request)` | `{ sessionId }` | `{ accepted: true }`（L577） |
| `events(request, signal)` | `{ address, throughSeq?, beforeSeq?, maxMessages?, turnWindow? }` | `{ cursor, records:[{type:'event',event}], hasMore }`（L596；`page` 为已弃用同义入口 L587） |
| `observeEvent(request, signal)` | `{ address }`，`@Remote({mode:'stream'})` | `AsyncIterable<{type:'ready'} \| {type:'event',event}>`（L605） |
| `observeProjections(request, signal)` | `{ sessionId }` | `ready` + `{type:'projection',seq,changes}` |
| `state(request, signal)` | `{ sessionId }` | `{ cursor, active, running, queue, pendingInput }`（L623） |
| `observeState(request, signal)` | `{ sessionId }` | 完整 state 替换帧 |
| `wait(request, signal)` | `{ sessionId, timeoutMs?, afterSeq?, maxMessages? }` | `{ reason:'idle'\|'input-required'\|'timeout', state, output:{records,truncated} }`（L641） |
| `follow(request, signal)` | `{ address, activate?, assistantStream?, maxMessages?, turnWindow? }`，stream | 首帧 `{type:'snapshot',header,projections,cursor,records,hasMore,assistantStream?}` 后续 `{type:'event',event}`（L651 → webController） |
| `projections(request, signal)` | `{ sessionId }` | `{kind:'sequenced',asOfSeq,values}` \| `{kind:'migration-required',values}` \| `null`（L660） |
| `control(signal)` | 无参，stream | `{type:'baseline',value:{projections,...}}` + `{type:'projection',sessionId,...}`（L668） |
| `resolveAgent(sessionId)` | 供其他 host API 域复用的 Agent 解析（L362） |
| `canOpenWorkspacePath()` / `workspaceDesktop()` / `openWorkspacePath()` / `workspacePathApplications()` | 桌面打开能力（L431/L438/L449/L475） |

**运行时自省交叉验证**（`GET http://127.0.0.1:18082/api/debug?token=…`，原型插件只读端点）返回的 `scMethods` 与上表一致，另外直接暴露内部件：
`historyController, stateController, webController, agents, commands, controlState, listState`。

**`SessionSummary`（`list` 行）运行时实测字段**：

```json
{ "sessionId": "session-b7e5ddc7-…", "updatedAt": 1794151867995, "agentAvailable": true,
  "formatStatus": "current", "running": true, "blank": false,
  "cwd": "D:\\Code\\VibeCoding\\Arc.Avalonia",
  "projections": { "kind": "sequenced", "asOfSeq": 1559,
                   "values": { "title": "…", "tokenUsage": {…}, "userQuestions": {…}, … } } }
```
（原型 `/api/sessions` 还读取 `parentSessionId`、`origin` 用于过滤 subagent 行。）

### 2.2 内部子控制器（插件可直接用）`[已验证]`

| 内部件 | 关键方法 | 证据 |
|---|---|---|
| `sessionController.historyController` | `events(request, signal)`、`observeEvent`、`projections`、`observeProjections`、`assistantOutput(sessionId, afterSeq, throughSeq, maxMessages, signal)` | `lib/types/history/controller.js` L75/L95/L116/L160/L184 |
| `sessionController.stateController` | `state`、`observeState`、`wait` | `lib/types/state/controller.js` |
| `sessionController.webController` | `follow`（snapshot 聚合） | `lib/types/web/controller.js` L79-137 |
| `sessionController.commands` | `create/prompt/rename/fork/cancel/updateQueue/attachment/selectModel` | `lib/types/commands.js` |

`observeEvent` 的帧序列（`history/controller.js` L95-109 + `history/listener.js` L25-32、L68）：
`{type:'ready'}` → 每个已提交事件 `{type:'event', event}`（`entryFor(event)`）；listener 只挂 `session/event` 与 `session/created`，**不读快照、不激活 Agent**。

### 2.3 审批（approval）：可注入性 `[已验证]`

服务定义：`dsh/node_modules/@deepseek-ai/dsh-user-approval/lib/types/index.js`

- L65-75 `export class ApprovalService extends Service { static inject=['sessionProjections']; static Config = z.object({ policy: z.union(['ask','never']).default('ask') }); constructor(...){ super(ctx, 'approval') } }`
  → Host 服务键 **`approval`**，插件可 `inject: ['approval']`。
- L137-154 `request(req)`：`req = { agent, toolName, callId?, reason?, displayReason?, signal }`；先 `session.append('approval/asked', {id,toolName,callId?,reason?})`，再 `decide()`，最后 `session.append('approval/decided', {id,outcome})`；**要求 turn 未结束**，否则抛错（审计对必须在 turn 边界内）。
- L182-219 `decide()`：`ctx.waterfall(scopeTarget(req.agent, req.agent), 'approval/request', req, () => Promise.resolve('unavailable'))`
  → **应答者实现方式 = 注册 `approval/request` waterfall 监听器**；返回 `'allowed-once' | 'rejected' | 'cancelled'`；抛错/非法值归一化为 `'unavailable'`（fail-closed）；`signal` 中止 → `'cancelled'`；`policy==='never'` 在分发前直接 `'rejected'`（L191-192）。
- 策略默认值来自配置（`base/cordis.patch.yml` L259-262）：`policy: (DSH_PERMISSION_MODE ?? 'workspace-write') === 'danger-full-access' ? 'never' : 'ask'`。本会话是 `danger-full-access` ⇒ `never`（故系统提示写“approval prompts are disabled”）。
- `setPolicy(agent, policy)` L106-118（会话内切换 + 注入一条让模型知道策略变化的 user 消息）；`setApprovalPolicy(session, policy)` L54-59（初始化期直接写日志）。

**审批已经能到达远程客户端**（这是本次调研最重要的发现之一）`[已验证]`：

- `dsh/node_modules/@deepseek-ai/dsh-api-remotes/lib/index.js` L21-147 的 `API_REMOTE_FORWARDED_EVENTS` 白名单里：
  - L26-29 `{ event: 'approval/request', mode: 'waterfall' }`
  - L143-146 `{ event: 'user-questions/request', mode: 'waterfall' }`
- Gateway 把 waterfall 投影成 `$events` 帧（`dsh/node_modules/@deepseek-ai/dsh-api-gateway/lib/index.js` L1117-1145）：
  `{ type:'waterfall', event, eventId, agentId, request }`，其中 `request` 已剥掉 `agent`/`signal`（`lib/types/stream-protocol.js` L61-86），即 `{ toolName, callId?, reason?, displayReason? }`
- 客户端应答：一元 RPC `$events/result`，payload `{ clientId, eventId, outcome }`，`outcome.kind ∈ {'result'|'next'|'rejected'}`（`stream-protocol.js` L18-54；host 侧 `index.js` L1146-1156）。
- 语义：`next()` 委托给后续 Host listener（最终缺省 `unavailable`）；`rejected` 让 Host 侧该次 ask 以取消/失败结束。

### 2.4 用户问答（ask_user_question 同族）`[已验证]`

- 服务/事件：`ctx.userQuestions`（`@deepseek-ai/dsh-user-questions`，`base/cordis.patch.yml` L79-80），**也是 `user-questions/request` waterfall**，同样在转发白名单中；Remote 端点 `userQuestions/*`（`lib/typert.host.js`：`answer`、`attachWait` 等）。
- 对远程客户端的意义：手机端可以在 `$events` 上同时处理“审批”和“提问”两类交互，不需要另建通道。

### 2.5 其他对远程客户端有用的 host 服务 `[已验证/部分]`

| 服务键 | 提供包 | 对远程端的用途 |
|---|---|---|
| `connection` | `dsh-client-connection` | 认证、Peer、`/api` FetchHandler、`authenticatedUrl()`、`admit()/createPeer()` |
| `webServer` | `dsh-host-webserver` | `register(route)`、`registerUpgrade()`、`port`、`protocol`、`getOrigin()` |
| `typertGateway` | `dsh-api-gateway` | `invoke()`、`stream()`、`registerRemoteEvents()`、`getPendingRemoteEvents()` |
| `remoteAccess` | `dsh-api-remote-access` | `decide/allows/snapshotFor/follow/registerPolicy/invalidate` |
| `sessions` / `sessionProjections` / `sessionQuery` | `dsh-session*` | 直接读事件/投影（绕过 Remote 层） |
| `agents` | `dsh-agent` | Agent 生命周期、`isOwnedBy()` 等 |
| `attachments` / `fileUploads` | `dsh-attachment*`、`dsh-client-file-upload` | 图片/文件上传与暂存凭证 |
| `permissionPresets` | `dsh-permission-presets` | 权限预设（read-only/workspace-write/danger-full-access）目录与切换 |
| `commands`（经 sessionController） | `dsh-commands` | slash 命令（`/compact`、`/permission`、`/plan`、`/goal`） |
| `jobController` / `terminalController` / `workspaceFiles` | api-job/terminal/workspace-files | 后台任务名册、终端、工作区文件（手机端次要功能） |

> 原型插件已用 `inject: ['sessionController']` 验证了注入链（`C:\Users\user\.dsh\plugins\dsh-remote-bridge\index.js` L298-302）。本次调研**未修改**该插件。

---

## 3. DSH 现有网络面

### 3.1 唯一 HTTP 载体：`dsh-host-webserver` `[已验证]`

- 一个 `node:http`（可选 `node:https`）服务；`register()` 具名 exact/prefix 路由，`registerUpgrade()` 精确 pathname 升级，`registerFallback` 唯一回退席位（dist）。
- 匹配顺序：exact → 最长 prefix → fallback；**不提供服务器级认证**（README「已知限制」），认证由 route owner（Connection）负责。
- `host` 拒绝通配地址；`port: 0` 由 OS 分配，`ctx.webServer.port` 可读；`getOrigin()` 给本机根 URL。
- `compression: gzip` 只压缩 socket-backed 响应，**SSE 不压缩**。

### 3.2 `dsh-client-connection`：`/api`、`/connect`、认证 `[已验证]`

核心常量（`dsh/node_modules/@deepseek-ai/dsh-client-connection/lib/index.js`）：

- L14 `API_PATH = "/api"`；L19 `CONNECT_PATH = "/connect"`；L276 `TOKEN_QUERY = "token"`。
- L440 `authenticatedUrl(baseUrl)`：把进程级 `launchToken` 加为唯一 query（`?token=…`）——**desktop host 就是用它把带 token 的 URL 交给 Electron**（desktop-host L605）。
- L458 `authorizeIndex(req,res,secure)`：**只在 `GET /`** 接受 `?token=`，校验通过后 303 → `./` 并写签名 cookie；否则 401。
- L505-539 `authorizeConnect(req,res,secure,publish)`：
  - 只接受 `GET /connect?token=<launchToken>&interactions=answer|none`（`interactions` 必须出现一次，缺省 `none`，重复或非法 → 400）
  - 成功：`302` + `location: ./api/remote.mux` + `set-cookie`（**API cookie**），**同步创建并发布一个 Peer**（`publish(interactions)` → `publishPeer`）
  - 失败：401
- L546-570 `authenticate/isAuthenticated/authenticateApi`：cookie = `{version:2, authority, id, issuedAt, expiresAt}`，HMAC 签名；`authority` 绑定 **hostname+port**（HTTP/HTTPS 不同 audience）；过期/早于签发/超出 maxAge 一律无效。
- L797-809 Host 服务方法 `authorizeConnect()` / `authorizeIndex()`：先过 `isTrusted()`，否则 403/401。
- L820-831 `createSharedFetchHandler`：exact Fetch route 只允许 **owner** Peer（非 owner → 403）；其余 `/api/<ns>/<method>` 交给 Gateway interceptor，未认领 → 404；**未认证 → 401，Host/Origin 不合法 → 403**（README「浏览器认证与请求信任」）。
- cookie 细节（README）：host-only、`Path=/`、`HttpOnly`、`SameSite=Strict`、`Secure` 仅在 TLS 监听器；`cookieMaxAgeDays` 默认 30 天；签名密钥存 `$DSH_HOME/.credentials.yaml` 的 `client-connection/browser-session` grant。
- API Peer 特性：`GET /connect` 的 API 连接默认 `interactions: 'none'`；`answer` 才可参与 waterfall（审批/提问）；重复关联返回 409；关闭生命周期 socket 后该 cookie 的 HTTP 被拒绝。**没有 logout 操作**（README 限制）。

### 3.3 一元 RPC 线格式 `[已验证]`

客户端实现：`dsh/node_modules/@deepseek-ai/dsh-client-connection/lib/client.node.js` L317-346

```
POST /api/<namespace>/<method>
content-type: application/json
{ "type": "client-request", "rpcId": "<uuid>", "method": "<method>", "payload": <JSON args> }

→ 200 application/json
{ "type": "server-response", "rpcId": "<same uuid>",
  "result": { "ok": true, "value": <JSON> } }
       或  { "ok": false, "error": { "code": "...", "message": "...", "details": {...} } }
```

- 二进制结果：`multipart/form-data`，`metadata` 部分是上面的 JSON 信封 + `attachments:[{codec:'bytes',part,path}]`（`client.node.js` L348-395）。
- 服务端会按 Typert 严格 codec **剥掉未声明字段**并校验（`gateway` README；本仓 `scripts/remote-contract.json` 里每个参数都有 `strict` JSON Schema，`additionalProperties:false`）。
- 错误码族：`gateway/*`（`bad-request`、`internal`、`cancelled`、`uplink-overflow`、`input-invalid`、`peer-disposed`…）、`session/*`、`remote-access/denied`。

### 3.4 WebSocket 流 mux `[已验证]`

- 路由常量：`dsh/node_modules/@deepseek-ai/dsh-api-gateway/lib/types/stream-protocol.js` L4 `REMOTE_STREAM_MUX_PATH = '/api/remote.mux'`（升级认证走 `connection.admit`，与 HTTP 同一 cookie）。
- 客户端 → Host 帧（`parseRemoteStreamClientMessage` L148-170）：
  `{type:'open', streamId, endpoint, payload}` / `{type:'item', streamId, value?}` / `{type:'end', streamId}` / `{type:'cancel', streamId}`
- Host → 客户端帧（`parseRemoteStreamServerMessage` L176-198）：
  `{type:'item', streamId, value?}` / `{type:'end', streamId}` / `{type:'error', streamId, error:{code,message,details}}`
- 心跳：Host 每 `websocketHeartbeatIntervalMs`（默认 2000 ms）发 Ping；连续两个周期无活动断链；默认 `permessage-deflate`，≥1024 字符的 JSON 帧压缩（gateway README）。
- 逻辑流不跨载体代际重放；上行缓冲上限 `streamInboxBytes`（默认 262 144）。

### 3.5 `$events`：Host 事件广播 + 审批 waterfall `[已验证]`

- 常量：`stream-protocol.js` L6 `REMOTE_EVENT_STREAM_ENDPOINT = '$events'`、L8 `'$events/result'`、L10 `payload = { args: {} }`。
- 打开后 Host 先发唯一 ready：`{ type:'ready', clientId, host:{ home } }`（gateway README + `lib/types/client/remote-events.js`）；`clientId` 用于后续应答。
- 之后两类帧：
  - `emit`：`{ type:'emit', event, args }`（`gateway/lib/index.js` L1068-1092；非 owner 还要过 `remoteAccess.decide({action:'event:<name>'})`）
  - `waterfall`：`{ type:'waterfall', event, eventId, agentId, request }`（L1117-1145）
- 应答 waterfall：`POST /api/$events/result`，payload `{ clientId, eventId, outcome }`（§2.3）。
- **只有 `interactions:'answer'` 的 Peer 才会收到 `waterfall`**；`$events/result` 也要求 `peer.interactions === 'answer'`（gateway README）。这决定手机端配对时必须 `GET /connect?interactions=answer`。

转发白名单全集（`dsh-api-remotes/lib/index.js` L21-147）：

```
emit:      agent-preset/selected, api-session/{activity,added,error,removed,status}, commands/change,
           deepseek-account/{session-expired,model-sign-in-required}, credentials/{record-updated,reference-updated},
           goal/activation-changed, cordis/{request-run,request-run-resolved,dynamic-package,dynamic-retract,
           inspect-query,inspect-query-resolved}, llm/{adapters-updated,models-updated},
           permission-presets/catalog-changed, plugin-manager/{changed,install-log,install-state,update-state},
           plugin-market/changed, settings/document-updated, schedule/changed
waterfall: approval/request, user-questions/request
```

（`api-session/*` 五条带 `resources: {session: …}`，非 owner 逐事件按 Session 授权过滤。）

### 3.6 访问控制模型 `[已验证]`

`dsh/node_modules/@deepseek-ai/dsh-api-remote-access/README.zh.md`：

- owner（`{kind:'owner'}`）恒放行；非 owner 走 `policy` → `role{name,resource,actions}` + `binding{role,resource,condition?}`。
- 资源种类：`global` / `session` / `workspace`（`TypertResourceMap`）；一次调用访问多个资源时**每个**都要被放行。
- `decide({principal, action, resources, args})`；拒绝 → `RemoteError('remote-access/denied', details:{action,reason})`，reason ∈ `no-allow|policy-failed|resource-invalid|condition 原因`。
- 列表方法逐项 `allows()` 过滤（`sessionController.list` L373、`search` L385、`control` L673）。
- **对手机端**：走 `/connect` 得到的是 owner Peer，因此不会被策略拦截；如果未来要“只读分享/熟人设备”，要么用 liveshare（见 §3.7），要么在 Gateway 前自己加 principal —— 而 `registerPolicy(kind, policy)` 是公开扩展点。

### 3.7 Liveshare（官方的“跨机只读/受限写入”通道）`[已验证]`

`dsh/node_modules/@deepseek-ai/dsh-experimental-liveshare/README.zh.md`：

- 组合：`owner` 侧插件 + 独立部署的 Liveshare 服务端；viewer 页面 = owner 自己的 web shell + `viewer-carrier.ts` 生成的载体脚本，把 Connection 调用与 Gateway 流转成 `POST {base}/__share/rpc`。
- 权限：`share-viewer` principal，按 share 档位 `view`/`write` + 逐会话 grant；只读档下 viewer 的轮次仅能用 read/read_image/glob/grep/present、todo_write/skill/web_search/web_fetch。
- **未启用**：默认 profile 不含它，`dsh.plugin: true` 都没有；要在 `cordis.patch.yml` 插两条（README「默认可见性与启用」）。
- 对 Android 的启发性：它证明了“非浏览器客户端 = 实现 Connection 协议子集”的可行性；但它把 UI 固定成 owner 的 web 页面（WebView 方案），不适合原生 Avalonia 客户端直接复用。

---

## 4. 事件模型（归一化契约的原料）`[已验证]`

### 4.1 事件信封

`dsh/node_modules/@deepseek-ai/dsh-session/lib/types/index.js` L128-169（`assertSessionEventEnvelope`）：

```
SessionEvent = { type: string, seq: number(int>=0), time: number(ms epoch), data: any,
                 surfaceOp?: 'append' | {op:'replace',startSeq,endSeq},
                 sourceEventSeqs?: number[], ignorable?: true }
```
只有这 7 个键合法；`seq` 是**日志内连续序号**（gap = 协议错误：`session event stream skipped seq N`）。

### 4.2 事件类型全集（61 个，`KNOWN_SESSION_EVENT_TYPES`）`[已验证]`

来源：`dsh/node_modules/@deepseek-ai/dsh-session/lib/types/known-event-types.js`

```
agent-preset/selected, agent/inbox/spliced, approval/asked, approval/decided, approval/policy,
assistant/attempt, assistant/message, command/done, command/run, compaction/{end,prune,start,summary},
deliverables/presented, developer/message, feedback/{message-delete,message-put,record}, goal/change,
hook/{invoked,result}, image/offload, job/{registered,settled}, llm/{retry,retry-started},
model/selection, permission/preset, plan/mode, request/{context,header}, sandbox/mode, schedule/change,
session-log-deepseek/delivery-accepted, session/end-seed, session/{title,title-llm-request},
step/{end,start}, subagent/{catalog,descriptor,model-selection-policy}, system/message,
team/member, team/message/{delivered,queued}, team/task, todo/write,
tool-workflow/{agent-end,agent-start,run-end,run-start}, tool/call, tool/{ptc-dispatch,ptc-dispatch-start},
tool/result, turn/{end,start}, user/message, web/deepseek-search-llm-request,
working-directory/change, workspace/changes
```
- 只有 `system/developer/user/assistant/message` 与 `tool/result` 是 **surface 事件**（模型可见），必须带 `surfaceOp`；其余是日志/追踪事件。
- 未知类型：只有显式 `ignorable:true` 的插件记录可被旧版本跳过，否则读路径拒绝该日志（跨版本安全性设计）。

### 4.3 关键事件的 `data` 形状（由代码/schema 归纳）

| 事件 | data | 证据 |
|---|---|---|
| `user/message` | `{ content: ContentBlock[], source:{kind:'user', rpcId?, clientTimeZone?, principal?} }`（即 message 本体） | `surface.js` L85-87；session-controller `prompt` L547-554 |
| `assistant/message` | `{ message:{role:'assistant',content:[…]}, stream:[…], turn, step, … }` | `surface.js` L93-97；`session/types/index.js` L209-217（turn/step/stream 必填） |
| `tool/result` | `{ message:{role:'tool',…}, … }` | `surface.js` L98-100 |
| `tool/call` | 工具调用记录（含 `toolName`/`callId` 语义；surface 之外） | `known-event-types.js`；`approval/asked` 用 `callId` 关联 |
| `approval/asked` / `decided` | `{ id, toolName, callId?, reason? }` / `{ id, outcome }` | `user-approval/lib/types/index.js` L145-152 |
| `turn/start` / `turn/end` | 轮次边界（run started/finished 的最佳锚点） | `known-event-types`；`approvalStateDefinition` L39-42 |
| `step/start` / `step/end` | 单步（一次模型请求）边界 | 同上 |
| `model/selection` / `permission/preset` / `plan/mode` / `sandbox/mode` | 会话级开关变更 | 同上 |

`ContentBlock`（`dsh-llm`）：`{type:'text',text}`、`{type:'image',mediaType,data|ref}`、`{type:'tool-call',name,arguments,…}` 等 —— 原型 `textOf()/toolNamesOf()` 就是按 `data.message.content[].type` 提取的，已真机验证。

### 4.4 三种读取/订阅形态（对应 Remote 端点）

| 端点 | 形态 | 用途 |
|---|---|---|
| `session/events`（= `session/page`） | 一元，cold-safe | 历史分页：`{cursor, records, hasMore}`，`records[i] = {type:'event', event}` |
| `session/follow` | stream | 首帧 snapshot（header+projections+第一页）+ 增量事件；Web/desktop 主用 |
| `session/observeEvent` | stream | 只要增量、不要快照（`ready` 后逐事件） |
| `session/state` / `observeState` / `wait` | 一元/流 | 运行态：`{cursor,active,running,queue,pendingInput:['approval'|'question'|'other']}`；`wait` 还能带回有界 assistant 输出 |
| `session/control` | stream | 全进程 projection baseline + 逐会话增量（会话列表/标题/队列） |
| `$events` | WS 逻辑流 | 进程级广播（含 `api-session/*` 与审批 waterfall） |

### 4.5 给“归一化契约”的映射建议（设计输入）`[推测/建议]`

| 契约事件 | DSH 来源 |
|---|---|
| `run.started` / `run.finished` | `turn/start` / `turn/end`（或 `api-session/status(running)` 做快速态） |
| `message.delta` | `assistant/attempt` / `assistant/message.stream`（进程内、client-only，重连不重放）→ 建议只用 `assistant/message` 的最终文本，delta 作为可选优化 |
| `message.final` | `assistant/message`（surfaceOp=append） |
| `tool.call` / `tool.result` | `tool/call` + `tool/result`（用 `callId` 配对） |
| `approval.request` / `approval.resolved` | `$events` waterfall `approval/request` / 应答结果；审计用 `approval/asked`+`approval/decided` |
| `question.request` | `$events` waterfall `user-questions/request` |
| `queue.changed` | `session/state.queue` + `session/control` projection |
| `session.updated` | `api-session/{added,removed,status,error,activity}` + `session/control` |
| `todos` | `todo/write`（或 projection） |
| `turn.outline` | `session-turn-outline` 投影 key（`turnOutline`） |

---

## 5. 契约可自动化（重要工程结论）`[已验证]`

每个参与 Remote 的包都带一个**生成的** `lib/typert.host.js`：

- 头部：`/* Generated by @deepseek-ai/dsh-typert-generator from FaceModel — do not edit. */`
- 中段：zod schema 工厂，逐字段带 `.describe()`（例如 `session/prompt` 的参数、`session/list` 的结果）
- 尾部：`const $remote = createTypertRemoteInfo({ package, methods: { '<ns>/<method>': { service, description, mode, parameters:[{name, source, codec:{jsonSchema}, strictCodec:{jsonSchema}}], result:{jsonSchema}, sourceLocation } }, events:[{name, mode, signature}] })`
  （session-controller：`lib/typert.host.js` L1832 `createTypertRemoteInfo`、L2133 `'session/list'`、L2052 `'session/follow'`、L2181 `'session/observeEvent'`）

本仓 `scripts/dump-remote-contract.mjs` 用极简 JS 对象字面量解析器把这段纯数据读出来（不执行模块、不引依赖），输出：

- `34` 个 namespace、`193` 个端点，含每个参数的 **strict JSON Schema**（`additionalProperties:false`）与结果 schema；
- 同文件里 `events` 数组给出 `api-session/*` 等事件的 **TS 签名**（如 `'api-session/status'(sessionId: SessionId, running: boolean): void`）。

➜ **Android 客户端不必手抄协议**：用 `scripts/remote-contract.json` 生成 Kotlin/C# DTO 与端点常量；缺失的 stream 结果 schema（`jsonSchemaError` 的少数端点，如 `session/follow`、`session/list`、`session/control`）由 zod 工厂源码 + §4 的帧描述补齐。

`session/prompt` 的 strict 请求 schema（原样摘自产物）：

```json
{"type":"object","properties":{
  "requestId":{"type":"string","description":"Client-minted identity persisted on the exact accepted user message."},
  "sessionId":{"type":"string"},
  "mode":{"type":"string","enum":["queue","steer"]},
  "content":{"type":"array","items":{"oneOf":[
     {"type":"object","properties":{"type":{"const":"text"},"text":{"type":"string"}},"required":["type","text"]},
     {"type":"object","properties":{"type":{"const":"image"},"mediaType":{"enum":["image/png","image/jpeg","image/webp","image/gif"]},"data":{"type":"string"},"name":{"type":"string"}},"required":["type","mediaType","data"]},
     {"type":"object","properties":{"type":{"const":"file"},"receiptId":{"type":"string"}},"required":["type","receiptId"]}]},
  "clientTimeZone":{"type":"string"}},
 "required":["requestId","sessionId","mode","content"],"additionalProperties":false}
```
── 与原型 `/api/prompt` 实际调用完全一致（`C:\Users\user\.dsh\plugins\dsh-remote-bridge\index.js` L171-177），说明原型结论正确。

---

## 6. 插件加载机制 `[已验证]`

### 6.1 profile 与 patch 层

- profile 目录：`$DSH_HOME/profiles/<name>`，由**可安装组合包（bundle）** + 自己的 `cordis.patch.yml` 组成；bundle 的 `dsh.bundle.patch` 声明一个或多个 patch 文件，按序拼接（`dsh-app-boot` README L50）。
- patch 层顺序：bundle 层 → profile 级 user patch → home 级 user patch（后者优先）。
- patch 条目能力（`dsh-app-boot` README L63-67）：
  - 按 `id` 覆盖某行的**整个 config**（不是深合并）
  - `insert:` 列表插入新行
  - `!!js` 表达式在启动时求值（`web-app/cordis.patch.yml` L179/L180/L262 大量使用）
  - 插入行的 `name` 可以是**绝对路径、file URL、包标识符**；相对 `./`/`../` 按 patch 文件位置转成 file URL
  - patch 指向不存在的条目 → stderr 警告；空文件/纯注释 → 启动失败（要禁用该层请写 `[]`）

### 6.2 本机实例的插件是怎么挂进去的（可复制的做法）

`C:\Users\user\.dsh\profiles\desktop\cordis.patch.yml` L186-193：

```yaml
- insert:
    - id: remote-bridge
      name: dsh-remote-bridge
      config: { host: 127.0.0.1, port: 18082, token: 0123456789abcdef01234567 }
```

名字能解析成包，是因为 profile 有自己的 `node_modules`：`C:\Users\user\.dsh\profiles\desktop\node_modules\dsh-remote-bridge` 是 **junction → `C:\Users\user\.dsh\plugins\dsh-remote-bridge`**（实测 `Get-Item ... LinkType=Junction`），插件的 `package.json` 声明 `"dsh": {"plugin": true}`、`"main": "index.js"`。

> `dsh.plugin: true` 标记的**具体消费者**在本次调研中未定位（plugin-manager / plugin-detail 源码里没有该字段的读取点）→ `[推测]` 它是插件市场/清单识别用的展示标记，Loader 本身只认 Cordis 插件形状（`name`/`inject`/`apply` 或 `Config`）。可行的替代写法：`name: C:\Users\user\.dsh\plugins\dsh-remote-bridge\index.js`（绝对路径）或 `name: ./plugins/...`（相对 patch）。

### 6.3 HMR / 热加载触发条件

`dsh/node_modules/@deepseek-ai/dsh-hmr/README.zh.md`：

- 启动器提供 `profileContext` 时，`base` 组合包以 `root: []` 启用 HMR；`headless`/`sdk`/`acp` 默认禁用。
- HMR 监听：**profile manifest（`package.json` 的 `dsh.profile.bundles` 变化）** + **两份 user patch（profile 级与 home 级 `cordis.patch.yml`）**；初始化时注册、应用就绪后处理。
- profile 级重组还会替换 **profile 目录下磁盘上已变化的已加载文件**（包括 `node_modules` 下的已安装包与链接包的目标）→ 链接插件 `index.js` 改动会触发热替换（原型里的 `// hmr tick 212403` 与 patch 里的 `# reload tick …` 注释即此用途）。
- 未启用/不存在 HMR 时改动要重启；`ctx.hmr` 服务与 `hmr/change`、`hmr/reload` 事件保留。
- 重载失败策略、`recomposeProfile()`/`reconcileProfilePatches()`、`app-boot/config-reload` 见 app-boot README L96-98。

### 6.4 配置 schema 与版本/兼容性

- **Config schema：支持**。插件用 `static Config = z.object({...})`（`@deepseek-ai/schemastery` 或 zod）声明；Loader 读取时 `Config(config)` 归一化默认值（`sessionController` L240-243、`ApprovalService` L68-70）。
- **版本/兼容检测**：
  - bundle 与插件可用 `engines.dsh` 或 DSH peerDependency 声明运行版本要求；不满足 → 安装前拒绝；启动时 bundle 被跳过并列入 `skippedBundles`（app-boot L54）。
  - 精确版本豁免存 profile 的 `compatibility.json`（`package@version → [dsh runtime versions]`），默认不继承。
  - **运行时没有统一的“DSH 版本”服务可注入** → `[推测]` 网关插件要自报契约版本时，应自行读 `@deepseek-ai/dsh/package.json` 的 `version`（profile runtime resolution 会把安装包映射进 profile 解析域，app-boot L140），并在自家 `/meta` 端点上声明 `contractVersion`。
  - 事件层兼容靠 `ignorable` 标记 + `KNOWN_SESSION_EVENT_TYPES` 拒绝未知必需事件（§4.2）。

---

## 7. 两条路线对比与选型建议

### 7.1 对比

| 维度 | A. 复用自带 Web/Remote 服务 | B. 自建网关插件（现状原型） |
|---|---|---|
| 传输 | HTTP `POST /api/<ns>/<method>` + WS `/api/remote.mux`（已存在、已鉴权、已压缩、有心跳） | 自定义 HTTP + SSE（`/api/*`），自己实现 |
| 鉴权 | `?token=` → 签名 cookie / `/connect` 换 owner API cookie（官方语义，authority 绑定、30 天、可撤销） | 自定义 Bearer token（明文本地配置） |
| 事件 | `$events` + `session/follow` + `session/control`，含**审批/提问 waterfall**、断线补洞语义 | SSE 自解析 `observeEvent`，审批完全缺失 |
| 契约 | 生成式 Typert 描述符（193 端点 + JSON Schema），可自动生成 DTO | 手写，随版本漂移 |
| 写操作 | `session/prompt`（含 queue/steer）、`cancel`、`updateQueue`、`selectModel`、`fork` | 只有 prompt(queue/steer)、history 读 |
| 版本兼容 | 与 Web GUI 同生共死：DSH 升级即协议升级，需要客户端跟随 | 插件与 DSH 解耦，但依赖的内部结构可能变 |
| 复杂度 | 高：需实现认证、RPC 信封、WS mux 分帧、strict codec | 低：现状 300 行即可跑通 |
| 与官方 UI 冲突 | 无（owner Peer 可多个；GUI 与手机并存） | 无（但两套事实来源，语义重复） |
| 暴露面 | `dsh web --host` 需具体 IP + `--trusted-host`；通配地址被拒；非 TLS 为明文 | 自己 bind，任何地址（风险自担） |

### 7.2 建议：**A 为长期主干，B 降级为“契约试验台 + 逃生舱”**

推荐的最终形态（分两层，互不阻塞）：

1. **底座：官方 Remote 协议**
   - 配对：手机首次连 `POST /connect?token=…&interactions=answer`（token 由 DSH 启动 URL/二维码提供），拿 API cookie；此后所有请求复用 cookie。`[已验证该流程存在]`
   - 会话：`session/list`（列表/状态）→ `session/follow`（历史+增量）或 `session/events`（分页）→ `session/prompt`/`cancel`/`session/state`。
   - 交互：WS mux 打开 `$events`，用 `clientId` 应答 `approval/request` 与 `user-questions/request`（`$events/result`）。
   - 契约：用 `scripts/remote-contract.json` 生成 DTO；客户端内置 `contractVersion`，Host 侧版本不匹配时降级到只读。
2. **薄插件（可选但建议保留 DSH 侧一个）**
   - 不做业务转发，只做三件事：**配对入口**（把 `authenticatedUrl()`/二维码/局域网地址暴露给手机）、**契约版本上报**（`/meta`：dsh version、contractVersion、能力位）、**归一化补充**（例如把 `assistant/message.stream` 聚合成手机友好的 delta，或把 `todo/write` 等投影补齐）。
   - 实现成本低：`inject: ['sessionController','webServer','connection']`，复用 `ctx.webServer.register()` 挂自省端点。
3. **B（自建网关）继续保留**：作为“协议未覆盖能力”的逃生舱与真机联调试验台；不建议把它扩成主交付，否则要重复实现审批、重连补洞、strict codec、鉴权撤销等一整套官方已有语义。

**若最终选择纯 B**（自建网关为唯一通道），最小必备补齐项：审批 waterfall 接入 + 事件 seq 连续性/缺口修复 + 客户端契约版本 + 鉴权撤销与配对过期。否则功能与安全都会落后于 A。

---

## 8. 风险清单

| # | 风险 | 影响 | 证据/触发条件 | 缓解 |
|---|---|---|---|---|
| R1 | **协议是预发布、无兼容承诺** | 客户端随 DSH 升级而破 | 版本 `0.2.2-rc.1`；SDK 协议 README 明写“无协议版本协商、无兼容承诺” | 客户端显式 `contractVersion` + 失败即只读降级；锁 DSH 版本 |
| R2 | **desktop 端端口随机（`--port 0`）** | 手机无法写死地址 | `desktop-host/lib/index.js` L449-455 | 配对接口/二维码/mDNS；由薄插件上报 `webServer.port` |
| R3 | **非 loopback 暴露需 `--host <具体IP>` + `--trusted-host`，通配地址被拒** | 直接 `0.0.0.0` 不可用 | `web-app` README L65-67；`host-webserver` README | 反向代理/隧道 + `--public-url`；或 WireGuard（原型已验证 18082 走隧道） |
| R4 | **明文 HTTP 会暴露 bearer cookie** | token/cookie 泄露 | `client-connection` README 限制 | TLS（`--tls-cert/--tls-key`）或 VPN 隧道 |
| R5 | **审批 fail-closed 与策略耦合** | 手机上点“允许”也可能没用 | `policy==='never'` 在分发前拒绝（approval L191-192）；本机当前即 `danger-full-access → never` | 客户端显示当前策略；提示切换权限预设；不要承诺“总能批准” |
| R6 | **approval 要求 turn 未结束** | 空闲期无法预置审批 | approval L139-143 | 只在事件到达时弹窗；超时按 `cancelled` 处理 |
| R7 | **单文件/多客户端语义**：API Peer 关闭 socket 后 cookie HTTP 被拒；无 logout | 断线后需重新 `/connect` | `client-connection` README 限制 | 客户端持久化 cookie + 失败重跑 `/connect`；提供“重新配对” |
| R8 | **stream 不重放**（`$events` 单向通知、`assistant` delta 不重放） | 断线期间丢通知 | gateway README 限制；api-remotes README L79 | 重连后重新读 `session/list` + `session/follow`（按 seq 补洞），不要把 delta 当可靠来源 |
| R9 | **strict codec 会剥字段** | 多传字段被静默丢弃/校验失败 | `additionalProperties:false`；gateway README | DTO 由 `remote-contract.json` 生成，不手写 |
| R10 | **事件类型集合随构建变化**（61 个 type 是“本构建理解的语言”） | 新事件被旧客户端误判 | `known-event-types.js` 注释 | 归一化层对未知 `type` 做直通/忽略，不崩溃 |
| R11 | **内部件（`historyController` 等）不是稳定 API** | 原型式直接取内部件易碎 | 运行时自省可见，但源码在 `lib/types/*`（内部实现目录） | 只走 `sessionController` 公开方法或 Remote 端点 |
| R12 | **liveshare 路径依赖独立服务端** | 只想“跨机只读”也要部署 | liveshare README | 不作为 v1 依赖 |
| R13 | **权限模型只管“能不能调”，不管“能不能看”** | 手机 owner 能看到全部会话 | remote-access README（owner 恒放行） | 若要多用户/只读，需 `registerPolicy('kind')` 或 liveshare |

---

## 9. 未决问题（需要下一轮验证）

1. **API Peer 的 cookie 生命周期**：`/connect` 创建的 Peer 在 WS 断开后“HTTP 被拒绝”——是 cookie 失效还是 Peer 已释放？需真机验证“断开 WS 后 `POST /api/session/list` 的返回码”。`[推测]` 当前实现是 Peer 释放即拒（`removeRemoteEventClient`/`publishPeer` 生命周期），cookie 本身仍可能签名有效。
2. **`assistant/message.stream` 的 delta 格式**：可用于流式打字机效果，但需实测帧形状与重连语义（`lib/types/history/assistant-stream.js` 未逐行读）。
3. **多 answer Peer 的 UX 影响**：审批/提问对**所有** `interactions:'answer'` 的 Peer 广播，先答者胜、其余收 `{type:'cancel',eventId}` 帧（已由 gateway `finishRemoteEvent` L1175-1186 证实）。需要实测“桌面弹窗 + 手机弹窗同时出现、桌面先点”的真实观感与是否出现 UI 竞态。
4. **`dsh.plugin: true` 的真实消费者**（plugin-manager / plugin-market？）以及 profile 里 `node_modules/<pkg>` junction 是手工创建还是 `dsh plugin add` 产生（影响发布与安装流程）。
5. **WebSocket 经 `--public-url` 反向代理**：mux 升级是否要求 `Origin`/`Host` 与 trustedHosts 完全一致，代理后是否必须用 `--trusted-host` 显式点名（信任逻辑已读，未在代理拓扑下实测）。
6. **同 `$DSH_HOME` 双实例**：`dsh --profile web` 对外 + desktop 同时运行时的会话写锁冲突（`session/writer-held`）与事件可见性，需实测。
7. **原生客户端 carrier 细节**：multipart 二进制结果、`permessage-deflate` 与 `ws` 的握手兼容性（Android OkHttp 支持，需真机验证）。

---

## 附录 A. 证据索引（asar 内路径 → 关键点）

| asar 内路径 | 关键点 |
|---|---|
| `dsh/node_modules/@deepseek-ai/dsh-api-session-controller/lib/index.js` | `ctx.sessionController` 类：L225 inject、L240 Config、L264 服务键、L371-668 全方法（含 JSDoc 契约） |
| `.../dsh-api-session-controller/lib/types/history/controller.js` | `events` L75、`observeEvent` L95（ready→event）、`projections` L116、`observeProjections` L160、`assistantOutput` L184 |
| `.../dsh-api-session-controller/lib/types/history/listener.js` | `session/event` 监听 L25-32、事件帧 L30、`entryFor` L68、seq 缺口检测 L96-117 |
| `.../dsh-api-session-controller/lib/types/web/controller.js` | `follow` opening snapshot 形状 L97-128 |
| `.../dsh-api-session-controller/lib/typert.host.js` | L1832 `createTypertRemoteInfo`、L2052 follow、L2133 list、L2181 observeEvent（含 JSON Schema） |
| `dsh/node_modules/@deepseek-ai/dsh-user-approval/lib/types/index.js` | L65-75 `ApprovalService`+`approval` key、L137-154 `request()`、L182-219 `decide()` waterfall、L54-118 策略写入 |
| `dsh/node_modules/@deepseek-ai/dsh-api-remotes/lib/index.js` | L21-147 转发白名单（`approval/request`、`user-questions/request` 为 waterfall）、L241-258 waterfall 桥 |
| `dsh/node_modules/@deepseek-ai/dsh-api-gateway/lib/types/stream-protocol.js` | L4 `/api/remote.mux`、L6 `$events`、L8 `$events/result`、L18-54 result 解析、L61-86 请求投影、L148-198 双向帧 |
| `dsh/node_modules/@deepseek-ai/dsh-api-gateway/lib/index.js` | L1068-1092 emit 广播、L1093-1145 waterfall 帧、L1146-1156 result 结算 |
| `dsh/node_modules/@deepseek-ai/dsh-client-connection/lib/index.js` | L14 `/api`、L19 `/connect`、L276 `token`、L440 `authenticatedUrl`、L458 `authorizeIndex`、L505-539 `authorizeConnect`、L546-570 cookie 校验、L820-831 FetchHandler/owner-only |
| `.../dsh-client-connection/lib/client.node.js` | L317-346 一元 RPC 信封、L348-395 multipart 二进制 |
| `dsh/node_modules/@deepseek-ai/dsh-session/lib/types/known-event-types.js` | 61 个事件类型全集 + `ignorable` 兼容机制说明 |
| `.../dsh-session/lib/types/index.js` | L128-169 事件信封 7 键、L209-217 assistant settlement 必填字段 |
| `.../dsh-session/lib/types/surface.js` | L13-19 surface 事件集、L75-106 `deriveEventMessage` |
| `dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/index.js` | L427-465 `--port 0` 启动、L605 `authenticatedUrl` 交给 Electron |
| `dsh/node_modules/@deepseek-ai/dsh-web-app/cordis.patch.yml` | L175-184 webserver、L193-200 web-runtime、L230-237 connection、L244-245 api-remotes |
| `dsh/node_modules/@deepseek-ai/dsh-base/cordis.patch.yml` | L46-58 typert/gateway/remote-access、L259-262 approval 策略、L264-276 权限预设 |
| `dsh/node_modules/@deepseek-ai/{dsh-api-gateway,dsh-api-remote-access,dsh-user-approval,dsh-sdk-protocol,dsh-sdk-jsonrpc-server,dsh-experimental-liveshare,dsh-hmr,dsh-app-boot,dsh-plugin-manager}/README.zh.md` | 语义与限制（本机无 `.d.ts`，README 是最接近规范的说明） |

**运行时证据**
- `netstat`：`127.0.0.1:54306` ← PID 29432（`dsh-desktop-host/lib/index.js` 进程）。
- `GET http://127.0.0.1:18082/api/debug?token=…`（原型插件只读端点）：`sessionController` 方法名全集、`historyController` 方法名、一个真实 `SessionSummary`（字段名与 `projections` 形状）。
- `C:\Users\user\.dsh\plugins\dsh-remote-bridge\bridge.log`：Android Chrome UA 经 adb reverse 访问记录（真机验证痕迹）；`cordis.patch.yml` 里的 `# reload tick …` 与插件内 `// hmr tick …` 证明 patch/模块热加载可用。

## 附录 B. 复现命令

```powershell
cd D:\Code\VibeCoding\DSHRemote
# 1) asar 结构
node scripts/asar-extract.mjs stat
node scripts/asar-extract.mjs list --grep "dsh-api-session-controller/lib" --limit 30
# 2) 抽单个文件（不回写 DSH 目录）
node scripts/asar-extract.mjs read dsh/node_modules/@deepseek-ai/dsh-user-approval/lib/types/index.js --out scripts\_extract\approval_types.js
# 3) 全量 Remote 契约（193 端点 / 34 namespace）
node scripts/dump-remote-contract.mjs --out scripts/remote-contract.json
node scripts/dump-remote-contract.mjs --summary
# 4) 只读核对运行时注入面（原型插件已在本机 18082 运行）
Invoke-WebRequest "http://127.0.0.1:18082/api/debug?token=0123456789abcdef01234567" -UseBasicParsing
```

> 本调研**未修改** DSH 安装目录、未修改原型插件；新增文件仅 `docs/research/dsh-internals.md`、`scripts/asar-extract.mjs`、`scripts/dump-remote-contract.mjs`、`scripts/remote-contract.json`。
