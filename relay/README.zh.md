# dsh-remote-relay

DSH 宿主侧薄插件：在桌面进程内把「只监听 loopback 的官方 Web/Remote 服务」以 TLS 暴露到**一个**局域网/VPN 地址，并输出手机端配对信息。

它不实现业务语义，也不改动桌面 GUI 的绑定参数（桌面仍只监听 `127.0.0.1`）；它只是同一进程里的第二个监听 + 反代。

## 结构

```
relay/
  package.json          # "dsh": { "plugin": true }, main = src/index.mjs（ESM，零第三方依赖）
  src/index.mjs         # Cordis 插件入口：inject ['webServer','connection']
  src/proxy.mjs         # node:https TLS 反代：HTTP + WebSocket upgrade + loopback 辅助路由
  src/pairing.mjs       # SPKI SHA-256 / pairing URI / 原子写 pairing.json
  src/probe.mjs         # 启动协议自检（五端点探测 + DSH 版本解析）
  src/readiness.mjs     # 启动就绪等待（appReady）+ 自检退避重试（v0.6.1+）
  src/gateway.mjs       # 零依赖 WSS 客户端 + 网关连接器（隧道/HTTP/WS/心跳/重连）
  src/identity.mjs      # 远程身份 token/桌面名（~/.dsh/remote/identity.json，轮换）
  src/config.mjs        # 中继服务器配置（~/.dsh/remote/relay-config.json：校验/原子写/迁移）
  cordis.patch.yml      # bundle patch：insert id=remote-relay（默认配置；网关地址不放这里）
  src/pair-page.mjs     # 仅 loopback 的扫码配对页（HTML + 同源 QR 渲染脚本）
  src/downloads.mjs     # APK 目录扫描/白名单/sha256 缓存（下载接口的唯一数据源）
  src/download-page.mjs # LAN 可访问的 APK 下载页（无脚本，严格 CSP）
  src/session-summary.mjs # 手机紧凑会话列表（反代 session/list + projections 裁剪，v0.6.0+）
  assets/qrcode.js      # vendor: qrcode-generator 2.0.4（MIT，仅浏览器端使用）
  assets/LICENSE-qrcode-generator.txt
  certs/                # 自签证书（git 忽略；由 scripts/gen-tls.ps1 生成）
```

## 它做什么

1. `ctx.webServer.port` → 桌面进程内 web 服务的 loopback 端口（当前 54306）。
2. `ctx.connection.authenticatedUrl('http://127.0.0.1:<port>')` → 取出进程级 `launchToken`。
3. 用 `node:https` 在 `config.host:config.port`（默认 8443）+ `127.0.0.1:<同端口>` 上起 TLS 监听（**拒绝 0.0.0.0 / ::**），把请求反代到 `127.0.0.1:<port>`：
   - `Host` 重写为 `127.0.0.1:<port>`（cookie audience / Host-Origin 信任栅栏要求）
   - 客户端带 `Origin` 时改写为 `http://127.0.0.1:<port>`
   - **不解析、不改写 body**；cookie / Set-Cookie / 状态码 / `101 Switching Protocols` 原样透传
   - WebSocket：`upgrade` 事件双向管道（`/api/remote.mux`）
4. 写 `%USERPROFILE%\.dsh\remote\pairing.json`（tmp + rename 原子写），并 `console.log` 一行可复制的配对 URI。

额外提供以下辅助端点（不经过上游 DSH，summary 除外——它反代上游但只返回裁剪结果）：

- **`GET /__relay/health`**（无秘密）：relay/DSH 版本、自检结果、上游端口、证书指纹、APK 下载摘要。
- **`GET /__relay/sessions/summary`**（需令牌，v0.6.0+）：手机紧凑会话列表 + 按需详情，见「手机紧凑会话列表」节。
- **`GET /__relay/pair`**（仅 loopback）：扫码配对页，见下节。
- **`POST /__relay/probe`**（仅 loopback，v0.6.1+）：手动重跑启动自检（等 `appReady` + 退避重试），返回最新 `protocolProbe` 结果；配对页的「重新自检」按钮就是调它。
- **`GET /__relay/download`**（LAN 可访问）：APK 下载页/直链，见「APK 下载页」节。

health 返回示例：

```jsonc
{
  "ok": true,
  "relayVersion": "0.2.0",
  "dshVersion": "0.2.2-rc.1",              // 从打包 app.asar / profile 解析；拿不到则 "unknown"
  "dshVersionSource": "…\\app.asar\\dsh\\node_modules\\@deepseek-ai\\dsh\\package.json",
  "upstreamPort": 54306,
  "fp": "<SPKI sha256 hex>",
  "startedAt": "…",
  "protocolProbe": {
    "status": "ok",                         // ok | failed | pending
    "required": ["$connect", "session/list", "session/events", "$events", "session/follow"],
    "capabilities": { "$connect": true, "session/list": true, "session/events": true,
                      "session/follow": true, "$events": true },
    "missing": [], "errors": [], "checkedAt": "…", "sessionSample": "session-…"
  }
}
```

### 启动自检（M4）

启动时（TLS 监听成功后）对 loopback 上游做一次协议探测，结果写入 health 与 `pairing.json`：

- `$connect`：`GET /connect?token=…&interactions=answer` → 302 + Set-Cookie
- `session/list`：`POST /api/session/list` `{"args":{"_request":{}}}`
- `session/events`：`{"args":{"request":{address:{kind:'session',sessionId},maxMessages:1}}}`
- `$events`：在 **同一条** `/api/remote.mux` 连接上 `open`，等待 `{"type":"item","value":{"type":"ready"}}`
- `session/follow`：同一条 mux 上 `open`，收到首帧即视为端点存在（`session/*` 错误算可用，`gateway/*` 算不可用）

实现要点：一个 API socket 只对应一个 Peer，**所有流探测必须复用同一条 mux**，否则关闭首条流会让 cookie 失效（首页探测会因此误报 401）。探测端点见 `src/probe.mjs`。

**启动就绪等待与重试（v0.6.1+）**：DSH 的 `/api/remote.mux` upgrade 路由要到 launcher 提交 `appReady`（整棵插件树 boot 完成）之后才注册；在此之前宿主 webserver 对未注册的 upgrade 路径直接 `socket.destroy()`，客户端表现为 `socket hang up`。relay 插件正是在 boot 过程中被 apply 的，所以 v0.6.0 的“一次性自检”在冷启动时可能先于 `appReady` 命中该窗口，把 `$events`/`session/follow` 永久标成不可用（配对页从此显示 `active=false`，即使几秒后端点已经健康）。v0.6.1 起自检改为：

- 先等 `ctx.get('appReady').onReady()`（上限 60 s；非 launcher 宿主没有该服务则立即继续）；
- 只对可瞬时恢复的失败（`mux upgrade`/`socket hang up`/ECONNRESET/`gateway/*` 等）做退避重试（默认 5 次，间隔 0.75/1.5/3/6 s）；鉴权/硬不兼容失败不重试；
- 结果落定后再 `publish()`；配对页提供「重新自检」按钮（`POST /__relay/probe`），无需重启 DSH。

自检失败时：`pairing.json` 立即写成 `active:false` + `error:{code:'protocol-self-check-failed', missing, errors}`，日志打印 `[remote-relay] ERROR ...`；relay 本身继续服务，便于从 health 诊断（不静默降级）。启动过程中先写 `active:false, error.code=self-check-pending` 的临时 pairing，探测完成后才写最终结果；配对文件的删除/重写都是原子的。

## 扫码配对（loopback 页面，M3）

```powershell
pwsh -File scripts/show-pairing.ps1            # 检查 relay → 打开默认浏览器
pwsh -File scripts/show-pairing.ps1 -NoBrowser # 只打印 URL / 端口可 -Port 8443
```

页面地址：`https://127.0.0.1:<port>/__relay/pair`（自签证书，浏览器提示不安全时选「高级 → 继续访问」）。

- **内容**：当前 pairing 的二维码（内容 = `pairingUri`，含 host/port/token/fp）、`host:port` 文本、SPKI 指纹、relay/DSH 版本、pairing 更新时间、token 掩码、过期提示；`active:false` 时显示失败原因而不是二维码。页面上另有「重新自检」按钮（`POST /__relay/probe`，v0.6.1+），自检通过后自动刷新出二维码。
- **仅 loopback**：按 TCP `remoteAddress` 判定（不看转发头）；非 127.0.0.1/::1 一律 **404**。配对 token 只出现在本机页面里，不会经局域网暴露。
- **安全头**：`Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`、`Cache-Control: no-store`、`Referrer-Policy: no-referrer`、`X-Frame-Options: DENY`、`X-Content-Type-Options: nosniff`。
- **无内联脚本**：二维码由同源脚本 `/__relay/pair.js` 读取页面 `data-pairing-uri` 后绘制，QR 库来自 `/__relay/qrcode.js`（vendor 的 `qrcode-generator@2.0.4`，MIT，见 `assets/LICENSE-qrcode-generator.txt`）。relay 运行时仍零第三方依赖：这两个文件只作为静态字节服务，loopback 之外同样 404。
- **手机端**：用系统相机/扫码扫描 → `dshremote://pair?…` 深链拉起 DSH Remote App（Android 头已注册 intent-filter）；App 解析 URI 里的 host/port/token/fp 即可连接。
- **过期**：token 是桌面 DSH 进程级随机值，桌面重启后失效；relay 会重写 pairing，刷新本页（或重跑 `show-pairing.ps1`）即可。
- **日志**：relay 不记录配对页请求的内容，也不打印 token（`console.log` 的配对 URI 一行除外，该行是刻意的可复制输出）。

## APK 下载页（LAN，M5）

手机无需 adb 即可从服务器下载安装最新版：

```powershell
pwsh -File scripts/show-download.ps1          # 打印 LAN 下载 URL + 最新版本/大小/sha256
pwsh -File scripts/show-download.ps1 -Json    # 机器可读
pwsh -File scripts/show-download.ps1 -Open    # 本机浏览器打开
```

- **URL**：`https://<host>:<port>/__relay/download`（LAN/VPN 可访问；**无需 token**）。直链：`/__relay/download/<file>.apk`。
- **页面**：最新版卡片（版本/版本号/文件名/大小/sha256/更新时间 + 下载按钮）、历史版本表、安装提示（ColorOS「安装增强防护」：更多 → 开始深度扫描 → 继续安装）、配对说明、relay/DSH 版本；无 APK 时显示构建命令。
- **自动扫描**：每次请求重新扫描下载目录，新构建的 APK 无需重载 relay 即可出现；最新版 = 版本号（`dshremote-<ver>-<code>-...` 的 code）最大，其次 semver、mtime。
- **目录解析顺序**：`config.downloadDir` → 环境变量 `DSH_REMOTE_APK_DIR` → `<repo>/dist/apk` → `~/.dsh/remote/apk`。
- **严格白名单**：只发 `fs.readdirSync` 直接扫描到的 `^[A-Za-z0-9._-]+\.apk$` 常规文件；跳过符号链接；请求名再次校验且 `realpath` 必须仍在目录内（防 `..`/子目录/链接逃逸）；其它路径（含 `relay/certs/*`、任意非 APK）一律 404。不提供目录列表。
- **响应头**：`content-type: application/vnd.android.package-archive`、`content-disposition: attachment`（含 `filename*`）、`content-length`、`x-content-sha256`、`cache-control: no-store`、`nosniff`；大文件用 `fs.createReadStream` 流式发送；**支持 HEAD**（仅头，无 body）。
- **health / pairing**：`/__relay/health` 与 `pairing.json` 均带 `download` 字段：`{dir, url, count, latest:{file,version,versionCode,sizeBytes,sha256,updatedAt,path}}`。health 实时重扫（哈希按 path+mtime+size 缓存），pairing 为启动时快照。
- **安全边界**：下载页只暴露 APK；既有 `/__relay/health`、仅 loopback 的 `/__relay/pair` 与反代路由互不影响；仍零第三方运行时依赖。

## 手机紧凑会话列表（v0.6.0+）

手机首页只渲染 11 个字段，而上游 `session/list` 每条会话都带 projections（`inbox`、`permissions`、
`tokenUsage`、`todos`、`agentTeam` 快照……），实测列表 1.06MB 里约 92% 是列表用不到的数据。
relay 新增一个手机可达端点：用**手机请求自带的 `dsh-auth` cookie** 反代上游
`POST /api/session/list`（官方一元信封 `{"args":{"_request":{}}}`），再裁剪成紧凑 JSON。

- **URL**：`GET /__relay/sessions/summary[?sessionId=<id>][&includeSubagents=1]`
- **鉴权**：`Authorization: Bearer <remoteToken|launchToken>`，与 `/__relay/acl/*` 完全相同
  （复用 token 列表 + 时序比较）。TLS 监听与网关内部明文代理都提供；loopback 免令牌只对
  `helpers==='all'` 的公开监听生效，**网关内部明文代理不享受**。
- **列表响应**（`schema:1`，客户端忽略未知字段，后续可加字段）：

  ```json
  {
    "ok": true,
    "schema": 1,
    "fields": ["sessionId", "updatedAt", "running", "blank", "agentAvailable",
               "title", "cwd", "model", "lastPromptAt", "parentSessionId", "origin"],
    "generatedAt": "2026-11-12T05:00:00.000Z",
    "cursor": null,
    "counts": { "total": 241, "hiddenSubagents": 12, "returned": 229 },
    "items": [
      { "sessionId": "session-…", "updatedAt": 1794154489599, "running": true,
        "blank": false, "agentAvailable": true, "title": "排查DSH运行系统卡顿",
        "cwd": "C:\\Code\\repo", "model": "deepseek-v4.1-flash",
        "lastPromptAt": 1794154489599, "parentSessionId": null, "origin": null }
    ]
  }
  ```

  字段来源：`title`←`projections.values.title`；`model`←`projections.values.modelSelection.lastUsed.model`
  （回退 `next.model`）；`cwd`←`cwd`（回退 `projections.values.workingDirectory`）；
  `lastPromptAt`←`projections.values.sessionListMetadata.lastPromptAt`；
  `blank` 在 `blank` 缺省时回退 `sessionListMetadata.blank`。
  **默认过滤 subagent**（`parentSessionId` 非空）；`includeSubagents=1`（也接受 `true`）保留，
  `counts.total` 始终是上游总数。
- **详情扩展点**：`?sessionId=<id>` 原样返回该会话的上游 item（**不裁剪**，给后续按需详情留扩展点）：

  ```json
  { "ok": true, "schema": 1, "generatedAt": "…", "item": { "sessionId": "…", "projections": { … } } }
  ```

  会话不存在 → **404**。
- **错误**：`401 unauthorized`（缺少/错误令牌）、`405 method not allowed`（非 GET/HEAD）、
  `502 upstream-failed`（上游非 200、`result.ok:false`、超时 8s 或非法 JSON）。
- **实现**：`src/session-summary.mjs`（`createSessionSummaryApi` + 纯函数
  `trimSessionSummary` / `buildSummaryPayload` / `findSessionItem`）；`src/proxy.mjs` 只负责
  路由/鉴权/响应；`src/index.mjs` 注入 `sessionSummaryApi` 并同时挂到 TLS 监听与网关内部
  明文代理。日志不打印 cookie/token。单测 `test/session-summary.test.mjs`（假 upstream server）。

## 桌面端自动审批（v0.7.1+）

手机 App 关闭时审批不再挂起：relay 在宿主进程内注册 `approval/request` waterfall 监听器
（`{ prepend: true }`），命中规则时直接返回 `'allowed-once'`，不命中则 `next()` 交回原有链路
（桌面 Web UI / 手机）。因为排在 `api-remotes` 的转发监听器之前，命中时两边都不会弹审批。

- **规则文件**：`~/.dsh/remote/auto-approve.json`（可用 `config.autoApprove.file` 覆盖；原子写
  `0600`；目录监听热加载）。`schemaVersion:2`，字段 `enabled` / `all` /
  `workspaces[]`（`id,path,title`）/ `sessions[]`（`id,title,cwd,parentSessionId`）/ `revision`。
  手机端的树形勾选映射为：全部工作区→`all:true`；完全勾选的工作区→`workspaces[]`（覆盖以后新建会话）；
  部分勾选的工作区→只写勾选的 `sessions[]`。旧 v1 `mode` 文件在加载时自动迁移。
- **匹配**：
  - `all:true`：所有会话；
  - `sessions`：会话 id 命中，或沿 `header.parentSession` 向上回溯命中（最多 8 层），因此所选会话的
    所有子代理/孙代理都覆盖；
  - `workspaces`：用 `request.agent.session.header.cwd` 经 `ctx.workspaceRegistry.resolveByPath()`
    解析 workspace id 命中（registry 不可用时退回规范化路径比较，Windows 不区分大小写）；
  - 规则损坏、匹配异常、请求已中止 → 不自动审批（fail-closed），继续人工处理。
- **API**（TLS 监听与网关内部明文代理都提供；鉴权同 ACL API）：
  - `GET /__relay/auto-approve` → 当前规则 + `revision` + `stats` + 最近 50 条内存审计；
  - `PUT/POST /__relay/auto-approve` → 完整规则文档，校验后写盘并返回新文档；
  - `401` 未授权、`400` 校验失败、`405` 方法不支持。
- **health**：`health.autoApprove` 暴露 `enabled/all/workspaces/sessions/revision/stats/error/file`。
- **安全**：只返回 `allowed-once`，永不自动拒绝、永不处理 `user-questions/request`；每次放行打日志
  `[remote-relay] auto-approve allowed session=… tool=… rule=…`。
- **实现**：`src/auto-approve.mjs`（store/matcher/service/API），`src/proxy.mjs` 路由鉴权，
  `src/index.mjs` 生命周期 + health；单测 `test/auto-approve.test.mjs`（20 条）。

## 配置

### 1) 监听参数（LAN TLS）——profile patch

`~/.dsh/profiles/desktop/cordis.patch.yml` 里由 `scripts/dev-install.ps1` 维护的 insert 条目：

```yaml
- insert:
    - id: remote-relay
      name: dsh-remote-relay
      config:
        host: 192.0.2.10          # 只绑这个具体地址；缺省自动取默认路由网卡的 IPv4
        port: 8443
        tls:
          certFile: 'C:\Users\<you>\.dsh\remote\certs\relay-cert.pem'
          keyFile: 'C:\Users\<you>\.dsh\remote\certs\relay-key.pem'
        # name: 自定义展示名（默认计算机名）
        # token: 仅调试用；缺省从 authenticatedUrl 自动发现
        # pairingFile: 覆盖 pairing.json 路径（默认 ~/.dsh/remote/pairing.json）
        # downloadDir: APK 下载目录（默认 <repo>/dist/apk；也可设 DSH_REMOTE_APK_DIR）
```

改监听参数 = 重跑 `scripts/dev-install.ps1 -BindHost <ip> -Port <port>`（会先备份 patch 文件，再更新受管块并追加 reload tick 触发 HMR）。

### 2) 中继服务器（公网网关）——持久化 JSON（v0.4.0+）

公网中继服务器地址**不再写死**在 profile patch 里，而是持久化到
`~/.dsh/remote/relay-config.json`（`DSH_HOME`/`DSH_REMOTE_CONFIG` 可覆盖路径）：

```json
{
  "version": 1,
  "updatedAt": "2026-11-10T13:00:00.000Z",
  "initializedAt": "2026-11-10T13:00:00.000Z",
  "initializedBy": "one-time-migration:profile-patch",
  "gateway": {
    "enabled": true,
    "host": "203.0.113.10",
    "port": 9443,
    "path": "/__gateway/tunnel",
    "secure": true,
    "fingerprint": "<网关证书 SPKI SHA-256，可空>",
    "name": "DSH Remote Gateway"
  }
}
```

- `host` = 中继服务器 IP/域名；`port` = TLS 端口；`path` = 可选 API 路由端点（反代前缀也可，如 `/dsh/__gateway/tunnel`）；
  `fingerprint` 留空时首次连接自动信任（TOFU）。`gateway.enabled=false` 表示只用 LAN。
- 三种修改方式（**保存后立即重连，无需 DSH 重启 / 改 YAML**）：
  1. `https://127.0.0.1:8443/__relay/pair` →「中继服务器设置」表单；
  2. `pwsh -File scripts/relay-config.ps1 -Host <IP/域名> -Port <端口> [-Path ...]`（不带参数=查看；`-Disable`=关闭）；
  3. 直接编辑 JSON 文件：插件监听该文件，保存后自动重载。
- `GET/POST /__relay/config`（仅 loopback）：查看/保存配置的 JSON API（POST 必须 `content-type: application/json`，
  否则 415；可选 `initializedBy` 只在文件首次创建时记为初始化标记）。
- **优先级**：`relay-config.json` > profile patch 的 `config.gateway` > 默认（未启用）。
- **一次性初始化**：首次加载时若 JSON 不存在、而 patch 里还有 gateway 配置，插件会把旧值写入 JSON
  （`initializedBy=one-time-migration:profile-patch`）；`install-relay-bundle.ps1` 随后清掉 patch 里的 gateway 段落，
  让 JSON 成为唯一可编辑来源（`-NoSeedConfig` 可跳过）。

### 3) Windows 工作区权限守卫（v0.5.0+）——可选 `config.aclGuard`

插件在宿主进程内（用户令牌，不受沙箱限制，**不触发审批**）保证"新会话的工作区能被 DSH 沙箱正常授权"：
只给会话工作区根补一条**可继承**的用户完全控制 ACE，后续 capability ACE 与 Low 标签仍由 DSH 自己写。

- 触发点：`session/created`（预热）+ `tools/pre-execute`（首次工具执行前等待就绪，上限可配）+ 工具结果里出现 ACL 失败指纹时定点复查。
- 范围：只处理**被新会话实际用到**的工作区；每个工作区只准备一次（缓存于 `~/.dsh/remote/acl-admission.json`），之后 <10ms 放行。
- 副作用（模式 A，默认）：Windows 会把可继承 ACE 传播到该工作区已有子对象 —— 这正是"顺带补齐该工作区存量权限"。
- 排除：UNC/网络盘、系统目录（`C:\Windows`、`Program Files`、`ProgramData`）、根为 junction 的路径 → 只提示不改。
- 审计：每次 ready/repaired/skip/timeout 追加到 `~/.dsh/remote/acl-audit.jsonl`。

```yaml
- id: remote-relay
  config:
    # ... 其它配置 ...
    aclGuard:
      enabled: true          # 关闭整个守卫
      waitMs: 30000          # 首次工具调用最多等待准入准备的毫秒数；超时先放行、后台继续
      probeTimeoutMs: 8000   # 单次 PowerShell 探测超时
      repairTimeoutMs: 120000 # 单次 icacls 修复超时（大工作区传播可能较慢）
      cacheTtlHours: 24      # 持久化缓存有效期；过期后新会话会重新探测
      propagate: true        # 模式 A；false 尚未实现（会回退并打印提示）
      excludedPrefixes: []   # 额外排除的工作区根前缀
```

验证：

```powershell
cd relay
npm test                 # 含 acl-guard 单测（无需管理员）
npm run smoke:acl        # 端到端：在 %TEMP% 复现缺陷→修复→验证子对象继承（必须在非受限环境执行）
```

手机端 API（v0.5.1+，由 relay 自身提供，手机设置页「Windows 权限」卡片使用）：

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| GET | `/__relay/acl/status` | Bearer 配对令牌；本机 loopback 管理页免令牌 | 返回 guard 状态与已知工作区（不探测） |
| POST | `/__relay/acl/check` | 同上 | body `{ "repair": true, "root": "可选" }`；体检并（默认）修复已知工作区 |

- 令牌 = 手机配对时使用的凭据（网关模式为 remote token，LAN 模式为 launch token）；relay 同时接受两者。
- 只允许体检 relay 已知的工作区（会话用过的 + 会话列表中的），不接受任意路径。
- 网关的内部 loopback 转发**不**继承“本机免令牌”信任，必须带 Bearer。

## 生命周期 / 热重载

- 监听由 `ctx.effect()` 管理：HMR 重载、profile 重组、进程退出都会先 dispose 旧监听再重新绑定（端口占用有短重试）。
- **首次安装**：`dev-install.ps1` 建 junction + 追加 patch 条目 + 追加注释 tick，HMR 在不重启桌面的情况下加载插件。
- **改 relay 源码后**：源码单独变化不触发重载（已链接包文件在重组时比较），需再跑一次 `dev-install.ps1`（tick 变化触发重组）；用 `-RequireReload` 可断言确实换成了新实例。
- **桌面重启后**：`webServer.port` 与 `launchToken` 都会变；插件在进程启动时重新读取并重写 pairing.json（启动时先删除旧的 pairing，成功后再写，避免手机读到失效 token）。
- 证书/私钥只在启动时读取；换证书后需触发重载（重跑 `scripts/gen-tls.ps1` 再 `dev-install.ps1`）。

## 自签 TLS 与指纹 pinning

- 证书由 `scripts/gen-tls.ps1` 生成：`New-SelfSignedCertificate`（SAN: `localhost`、计算机名、LAN IPv4、`127.0.0.1`）→ 临时 PFX → openssl 转 PEM。
- 客户端**不依赖系统信任库**，改为 pin `pairing.json` 的 `fp`：证书 SubjectPublicKeyInfo 的 SHA-256（64 位小写 hex）。
- 只换证书、不换私钥时 `fp` 不变；换密钥会使 `fp` 变化，手机需重新配对。

## pairing.json

```json
{
  "schemaVersion": 1,
  "active": true,
  "host": "192.0.2.10",
  "port": 8443,
  "token": "<launchToken>",
  "fp": "<spki sha256 hex>",
  "pairingUri": "dshremote://pair?v=1&host=..&port=..&token=..&fp=..&name=..",
  "name": "DESKTOP-XXXX",
  "updatedAt": "2026-11-08T16:24:50.761Z",
  "protocol": "https",
  "relayVersion": "0.2.0",
  "dshVersion": "0.2.2-rc.1",
  "capabilities": { "$connect": true, "session/list": true, "session/events": true,
                    "session/follow": true, "$events": true },
  "probe": { "ok": true, "checkedAt": "…", "missing": [], "errors": [], "sessionSample": "session-…" },
  "download": {
    "dir": "D:\\Code\\VibeCoding\\DSHRemote\\dist\\apk",
    "url": "/__relay/download",
    "count": 3,
    "latest": {
      "file": "dshremote-1.0.1-11-arm64-release.apk",
      "version": "1.0.1",
      "versionCode": 11,
      "sizeBytes": 35416600,
      "sha256": "74cc8e12…496f",
      "updatedAt": "2026-11-09T00:21:30.781Z",
      "path": "/__relay/download/dshremote-1.0.1-11-arm64-release.apk"
    }
  },
  "loopbackPort": 8443,
  "upstreamPort": 54306
}
```

自检失败时额外带 `"active": false` 与 `"error": { "code": "protocol-self-check-failed", "message": …, "missing": [...], "errors": [...] }`。

手机端消费者只需把 `pairingUri`（或二维码）交给 App：`host`/`port` 给连接地址，`token` 用于 `GET /connect?token=..&interactions=answer`，`fp` 用于 pin 证书；`active`/`capabilities` 用于在配对前/连接失败时给出可读诊断（Core 会忽略不认识的字段）。

## 脚本

| 脚本 | 作用 |
|---|---|
| `scripts/gen-tls.ps1` | 生成自签证书 → 默认 `~/.dsh/remote/certs/relay-cert.pem` + `relay-key.pem`（`-OutDir` 可指定；旧 `relay/certs/` 仍可用），打印 SPKI 指纹 |
| `scripts/dev-install.ps1` | 工作树 → profile：junction + 受管 patch 块（先备份）+ HMR 热载 + 健康等待；`-RequireReload` 断言重启 |
| `scripts/package-relay.ps1` | 打包 `dist/dsh-remote-relay-<version>.zip`（不含 certs）+ `.sha256` + 包内 INSTALL.md |
| `scripts/install-relay.ps1` | zip/目录 → `$DSH_HOME/plugins/dsh-remote-relay` + 证书复用/生成 + junction + patch + 热载；`-WhatIf`、`-NoReload` |
| `scripts/uninstall-relay.ps1` | 移除受管 patch 块/junction（可选插件目录、pairing），热卸载并等待端口释放；幂等 |
| `scripts/relay-smoke.ps1` | 读 pairing.json：health + `--pinnedpubkey` 验 fp + pairing active/capabilities + `/connect` 302+cookie + `session/list` 200 + 可选 WSS 101/`$events` ready |
| `scripts/show-pairing.ps1` | 确认 relay 在跑（可选 `-AutoInstall`），打印配对摘要，打开 loopback 扫码页 `https://127.0.0.1:<port>/__relay/pair`（`-NoBrowser` 只打印 URL） |
| `scripts/show-download.ps1` | 打印 LAN APK 下载页 URL 与最新 APK（版本/大小/sha256）；`-Json` 供脚本消费，`-Open` 打开本机浏览器 |
| `scripts/package-relay-plugin.ps1` | 构建 DSH bundle 产物：`dist/dsh-remote-relay-<ver>/` + `.tgz`（插件页可见） |
| `scripts/install-relay-bundle.ps1` | 迁移 dev insert → 宿主 `pluginManager/installBundle` 安装 bundle；校验 `listBundles`/health 自检，失败自动回滚 |
| `_scratch/relay/ws-check.mjs` | WS 帧级验证脚本（`relay-smoke.ps1 -IncludeWs` 会调用） |

共用逻辑在 `scripts/relay-common.ps1`（profile/patch 块/junction/健康等待），三个安装脚本行为一致。

## 打包 / 安装 / 卸载（M4）

```powershell
pwsh -File scripts/package-relay.ps1
pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-0.2.0.zip -BindHost 192.0.2.10
pwsh -File scripts/uninstall-relay.ps1
```

- 版本与兼容声明：`package.json` 的 `version`、`engines.dsh`（`>=0.2.2-rc.1`）、`files`（打包白名单）。
- zip 不含证书/私钥：目标机安装时复用已装证书，或复制仓库证书，或调用 `gen-tls.ps1` 生成。
- `package.json` 版本变化会让文件内容变化 → profile 重组时插件被替换；仅注释/配置不变时不会重启（`dev-install.ps1 -RequireReload` 可断言）。

## 公网网关 + v2 配对（M6）

手机不在同一局域网时，relay 主动向公网网关建 WSS 隧道（协议细节见 [docs/remote-v2.md](../docs/remote-v2.md)）。

配置（**v0.4.0 起**：`~/.dsh/remote/relay-config.json`，见上文「配置 → 2)」；下列 profile patch 写法仅作向后兼容回退）：

```yaml
- id: remote-relay
  config:
    name: 'DSH Desktop'
    gateway:
      enabled: true
      host: 203.0.113.10            # 或 url: 'wss://203.0.113.10:9443/__gateway/tunnel'
      port: 9443
      path: '/__gateway/tunnel'
      name: 'DSH Remote Gateway'
      fingerprint: '0123456789abcdef…456789abcdef'     # 网关证书 SPKI SHA-256（可选，缺省 TOFU 并写入 pairing）
      # token: '<32 hex>'               # 可选；缺省自动生成并持久化到 ~/.dsh/remote/identity.json
      # secret: '<register secret>'     # 可选；网关要求时填
```

- 网关模式下额外在 `127.0.0.1:0` 起**仅 loopback** 明文反代（连接器转发入口；只额外开放 `/__relay/health`，
  pair/download 页面不经公网暴露）；LAN TLS 监听保持不变。
- 启动时 GET `<网关>/__gateway/identity`（路径由 `gateway.path` 推导：末尾 `/tunnel` → `/identity`）取证书 fp
  （有 `gateway.fingerprint` 则强校验，否则 TOFU 记录）；连接器异常时 `gateway.usable=false`，pairing 回退 v1（LAN 不受影响）。
- pairing v2：顶层 `host/port/token/fp` = 网关地址 / remoteToken / 网关证书 fp；保留 `localHost/localPort/localFp/localPairingUri`；
  `gateway:{url,online,fp,usable,error}`。`schemaVersion=2`。
- 管理页 `https://127.0.0.1:8443/__relay/pair`：桌面名、服务器地址、远程 Token（掩码/显示/复制）、v2 二维码、
  「生成新 Token」（`POST /__relay/pair/rotate`，仅 loopback；轮换 identity → 连接器重连 → 立即重写 pairing，旧 token 网关 404）。
- health 增
  `gateway:{enabled,host,port,path,secure,url,name,source,configFile,configError,online,fp,usable,error,internalPort,connectedAt,reconnects,tokenMasked}`
  与 `identity:{file,name,tokenMasked,createdAt,rotatedAt}`；`pairing` 摘要字段。

## 作为 DSH bundle 安装（M6）

DSH「设置 → 插件」只列 bundle，所以 relay 提供 bundle 形态（不再依赖手写 patch insert 才可见）：

- `package.json`：`"dsh": { "plugin": true, "bundle": { "patch": "./cordis.patch.yml" } }`，
  `exports` 提供 `"."`/`"./dsh"`/`"./package.json"`；`files` 含 `cordis.patch.yml`。
- `cordis.patch.yml`：`- insert: [{ id: remote-relay, name: dsh-remote-relay }]`（默认配置；LAN IP/8443 由插件默认值决定；
  公网中继地址在 `relay-config.json`，不在 patch 里）。
- **TLS 默认路径**：`~/.dsh/remote/certs/relay-cert.pem` / `relay-key.pem`（bundle 不含证书；`gen-tls.ps1` 默认输出到该目录）；
  缺失时插件报清晰错误并提示运行 `scripts/gen-tls.ps1`；`tls.certFile/keyFile` 优先。
- 打包：`scripts/package-relay-plugin.ps1` → `dist/dsh-remote-relay-<ver>/` + `.tgz`。
- 安装：`scripts/install-relay-bundle.ps1`（迁移 dev insert → 宿主 `pluginManager/installBundle`，校验 listBundles + health，
  失败回滚）。**不手写 profile package.json、不直接跑 CLI 安装**（桌面 profile 由 Electron 管理）。

## 已知约束

- 只允许绑具体 IP；`0.0.0.0`/`::` 会被拒绝（与 `dsh-host-webserver` 的 wildcard 拒绝策略一致）。
- 手机可达性还取决于 Windows 防火墙：当前机器为「公用网络」，`DeepSeek Harness.exe` 已有 Public 入站 Allow 规则；若换网络/换机导致被拦，需要为入站 8443 或该程序加规则。
- relay 不额外做访问控制：安全性来自「TLS + launchToken（仅出现在 pairing）+ 短生命周期 cookie」。`/__relay/health` 不含秘密。
- 一个进程只有一个 relay 实例（同一端口 + loopback 镜像）；不支持多地址同时监听。




