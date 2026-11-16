# DSH 手机远程：整体方案（v1，已确认路线）

> 状态：路线已由用户确认（2026-11-08）：
> 1. **直接采用 DSH 官方 Remote 协议**（不自建 `/v1` 业务契约）；
> 2. **审批放 M2**，M0 先交付「看历史/流式 + 发消息 + 停止」的最小可用 APK；
> 3. **首版即自签 TLS + 指纹 pinning**。
>
> 依据：[`docs/research/dsh-internals.md`](research/dsh-internals.md)（610 行）与 [`docs/research/arc-android.md`](research/arc-android.md)（360 行）。
> 工作区：`D:\Code\VibeCoding\DSHRemote`。

---

## 0. 一句话方案

**DSH 侧只装一个「配对 + TLS 中继」薄插件（`dsh-remote-relay`）：把桌面进程内 loopback 的官方 Remote 服务（`/connect` + `/api` + `/api/remote.mux`）以 TLS 暴露到局域网并给出配对信息；手机端是独立的 Avalonia App（复用 Arc Agent 组件），直接作为「官方协议的另一个客户端」连接，客户端 Core 把 DSH 的 `SessionEvent` 映射成 `ArcAgentEvent`。**

三个要点：

1. **不发明协议**：手机说 DSH 官方 wire（`/connect` 换 owner API cookie、`POST /api/<ns>/<method>`、WS `/api/remote.mux` 逻辑流、`$events` waterfall），与桌面 Electron UI 是**并列的两个 owner 客户端**。
2. **DSH 侧不写业务逻辑**：插件只做三件事——拿到 `ctx.webServer.port` 与 `ctx.connection.authenticatedUrl()` 的 launch token、做 TLS 终止与 HTTP/WS 反向代理（Host 重写为 `127.0.0.1:<port>`）、输出配对 URI/二维码。
3. **语义映射放客户端（C#）**：`SessionEvent → ArcAgentEvent` 只在 `DshRemote.Core` 一处实现；网关/中继完全不碰事件语义。

---

## 1. 背景与目标

### 1.1 已经发生的事（来自对话）

- 原型 `~/.dsh/plugins/dsh-remote-bridge`：宿主插件 + 7KB 页面，自建 4 个 HTTP 接口，**真机（TestDevice / Android 16）全链路验证通过**：手机打开页面 → 拉会话 → SSE 收事件 → 发消息进桌面当前会话 → Agent 继续执行。
- 真机暴露的问题：会话下拉不可辨识、无历史且 30s 清屏、只渲染纯文本、无工具卡片/审批、token 明文、手搓页面依赖内部服务。
- 上一轮已确定方向：**客户端做重，独立 APP（Avalonia + Arc.Avalonia）**。

### 1.2 本轮目标

1. 可持续开发的仓库：DSH 薄插件 + .NET 客户端 + 协议子集文档 + 测试 + 安装/构建脚本。
2. Android APK（同一份 XAML 在桌面壳也能跑），复用 Arc Agent 面板，达到「手机上能远程干活」的可用度。
3. 安全基线：自签 TLS + 指纹 pinning + 配对 URI。
4. 里程碑 M0 即可真机演示：列会话、看实时输出、发消息、停止。

### 1.3 非目标（首版）

- iOS（本机无 `Avalonia.iOS`，需 Mac 构建）。
- 复刻桌面 GUI 全部功能（设置、插件市场、文件管理器、终端等）。
- 公网暴露/自建中转；外网走 WireGuard/Tailscale。
- 只读分享（那是官方 experimental liveshare 的领域）。
- 自建 `/v1` 契约（用户已否决；保留为未来逃生舱的备选，不实现）。

---

## 2. 现状盘点（本轮调研结论）

### 2.1 DSH 侧

| 能力 | 结论 | 对方案的意义 |
|---|---|---|
| 官方 Remote 协议 | 桌面 runtime 内有 `dsh-host-webserver`（`127.0.0.1:随机端口`，本机 54306）+ `dsh-client-connection`：`GET /connect?token=&interactions=answer|none` → **302 + 签名 API cookie + 发布 owner Peer**；`POST /api/<ns>/<method>` JSON-RPC 信封；WS `/api/remote.mux` 逻辑流；`$events` 逻辑流承载进程级广播与 **审批/提问 waterfall** | **这就是手机要讲的协议**；193 端点/34 namespace 的 strict JSON Schema 已导出（`scripts/remote-contract.json`） |
| 认证 | 进程级随机 `launchToken`（`ctx.connection.authenticatedUrl()` 可拿到带 token 的 URL）；cookie 为 HMAC 签名、authority 绑定 `host:port`、默认 30 天、`HttpOnly/SameSite=Strict`；API Peer 绑定 WS socket 生命周期 | 中继插件负责把 token 交给配对端；手机端持久化 cookie，WS 断开后重新 `/connect` |
| 桌面绑定 | desktop-host 代码写死 `--host 127.0.0.1 --port 0`，Electron renderer 依赖该 loopback URL；改绑 LAN 会让桌面 UI 失联 | **手机不能直连**，必须由插件做中继（这是插件存在的第一理由） |
| 会话/事件 | `sessionController`：`list / events(page) / observeEvent / follow / state / wait / cancel / updateQueue / selectModel / fork…`；`SessionEvent` 信封 7 键、本构建 61 种事件；`turn/start`、`step/*`、`user/message`、`assistant/message`、`tool/call`、`tool/result`、`approval/asked`、`approval/decided` | 手机端只实现需要的子集；事件透传后由 C# 映射 |
| 审批/问答 | 官方 `$events` waterfall：`approval/request` 与 `user-questions/request` 投递给每个 `interactions:'answer'` 的 Peer，**先答者胜，其余收到 `cancel`**；应答 `POST /api/$events/result` | M2 直接复用；不做 host 侧应答者，不与桌面 UI 抢 waterfall |
| 事件投递语义 | `$events` 单向通知不重放；`assistant` 增量不重放；重连后应重新 `session/list` + `session/follow`（按 seq 补洞） | 客户端断线策略：重连 → 重新 /connect → follow 快照覆盖 + 事件补洞 |
| 版本风险 | DSH 0.2.2-rc.1；SDK 协议 README 明写「无协议版本协商、无兼容承诺」；strict codec 会剥未声明字段 | 客户端内置 `contractVersion` + 失败降级只读；锁 DSH 版本测试 |
| Web 另起 | `dsh --profile web --host <IP> --tls-cert` 可对外提供完整官方 GUI，但是**独立 runtime**，与桌面会话争写锁 | 不采用；但可作为「只想用浏览器应急」的备选 |

### 2.2 手机端（已验证）

| 项 | 结论 |
|---|---|
| 工具链 | .NET SDK 10.0.114；Android workload 36.1.118；JDK 21；可用 SDK **必须是 `D:\SmartBillAndroidSdk`**（默认路径缺 API 36）；构建必须显式 `-p:AndroidSdkDirectory=... -p:JavaSdkDirectory="C:\Program Files\Java\jdk-21"`，且路径纯 ASCII |
| 实测 | Arc 探针 APK 构建 2m13s / 安装成功 / 冷启动 1.2s / Agent Surface 正常渲染（`_scratch/arc-probe-device.png`）；TestDevice 无线 adb 在线 |
| 包体积 | Debug 全 ABI 75.3MB（裸 Avalonia 70.9MB）；Release 单 arm64 预期 ~20MB |
| Arc 接入 | `<arc:ArcTheme/>`；`Surface.Attach(workspace, transport, services)`；**一条长连接 + 一个 sink 可喂多 run**（按 `Stamp.RunId` 分桶，orphan 缓冲 256）；run 结束必须推 `RunPhaseChanged`（`sink.Complete` 是 no-op） |
| 必踩坑 | `AvaloniaMainActivity` 必须 AppCompat 主题；`EmbedAssembliesIntoApk=true`；两个同名 `ArcAgentEvent(Kind)` 用 using 别名；Transcript 单独用需设 Markdown 工厂 |
| 移动端缺口 | Arc 零移动适配：安全区/IME 避让/返回键/触摸模式/窄屏布局都要自写（探针里运行条已重叠） |

---

## 3. 架构

```
┌──────────────────────────── 手机（Android） ────────────────────────────┐
│  DshRemote.App (Avalonia；net10.0-android + net10.0 桌面壳共用 XAML)      │
│   ├─ ArcTheme + ArcAgentSurface/Transcript/ToolCallCard/ApprovalTray     │
│   │   /Composer/RunControlBar（自写安全区、IME、返回键、窄屏重排）        │
│   └─ 会话列表 / 连接与配对 / 前台服务 + 通知（M3）                       │
│                              │                                           │
│  DshRemote.Core (net10.0)                                                │
│   ├─ DshClient：/connect 认证、RPC 信封、WS mux、cookie 持久化           │
│   ├─ Streams：session/follow、session/observeEvent、$events（M2）        │
│   ├─ DshEventMapper：SessionEvent → ArcAgentEvent（唯一映射点）          │
│   ├─ ArcTransportAdapter：IArcAgentTransport/EventSink 实现              │
│   └─ PairingStore / TlsPinning：token、SPKI 指纹、Keystore              │
└──────────────────────────────┬───────────────────────────────────────────┘
                     HTTPS（自签证书 + SPKI pinning）+ Cookie
                               │
┌──────────────────────────────┴──────────────────────────────┐
│  桌面 DSH 进程内：dsh-remote-relay（Cordis 薄插件，Node ESM）  │
│   ├─ 发现：ctx.webServer.port / ctx.connection.authenticatedUrl()│
│   ├─ TLS 终止（自签 cert/key，配置提供）                        │
│   ├─ 反向代理：HTTP + WS upgrade → 127.0.0.1:<port>             │
│   │   （Host/Origin 重写为 loopback，cookie 原样透传）          │
│   └─ 配对：生成 dshremote://pair?...&fp=<SPKI>（日志/文件/页面）│
└──────────────────────────────┬──────────────────────────────┘
                               │ 明文 loopback（同进程/同机）
┌──────────────────────────────┴──────────────────────────────┐
│  desktop-host 的 dsh-host-webserver（127.0.0.1:随机端口）      │
│   ├─ /connect → API cookie + owner Peer                       │
│   ├─ POST /api/<ns>/<method>（typert gateway）                │
│   ├─ WS /api/remote.mux（逻辑流）                             │
│   └─ $events（广播 + approval/question waterfall）              │
│                    ↓ 同进程调用                                │
│  sessionController / approval / userQuestions / Agent runtime  │
└───────────────────────────────────────────────────────────────┘
```

### 3.1 为什么必须有这个插件

1. 桌面 webserver 写死 loopback，手机无法直连；插件做 TLS 反代/中继。
2. launch token 是进程级随机值，只在进程内可读（`ctx.connection.authenticatedUrl()`）；插件负责把它变成可配对的信息。
3. 插件是唯一能改的 DSH 侧代码位置；它**不介入业务**，所以升级风险最小。

### 3.2 职责边界

- **插件**：发现端口/token、TLS、反代、配对信息、自身健康检查。**不读事件、不改语义、不存审批**。
- **Core（C#）**：协议客户端（认证/cookie/RPC/WS mux/流）、断线重连与补洞、`SessionEvent → ArcAgentEvent` 映射、错误归一化。
- **App**：XAML、导航、通知、配对、安全区/IME/返回键、Keystore。

---

## 4. 协议子集（手机实际要实现的官方 wire）

> 详细文档定为 `docs/protocol-v1.md`（M0 第一步产出，含每个请求/帧的 JSON 样例与错误码）。

### 4.1 认证

1. 配对 URI：`dshremote://pair?v=1&host=<LAN IP>&port=<relay 端口>&token=<launchToken>&fp=<TLS SPKI 指纹>`；MVP 手输/粘贴，M3 扫码。
2. `GET https://<host>:<port>/connect?token=<token>&interactions=answer`（M0 用 `none`，M2 审批改为 `answer`）→ **302 + `Set-Cookie`（API cookie）+ 服务端发布 owner Peer**。客户端不跟随 302（或跟随也无害），提取 cookie 与 `location`（mux 路径）。
3. 打开 `WSS https://<host>:<port>/api/remote.mux`，带 `Cookie`；升级通过后该 Peer 存活；**WS 断开后 HTTP 带旧 cookie 会被拒** → 客户端自动重新 `/connect`。

### 4.2 一元 RPC（HTTP）

```
POST /api/<namespace>/<method>
content-type: application/json
Cookie: <api cookie>
{ "type":"client-request", "rpcId":"<uuid>", "method":"<method>", "payload": <JSON args> }

→ { "type":"server-response", "rpcId":"<uuid>",
    "result": { "ok": true, "value": ... } | { "ok": false, "error": { code, message, details } } }
```

M0/M1 需要的端点：

| 端点 | 用途 | 备注 |
|---|---|---|
| `session/list` | 会话列表（`{items: SessionSummary[]}`） | 结果类型无 strict schema（`SessionSummary` 形状见调研 §2.1/真实响应） |
| `session/events` | 历史分页 `{address, beforeSeq, maxMessages, turnWindow}` → `{cursor, records:[{type:'event',event}], hasMore}` | 冷会话可用，无需激活 Agent |
| `session/prompt` | `{requestId, sessionId, mode:'queue'|'steer', content:[{type:'text',text}], clientTimeZone}` → `{accepted:true}` | requestId 幂等 |
| `session/cancel` | `{sessionId}` → `{accepted:true}` | 停止当前轮 |
| `session/state` | `{cursor, active, running, queue, pendingInput}` | 会话详情/状态 |
| `session/updateQueue` | 队列编辑（M2 可选） | |
| `session/selectModel` | 切换模型（M1 可选） | |

### 4.3 WS 逻辑流

客户端 → Host：`{type:'open', streamId, endpoint, payload}` / `{type:'item', streamId, value?}` / `{type:'end', streamId}` / `{type:'cancel', streamId}`
Host → 客户端：`{type:'item', streamId, value?}` / `{type:'end', streamId}` / `{type:'error', streamId, error:{code,message,details}}`

M0：`session/observeEvent`（`{type:'ready'}` → `{type:'event', event}`）。
M1：`session/follow`（首个 `{type:'snapshot', header, projections, cursor, records, hasMore}`，后续 event；用于断线补洞）。
M2：`$events`（ready `{clientId, host:{home}}` → `emit` / `waterfall`）+ `POST /api/$events/result` 应答审批/提问。

### 4.4 事件 → Arc 映射（客户端 Core）

| DSH `SessionEvent.type` | ArcAgentEvent |
|---|---|
| `turn/start` | `RunStarted`（runId 由客户端按 turn 生成）+ `RunPhaseChanged(Running)` |
| `step/start` / `step/end` | `StepStarted` / `StepFinished` |
| `user/message` | `MessageStarted(User)` / `MessageEnded`（历史回放聚合） |
| `assistant/message` | `MessageStarted(Assistant)` / `MessageDelta` / `MessageEnded`（`assistant/attempt` 增量可选） |
| `tool/call` | `ToolCallStarted` + `ToolArgsDelta`（无增量时一次性全量） |
| `tool/result` | `ToolCallEnded(Succeeded/Failed)` |
| `approval/asked` / `approval/decided` + `$events` waterfall | `ApprovalRequested` / `ApprovalResolved`（审计事件可用于恢复） |
| 网关无需新增：`user-questions` waterfall | `QuestionRequested` |
| `turn/end` | `RunPhaseChanged(Done/Stopped/Failed)` |
| 历史分页/follow 快照 | `StateSnapshot` + 增量 |
| 未知类型 | 忽略/日志（`ArcCustomEvent` 兜底） |

### 4.5 TLS 与 pinning

- 插件用配置的 `tls.certFile/keyFile` 起 HTTPS；证书由 `scripts/gen-tls.ps1` 生成自签（SAN 含 `host` 的 LAN IP/主机名），并计算 SPKI SHA-256 指纹。
- 配对 URI 带 `fp`；App 首次连接时记录指纹（Keystore 加密），之后只接受该指纹（`ServerCertificateCustomValidationCallback` 比对 SPKI）；指纹变化 → 明确报错并要求重新配对。
- 桌面 loopback 段仍是明文 HTTP（同机，不经过网络）。

---

## 5. 仓库结构

```
DSHRemote/
├─ README.md                      # 总览 + 10 分钟快速开始
├─ docs/
│  ├─ PLAN.md                     # 本文（v1）
│  ├─ protocol-v1.md              # 官方 wire 子集：认证/RPC/WS 帧/错误码/样例（M0 产出）
│  ├─ event-mapping.md            # SessionEvent → ArcAgentEvent 映射 + 用例（M1 产出）
│  ├─ security.md                 # 威胁模型、TLS/pinning、配对轮换、权限档位
│  └─ research/                   # dsh-internals.md / arc-android.md（已完成）
├─ relay/                         # dsh-remote-relay（Cordis 薄插件，Node ESM）
│  ├─ package.json                # name/version/dsh.plugin
│  ├─ src/index.mjs               # 插件入口：Config/apply、发现 port+token、自检
│  ├─ src/proxy.mjs               # TLS 服务 + HTTP/WS 反向代理（Host 重写）
│  ├─ src/pairing.mjs             # 配对 URI/QR、证书指纹
│  └─ test/*.test.mjs
├─ app/
│  ├─ DshRemote.sln
│  ├─ src/DshRemote.Core/         # net10.0：认证/RPC/WS/流/映射/状态机
│  ├─ src/DshRemote.App/          # net10.0 + net10.0-android：XAML/Arc
│  └─ tests/DshRemote.Core.Tests/
├─ scripts/
│  ├─ dev-install.ps1             # junction + patch 注入 + 热加载
│  ├─ gen-tls.ps1                 # 自签证书 + SPKI 指纹 + 配对 URI
│  ├─ build-apk.ps1               # 显式 AndroidSdkDirectory/JavaSdkDirectory
│  └─ e2e.ps1                     # 本机/真机冒烟
└─ _scratch/                      # 已验证的 Android 探针（可提为正式模板）
```

> **先桌面壳调 UI，再装真机**：App 项目双目标，共享同一份 XAML/ViewModel。

---

## 6. 里程碑与验收

| 里程碑 | 内容 | 验收标准 | 预估 |
|---|---|---|---|
| **M0 骨架 + 最小闭环** | relay 插件（发现 token/port、TLS、反代、配对 URI）；Core：`/connect` + RPC 信封 + WS mux + `session/list/events/prompt` + 重连；App：会话列表 + 聊天（历史+实时）+ 输入框 + 停止；桌面壳 + Android 头；TLS pinning；手工配对 | 手机 APK 装 TestDevice：列会话 → 打开看历史与实时输出 → 发消息 → 桌面会话继续执行 → 点停止生效；重连不崩、TLS 指纹不匹配明确报错；桌面 Electron UI 不受影响 | 2–3 天 |
| **M1 可读性** | `session/follow` 快照 + seq 补洞；Markdown 流式；工具调用卡片；推理折叠；会话切换/标题/模型/运行状态/队列展示；错误与空态 | 长会话（≥300 条）滚动流畅；杀 App 重开正确还原；断网 30s 重连后不丢消息 | 3–5 天 |
| **M2 审批 + 写操作** | `$events`（`interactions=answer`）+ waterfall 审批/提问 + `$events/result`；停止/改向；queue 编辑/删除；决议幂等与超时 | 桌面弹审批 → 手机收到并可批准/拒绝；桌面先答则手机卡片消失；手机重连后的 pending 表现符合官方语义（已在桌面端提示时可代答） | 3–5 天 |
| **M3 移动端体验** | 前台服务 + WS 常驻；完成/待审批本地通知；Keystore；二维码配对；安全区/IME/返回键/窄屏重排；长列表虚拟化 | 锁屏 10 分钟后能收到通知；窄屏不重叠；证书轮换流程可走通 | 3–5 天 |
| **M4 工程化** | 插件打包/安装脚本；协议漂移自检与版本探测；Core 单测 + E2E；README/安装文档；APK 签名/侧载；体积/性能 | 新机器按文档 15 分钟装好真机跑通；Release APK ~20MB；升级 DSH 后自检能明确报告不兼容 | 2–4 天 |

---

## 7. 安全模型

| 风险 | 措施 |
|---|---|
| 手机拿到的是 **owner** 权限（官方模型 owner 恒放行） | 配对 URI 等同密码；仅通过安全渠道传递；App 内可「忘记此电脑」；`/connect` cookie 30 天但 WS 断即失效，需重新 /connect（token 仍有效） |
| 局域网明文 | **首版即 TLS**：自签证书 + SPKI pinning；relay 只监听配置的 LAN 地址，绝不监听 `0.0.0.0` |
| token 泄露 | 配对 URI 不落盘明文（App 用 Keystore）；relay 日志脱敏；桌面端可重启进程轮换 launchToken（重启 DSH） |
| 外网暴露 | 不做端口映射；外网用 WireGuard/Tailscale，relay 绑 VPN 地址 |
| 只读诉求 | 手机端提供「只读模式」开关（不发送写操作）；真正的 ACL 用官方 liveshare（实验性，另案） |
| 审批误触 | 审批卡片显示工具/参数/风险；M2 支持「仅本次」；危险工具二次确认；桌面 UI 仍是最终兜底 |
| 插件自身被利用 | 反代只转发到 `127.0.0.1:<port>` 固定上游；Host/Origin 重写；不开放任意目标转发；来源 IP 白名单（可选） |

---

## 8. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | 官方协议预发布、无兼容承诺 | 客户端随 DSH 升级而破 | 每次 DSH 升级跑契约/E2E 冒烟；`contractVersion` + 失败降级只读；锁版本测试；协议子集文档记录所用端点 |
| R2 | strict codec 剥字段 / 参数不匹配 | 调用 400 | DTO 由 `scripts/remote-contract.json` 生成；只发送 `additionalProperties:false` 允许的字段；新增端点先跑契约校验 |
| R3 | API Peer/cookie 生命周期 | 断线后 401、重连复杂 | Core 维护「连接状态机」：WS 断开 → 丢弃 cookie → 重新 `/connect` → 重开流 → follow 补洞；错误码驱动 |
| R4 | 反代转发 HTTP/WS 的细节 | 101/升级失败、cookie 不匹配 | 代理层只做「明文 TCP 转发 + Host 重写」，不解析 body；WS 用 upgrade 事件透传；单测覆盖 302/cookie/101/大响应 |
| R5 | 事件流 untrusted 形状 | 映射崩溃 | 映射器对未知 type/畸形 data 全兜底；加入「事件回放测试」用真实 jsonl 会话日志 |
| R6 | TLS 自签证书 | Android 校验失败/指纹错 | 自定义校验只认 pin；`gen-tls.ps1` 生成 SAN 与指纹并写入配对 URI；证书轮换=重新配对 |
| R7 | Avalonia 无移动适配 | IME/安全区/返回键/窄屏重叠 | 外壳自写；探针已确认关键坑；M3 集中做；布局用响应式断点 |
| R8 | 构建依赖私有 SDK 路径 | 换机/CI 失败 | `build-apk.ps1` 显式传 `AndroidSdkDirectory`/`JavaSdkDirectory`；README 写清；必要时容器化 |
| R9 | Arc 0.9.0-alpha 演进 | API 破坏 | pin 版本；ViewModel/映射层隔离；升级前跑探针 |
| R10 | 桌面与手机同时写同一会话 | 排队/时序 | 复用 DSH queue/steer 语义；UI 显示「已排队」；不强制互斥 |

---

## 9. 与原型的关系

| 原型 | 本方案 |
|---|---|
| 自建 4 个 HTTP 接口 + 手搓页面 | 官方协议子集 + Avalonia App |
| 事件只有纯文本 | 全事件透传 + C# 映射（工具/推理/审批/问答） |
| 无历史、30s 清屏 | `session/events` 分页 + `session/follow` 快照补洞 |
| 无审批 | `$events` waterfall + `$events/result`（M2） |
| 明文 token 走局域 HTTP | 自签 TLS + SPKI pinning + 配对 URI |
| 依赖内部 `sessionController.prompt/observeEvent` | 依赖官方 wire（cookie/RPC/mux），内部 API 变更只影响中继 |

---

## 10. 决策记录

| 日期 | 决策 | 结论 |
|---|---|---|
| 2026-11-08 | 传输路线 | **采用官方 Remote 协议**（不自建 `/v1`）；DSH 侧只做 TLS 配对中继 |
| 2026-11-08 | 审批里程碑 | M2；M0 先「历史/流式 + 发消息 + 停止」 |
| 2026-11-08 | 网络基线 | 首版即自签 TLS + 指纹 pinning；外网走 VPN |
| 2026-11-08 | 客户端形态 | Avalonia 原生 App + Arc 组件（桌面壳同源 XAML） |

---

## 11. 下一步（M1 起）

1. 基于 M0 的真实事件流做 `session/follow` 快照 + seq 补洞（重连不丢消息）。
2. Markdown 流式渲染、推理折叠、工具参数/结果详情展开。
3. 会话切换/队列展示/模型切换；列表虚拟化与 IME 避让。
4. M2：`$events`（`interactions=answer`）+ 审批/提问 waterfall + `$events/result`。
5. M3：前台服务 + 通知、Keystore、二维码配对、证书轮换。

---

## 12. M0 验收记录（2026-11-09）

**状态：M0 完成并在真机验收通过。** 交付物与验收证据：

| 交付物 | 位置 | 验收 |
|---|---|---|
| relay 薄插件（TLS 反代 HTTP+WS、配对信息、健康检查） | `relay/` | 热加载进桌面 DSH（PID 29432，未重启），TLS `192.0.2.10:8443`；`/connect` 302+cookie、`session/list` 200、WS mux 101+ready |
| 配对/安装/证书脚本 | `scripts/gen-tls.ps1`、`dev-install.ps1`、`relay-smoke.ps1`、`build-apk.ps1` | smoke PASS；dev-install 幂等（重复执行不重启）；证书 SPKI 与 pairing 一致 |
| 协议客户端 + 测试 + 冒烟 | `app/src/DshRemote.Core`、`app/tests`、`app/tools/DshRemote.Smoke` | 42/42 单测通过；Smoke 经 relay TLS pin 连接真实桌面：219 会话、历史、state、live observeEvent |
| Avalonia App（桌面壳 + Android 头） | `app/src/DshRemote.App*` | 真机 TestDevice：配对→连接→会话列表（222 条/运行中标记）→打开会话（历史+实时工具卡）→发消息→停止（`turn 1 (aborted)`）；桌面壳同源 XAML 可跑 |
| 协议文档 | `docs/protocol-v1.md` | 含 6 个实测坑与请求/帧样例 |

真机证据截图：`docs/evidence/phone-sessions.png`、`phone-chat-real.png`、`phone-send.png`、`phone-stop.png`。

### 与计划的偏差（已接受）

- **UI 未接 `ArcAgentSurface`**：M0 用 ArcTheme + 自绘消息/工具卡（Arc Surface 需要完整的 ArcAgentEvent 映射，放 M1），换取了更快的真机闭环。
- **事件渲染先不支持 `assistant/attempt` 增量**：M0 只渲染最终 `assistant/message`；打字机效果 M1。
- **会话列表未虚拟化**：222 条首屏可接受，M3 换 `ListBox`。
- **软键盘未避让 composer**：Enter 仍可发送；M3 用 `TopLevel.InputPane` 处理。
- **relay 未做访问控制**：安全边界 = TLS + pairing token + 短生命周期 cookie；多用户/只读档位不在 M0。

### 遗留缺陷/风险

- DSH 协议为预发布（0.2.2-rc.1），升级后需重跑 `relay-smoke.ps1` + `dotnet test`。
- relay 重启窗口内 `pairing.json` 会短暂消失 1–3 秒（App 侧读取失败需重试；M1 处理）。
- 手机侧尚未处理 relay 重载后 token/端口变化（重新配对本可解决；M2/M3 做自动重连提示）。

---

## 13. M1–M4 完成记录（2026-11-09）

**状态：M1、M2、M3、M4 全部完成**（桌面壳 + Android 模拟器验收；真机 TestDevice 锁屏导致 M1–M3 真机补验待解锁）。

### M1 可读性
- Core：`ISessionFollowClient`（`DshClient.Follow.cs`）——`session/follow` 首帧 snapshot + live 事件，断线后重开 follow + `session/events` 反向分页补洞，按 seq 去重，缺口不可补时抛 `SessionFollowResyncException`；`SteerAsync` / `UpdateQueueAsync`；子代理 lineage 字段（`ParentSessionId`/`Origin`）。
- UI：ArcMarkdown 渲染；`assistant/attempt` 流式打字机（settle 用 `assistant/message` 权威覆盖）；推理折叠；工具卡参数折叠/结果展开/耗时/错误态；打开会话走 snapshot→follow；会话切换释放订阅；401/403 可读错误；队列/模型/运行态徽标；子代理会话从列表过滤（真实列表 68 会话 / 隐藏 161 子代理）。
- 验收：64/64 单测；桌面真实 relay 227+ 会话；模拟器真实会话渲染 + 断线补洞路径通过。

### M2 审批 + 写操作
- Core：`IInteractionClient`（`DshClient.Interactions.cs`）——`/connect` 固定 `interactions=answer`；`$events` pump（ready clientId → waterfall → cancel）；审批/问答帧解析（`displayReason` locale map、`agentId`=sessionId）；`$events/result` 应答（`{clientId,eventId,outcome}`）；requestId 去重、二次应答 no-op、失败回滚。
- UI：审批托盘（允许一次/拒绝/倒计时/已提交）、问答卡（单选+自由文本/提交/关闭 cancelled）、steer、队列编辑/删除/插队。
- 验收（真实桌面 + 模拟器）：审批 → 允许一次 → 桌面文件写出并继续；提问 → 选 A → agent 回复 A；通知、跨端去重正常。

### M3 移动端体验
- Android 平台层：`ConnectionForegroundService`（dataSync，修复 startForeground 5s 内 stop 竞态）、双通知渠道（status Low / events High）、`KeystoreSecretStore`（AES-256/GCM + SharedPreferences 密文）、深链 `dshremote://` intent-filter、`PlatformServices`（AppForeground/SafeAreaInsets/ImeHeight/OnBackPressed 等）、AppCompat `OnBackPressedDispatcher`（适配 Android 15+ 预测式返回）。
- 共享 UI：SecretStore 配对恢复/保存、深链自动连接、通知点按开会话、连接状态驱动前台服务、审批/问答/回合完成通知（前台同会话抑制）、安全区 Margin、IME 避让（`TopLevel.InputPane` + `ImeHeight`）、窄屏折叠、会话列表 `ListBox` 虚拟化、返回键聊天→列表。
- 扫码配对：relay loopback 页 `GET /__relay/pair`（仅本机 200、LAN 404、CSP/no-store），vendored `qrcode-generator` 生成二维码；`scripts/show-pairing.ps1` 打开。
- 验收：模拟器 31 PASS / 0 FAIL（FGS dumpsys、通知、深链脱敏、Keystore 跨进程持久化、insets、返回键）；Lead 手工验收审批/问答/深链/IME/返回键/Keystore 恢复。

### M4 工程化/分发
- relay：`/__relay/health` 增加 relayVersion/dshVersion/protocolProbe（5 能力位）；`pairing.json` 增 active/capabilities/probe/error；失败不静默降级。
- 打包/安装：`package-relay.ps1`（zip + sha256 + INSTALL.md）、`install-relay.ps1`（zip/目录、证书保留、幂等、WhatIf）、`uninstall-relay.ps1`。
- 测试：`e2e.ps1`（relay-smoke → 单测 → Smoke → APK/安装/启动，支持 `-Serial/-Apk/-Release/-SkipAndroid`）；`gen-signing-key.ps1` + `build-release-apk.ps1`。
- Release APK：`dist/apk/dshremote-1.0.0-10-arm64-release.apk`（33.77 MB，apksigner verify 通过）。
- 文档：README 重写、`docs/install.md`、`docs/upgrade.md`、`relay/README.zh.md`、`docs/evidence/README.md`。
- 验收：`e2e.ps1 -Serial emulator-5556` 全 PASS（8/8 步）。

### 真机补验（2026-11-09 08:xx，TestDevice 解锁后）
- 安装：ColorOS「安装增强防护」拦截 adb 全新安装（`-99`）；解锁后在手机上「更多 → 开始深度扫描 → 继续安装」即可安装（Debug/Release 均可）；同签名升级无此问题。
- `verify-m3.ps1 -Serial <真机>`：**全部 PASS**（device online/unlocked、深链拉起+token 脱敏、平台初始化、POST_NOTIFICATIONS、sessionId extra、AppForeground、insets、Keystore AES/GCM 往返 + 跨进程持久化 + 仅密文落盘、FGS `isForeground=true` + `dataSync` + 常驻通知、双渠道与审批通知、无 FATAL）。
- 手工验收：真实 relay 连接（68 会话 / 隐藏 161 子代理）、真实会话 Markdown/工具卡/推理折叠、审批托盘「允许一次」→ 桌面文件写出、问答卡选 A → agent 回 A、IME 避让、返回键、深链自动连接、Keystore 恢复、后台审批通知。
- 修复：发送后「已发送」乐观气泡与持久 `user/message` 重复（现在按 `source.rpcId` 精确匹配 + 文本兜底合并，真机 uiautomator 计数=1）；真机复验通过。
- `e2e.ps1 -Serial <真机>`：8/8 步 **PASS**（relay-smoke、64 单测、Smoke、APK、adb、安装、配对推送、启动截图）。

### 遗留 / 说明
- 模拟器 AOSP 镜像缺部分 CJK 字形（部分静态中文标签方框，数据/文本正常；真机无此问题）。
- 流式打字机当前依赖 `assistant/attempt.data.stream` 回放（Core 不透传 cursorless 增量）；如需真增量可在 M5 扩展 `$events`/follow 的 assistant-stream 通道。
- Release APK 33.78 MB（略高于 20–30 MB 目标；Avalonia 12 + .NET 10 + 单 arm64 的固有体积）。


---

## 14. M5 完成记录（2026-11-09）

用户新增 5 项功能：图片显示/发送、文字重叠修复、APK 下载接口、会话按工作区分类 + 侧栏、新建/左滑归档。

**交付**
- Core（core-dev，task-10）：`IWorkspaceClient`（workspace/follow baseline、archive/unarchive、create session）、`IImageClient`（session/attachment → base64）、`IDshClient.SendPromptAsync(..., images)`、`DshContent` 内容解析；85 单测；Smoke 增 `--workspaces/--create-session/--archive/--unarchive/--image/--images/--get-image`。
- UI（ui-dev，task-11）：MarkdownBodyView 重写（自研轻量 Markdown 渲染器，替换 Android 上叠层的 ArcMarkdownView）、图片缩略图/全屏查看/附图发送、工作区分组列表 + ArcDrawer 侧栏、新建会话、左滑归档/恢复。
- 左滑归档修正（lead）：ArcSwipeActions 放在 Button 内会被 Button 吃掉手势 → 改为行 Border 自绘 Pointer 手势 + 隐藏归档按钮（拖动阈值/回弹/纵向滚动不误触）。
- Android（android-dev，task-13）：PickVisualMedia/GetContent 选择器、Bitmap 压缩（≤1600px/2MB）、`PlatformServices.ImagePicker`、`dshremote.imagePath` 调试入口；24 PASS；M3 回归 31 PASS。
- relay（relay-dev，task-12）：下载页 + APK 流式下载 + health/pairing download 字段 + `show-download.ps1` + 文档；白名单/穿越/HEAD/字节 sha 验证 PASS。
- 发布：`dist/apk/dshremote-1.1.0-12-arm64-release.apk`（33.83 MB，sha256 `dcb3caf0…5ac8`）。

**模拟器验收（phone 被用户带走）**
- 真实 relay：列表按 8 个工作区分组、新建会话、侧栏切换、左滑归档（31→30）、归档区「已归档（46）」。
- 图片：附图按钮取 `dshremote.imagePath` → 缩略图 chip → 发送 → 气泡显示 800×600 彩虹图；agent 回复已看到图片。
- 文字：原叠印长消息在自研 Markdown 下排版正常（粗体/列表/行内代码均正确）。
- `e2e.ps1 -Serial emulator-5556`：relay-smoke/core-tests(85)/smoke-tool/apk/adb/apk-install/pairing-push/app-launch 8/8 PASS。
- 下载：`/__relay/download` 200 且最新列出 1.1.0-12；下载字节 35,469,848、sha256 与磁盘一致。

**遗留**：真机未回归（用户带走手机后通过下载链接自行验证）；模拟器 AOSP 无 Chrome 首次运行的证书/网络提示不影响真实手机；工具结果图片加载链路同用户图片，未单独用亮色图验证（暗色截图缩略图视觉上接近黑）。

---

## 15. 外网访问：VPS 公网入口（2026-11-09）

- 架构：relay 必须留在桌面（launchToken 进程级）；VPS 仅做公网入口 + SSH 反向隧道（TLS 仍在桌面 relay 终止，手机 SPKI pin 不变）。
- 部署：`dev-install.ps1 -PublicHost 203.0.113.10 -PublicPort 9443` 让 pairing 文档/URI 使用公网地址并保留 `localPairingUri`；VPS `ufw allow 9443/tcp`；桌面 `scripts/vps-tunnel.ps1` 托管 `ssh -R 0.0.0.0:9443:127.0.0.1:8443`（自动重连 + 登录自启）。
- 公网地址：`https://203.0.113.10:9443`，下载页 `/__relay/download`。
- 验证：health ok（publicHost/publicPort 正确）；公网下载 APK 35,469,848 bytes、sha256 一致；TestDevice 通过公网深链配对连接成功（顶栏 `203.0.113.10:9443 · TLS 指纹已固定`），列表/图片/问答/归档正常；kill ssh 后 5s 内自动重连。
- 文档：`docs/vps-deploy.md`。

---

## 16. M6 完成记录（2026-11-10）

1. 插件 bundle 化：`relay/package.json` `dsh.bundle.patch` + `cordis.patch.yml`；`scripts/install-relay-bundle.ps1` 经宿主 installBundle 安装（listBundles 可见、启停可用）；`scripts/package-relay-plugin.ps1` 产出 dir/tgz。
2. VPS 通用网关：`vps/` 零依赖 Node + systemd（9443），Token 路由多桌面、TLS 入口、APK 下载、心跳/重连；`scripts/deploy-gateway.ps1`/`sync-apk.ps1`；文档 `docs/vps-gateway.md`。
3. relay v2 配对：远程 Token 持久化（identity.json）、`/__relay/pair` 管理页（桌面名/服务器地址/Token/二维码/轮换）、gateway 在线状态；文档 `docs/remote-v2.md`。
4. App 多桌面：ProfileStore/ProfilesViewModel/DesktopsView，扫码（ZXing.Net）/粘贴/文件添加、切换、重命名、删除；v2 字段持久化。
5. UX：工作区下拉、历史分页懒加载（首屏 33ms 中型 / 1.2s 冷大历史，-71%/-86%）；ArcMarkdownView + 内置 Noto Sans SC 解决 Android 中文渲染（方案 B；Agent 层组件缺口见 `docs/research/arc-agent-integration.md`）。
6. 验证：Core 111 测试；Android QR 18/18 + M3 31/31；网关 53/53 + 15/15；VPS 真机 v1.2.0 连接成功；Desktop/Android 0/0；发布 APK 1.2.0（35.4MB，sha256 20e773d5…5eda4）。

### M6 收尾（2026-11-10）
- ArcMarkdownView 在“表格+代码块”混合文档上仍叠层；MarkdownBodyView 改为按顶级块拆分、每块独立 ArcMarkdownView 渲染，彻底解决（真机 v1.2.1 验证通过）。
- 发布 APK 更新为 1.2.1 (14)：`dist/apk/dshremote-1.2.1-14-arm64-release.apk`（sha256 `901a3611…a4e6b`），已同步 `https://203.0.113.10:9443/__gateway/download`。

### 最终验收（2026-11-10 01:23）
- `dotnet build DshRemote.sln`：0 warning / 0 error；Core 测试 111/111。
- `e2e.ps1 -Serial emulator-5556 -SkipApkBuild`：**8/8 PASS**（relay-smoke / core-tests / smoke-tool / apk / adb / install / pairing-push / app-launch）。
- VPS 网关：identity `tunnels=1`；下载页最新 `dshremote-1.2.1-14-arm64-release.apk`（sha256 `901a3611…a4e6b`）。
- 真机 TestDevice v1.2.1：经 `203.0.113.10:9443` 连接；工作区下拉、桌面管理、长中文 Markdown（列表+代码块+表格）Arc 渲染无叠层。
- 插件 bundle：`pluginManager/listBundles` → dsh-remote-relay 0.3.0 installed=true enabled=true rows=[remote-relay]。

### v1.2.2 收尾（2026-11-10）
- Arc 改为引用本机 Arc.Avalonia.Meta 工程；删除内置 Noto 字体；保留 Markdown 顶级块拆分（Arc 混合表格+代码块仍叠层）。
- 应用内 Camera2 扫码 Activity + ZXing（手电筒/相册/权限），真机帧级解码通过；归档会话不再显示；插件详情 README 增加配对二维码链接。
- APK 1.2.2 (15) sha256 48cabad7…66275；真机验证通过。

### 归档后自动跳到其他工作区修复（2026-11-10）
- 现象：手机端在某个工作区（如 dsh remote）左滑归档一个会话后，列表跳到另一个工作区（如 Arc.Avalonia）。
- 根因（两处叠加）：① `LoadSessionsAsync` 每次刷新重建分组时会丢弃「没有可见会话」的工作区，归档掉最后一条可见会话的工作区直接从下拉里消失；② 重建前先 `WorkspaceChoices.Clear()`，Avalonia ComboBox 的 TwoWay `SelectedItem` 会在 `Clear()` 内同步回写 null，把 `Settings.LastWorkspaceId` 清掉（headless 探针实测：回写发生在集合已空时、早于旧代码读取偏好），于是兜底按「运行中/最近更新」重新选工作区 = 跳转。
- 修复（`MainViewModel`）：重建前快照当前选中的工作区 id；保留当前选中的工作区（即使已空，显示空列表而不是跳走）；`LastWorkspaceId` 只对非 null 的真实选择持久化；断开连接时同步清空工作区下拉；新增 `DshRemote.App.Tests`（4 个回归测试：归档后仍停留原工作区/「其他」分组不被丢弃/占位 null 不回写偏好/刷新保留选中），旧逻辑下其中 3 个失败并可复现 `ws-demo-dshremote → ws-demo-arc` 的跳转。
- 验证：App 测试 4/4、Core 测试 111/111；Desktop 头与 Android 头 `0 warning / 0 error` 构建通过。

### v1.2.3 发布（2026-11-10）
- 内容：包含上面的「归档后自动跳到其他工作区」修复。
- APK：`dist/apk/dshremote-1.2.3-16-arm64-release.apk`（34.12 MB，versionCode 16，arm64-v8a，apksigner 校验通过，sha256 `b794d2cc…b5ee`）。
- 已用 `scripts/sync-apk.ps1` 同步到 VPS `/opt/dsh-remote/apk`，ssh 校验远端 sha256 一致；网关 `__gateway/identity` 的 `download.latest` 已变为 1.2.3 (16)。
- 下载页：`https://203.0.113.10:9443/__gateway/download`；直链 `/__gateway/download/dshremote-1.2.3-16-arm64-release.apk`。
- 手机覆盖安装即可（同签名、versionCode 递增）；App 内 Keystore 保存的配对不受影响，无需重新配对。

### v1.2.3（2026-11-10）
- 新增「自动允许审批」开关（持久化、全局），自动以 allow-once 应答审批并抑制通知；问题仍需回答。
- 模拟器真审批链路验证（自动写文件/turn 结束）；APK 1.2.3 (16) 已同步网关。

### v1.2.5（2026-11-10）
- 修复 v1.2.3 增量打包导致的启动闪退（libxamarin-app 程序集存储不完整）；release 脚本强制 clean。
- Markdown 链接转纯文本 + 零宽断行，修复叠印/基线/截断；全屏输入设置；思考+工具折叠（运行计时/默认折叠）。
- APK 1.2.5 (18) sha256 4f3f6ad7…677d84；x86_64 release 启动验证通过。

### v1.2.7（2026-11-10）：设置页偏好不持久化修复

- 现象：设置页「自动允许审批」「输入时展开全屏」打开后，重启 App 又回到关闭（初始）状态；配对本身能恢复。
- 复现（模拟器 x86_64 API 36）：设置开关 → `files/settings.json` 写入 true → `am force-stop` + 重启 → 文件与设置页 UI 都变回 false。
- 根因：Android 头把整份设置文档（含两个偏好）作为配对快照加密存进 Keystore（`dsh.pairing`，只在连接成功时写入），而偏好 setter 一变就立即把整份文档写回 `settings.json`。冷启动顺序是 `Load()`（settings.json，值正确）→ `LoadFromSecretJson()`（旧快照回放）→ `ReloadPreferences()`；第 2 步把连接时的旧偏好（false）写回文件，第 3 步读到的已经是被覆盖的值 → 设置页永远停在连接时的状态。桌面头没有 SecretStore，所以只在手机上出现。
- 修复（`SettingsViewModel`/`MainViewModel`）：
  - 配对快照只承载连接字段（`ToSecretJson` 不再写入偏好与工作区）；`LoadFromSecretJson` 只回放连接字段。
  - 新增 `_isRestoring` 写盘保护：`Load` / `LoadFromSecretJson` / `ReloadPreferences` 回放期间偏好 setter 不落盘；`settings.json` 成为偏好的唯一权威来源，`ReloadPreferences()` 仍在安全存储恢复之后执行。
- 回归测试：新增 `DshRemote.App.Tests/SettingsPersistenceTests`（3 个）。修复前 `StartupRestore_DoesNotRollPreferencesBackToTheConnectTimeSnapshot` FAIL（旧快照把 true 覆盖成 false），修复后 **App 11/11、Core 111/111** 全绿。
- 模拟器 E2E（同一台 x86_64 API 36，用真实故障路径）：把携带旧偏好的 Keystore 快照放回设备后，修复前重启把 `settings.json` 从 true 覆写为 false（复现）；装修复版后同一快照下重启，`settings.json` 仍为 true、两个开关仍勾选，连接区显示「已从安全存储恢复配对」（证明快照仍被读取，只是不再覆盖偏好）。
- 发布：`dist/apk/dshremote-1.2.7-20-arm64-release.apk`（34.12 MB，sha256 `b5d28080e902e164946c92c2d07904feaa8c98cf9f1538cbe2efd136fc6846b0`，apksigner 校验通过）；已用 `scripts/sync-apk.ps1` 同步到 VPS `/opt/dsh-remote/apk` 且远端 sha256 一致。
- 手机覆盖安装即可（同签名、versionCode 20）；Keystore 里的配对不受影响，无需重新配对。

### 会话列表隐藏动作透色修复（2026-11-10）：Arc 组件无缺陷，宿主绘制时机错误

- 现象：会话列表每行右侧、**未滑动（收起）**时能看到红色「归档」动作透出的颜色（右边缘约 1–3px 红边 + 右上/右下角红弧），预期收起态不应展示。
- 根因：`Views/SessionsView.axaml` 把 Danger 色 `Button.archive-action` 作为 `Border.row-card` 的同级下层元素**永远绘制**，收起态只靠卡片遮挡；卡片是圆角 + 1px 半透明描边，边缘/圆角存在抗锯齿半透明像素，下层危险色就从这圈接缝混出。手势实现（`SessionsView.axaml.cs`）只平移卡片，从未把动作按钮按手势状态隐藏。
- 对照结论：Arc `ArcSwipeActions` 收起态显式把两侧动作 host 置 `IsVisible=false`（`ApplyOffset`），不存在该问题 → **不是 Arc 组件库缺陷**；DSHRemote 当初自绘手势是因为 Arc 文档写明「行本身要响应点击（打开详情）时不要包滑动层」，属已知限制下的宿主自绘实现，问题出在自绘实现没有遵守「收起态不绘制动作」。
- 修复：`SessionsView.axaml` 归档按钮 `Opacity="0"` 起步；`SessionsView.axaml.cs` 的 `RowSwipeState` 持有动作按钮并提供 `SetActionPainted(bool)`，拖动开始/停靠打开时显示，回位、点击收起、关闭其他行时隐藏（不再依赖卡片遮挡）；隐藏态同时关闭 `IsTabStop`/`IsHitTestVisible`，避免焦点落在透明按钮上。Arc 组件与主题未改动。
- 验证：桌面壳 `--demo`（436x945，PrintWindow 截图）像素断言，卡片外圈条带危险红像素（R>=60 且 R-G>=25 且 R-B>=25）：修复前右 30 / 下 12 / 上 7、左 0（对照），修复后四边均 0；拖动中右 788 / 下 418 / 上 188（动作显示正常），松手回位后归 0。证据与 diff 见 `Arc.Avalonia/docs/analysis/swipe-leak/`，RCA/使用文档见 `Arc.Avalonia/docs/27-swipe-action-color-leak-rca.zh.md`、`docs/28-swipe-actions-integration.zh.md`。
- 待办：需重新构建并安装 APK 才能在真机生效（本次未发布新 APK）。

### 会话列表切换到 ArcSwipeActions（2026-11-10，v1.2.9）

- 背景：Arc.Avalonia 修复了 `ArcSwipeActions` 的「可点击行」缺陷（按下改为隧道阶段观察、横向锁定后才夺取指针；轻点到达 Content 内 Button/Tapped 表面，横拖仍由组件接管；Arc `docs/28 §2.7`，T1–T6 probe 全 PASS）。此前 DSHRemote 因该限制自绘滑动。
- 变更：`SessionsView.axaml` 行模板改为 `media:ArcSwipeActions`（`Trailing="{Binding SwipeActions}"`、`Label=Title`、`FullSwipe=False`）；行内容为整宽透明 `Button.session-row`（`OpenCommand`）；外观对齐 `row-card`（ArcSurface/ArcBorderSubtle/圆角18/padding14）。`SessionListViewModel.SwipeActions`（`ArcSwipeAction`，Tone=Danger，archive）由 `ArcSwipeActions` 消费。
- 删除：`SessionsView.axaml.cs` 的自绘手势状态机（约 190 行）与 `Button.archive-action` 样式；收起态动作由组件 `IsVisible=false` 处理，红边透色从根上消失。
- 验证：Core 115/115、App 14/14；Desktop/Android Debug 0/0；桌面 `--demo` 与模拟器演示数据：轻点打开会话、左滑露出「归档」、点归档 toast「已归档」且停留当前工作区、收起态四边危险色像素 0、crash buffer 空。
- 发布 APK：`dist/apk/dshremote-1.2.9-22-arm64-release.apk`（35,793,504 bytes，sha256 `13c89d2d99d238ecebdca5485c6699ef5bbdca9ab8238bbc13394980b518611a`，已同步 VPS 并校验）。
