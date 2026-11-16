# Remote v2：公网网关配对与连接器协议（M6）

M6 让手机不在同一局域网时也能连到桌面 DSH：桌面 relay 主动向公网网关建立一条 WSS 隧道，
手机改为连网关。LAN TLS 直连（v1）继续保留并可同时使用。

```
手机 ──TLS(pin fp)──► 公网网关 :9443 ──WSS 隧道──► 桌面 relay ──loopback──► DSH
        remoteToken                连接器帧           内部明文代理
```

- 公网网关：`vps/`（gateway-dev，systemd `dsh-remote-gateway`），示例部署 `https://203.0.113.10:9443`。
- 桌面 relay：`relay/`（本仓库），网关模式下额外起一个 **仅 loopback** 的明文反代，连接器把网关请求
  转发进去，复用既有的 Host/Origin 归一化逻辑。
- **中继服务器地址（v0.4.0+）**：由用户手动填写 IP/域名 + 端口 + 可选 API 路由端点，持久化在
  `~/.dsh/remote/relay-config.json`，可用管理页 `https://127.0.0.1:8443/__relay/pair`、
  `scripts/relay-config.ps1` 或直接编辑文件修改，保存后立即重连（见 [relay-config.md](relay-config.md)）。
- 手机只认识 `dshremote://pair?v=2&...`：`host/port` 是网关地址，`token` 是 **remoteToken**，
  `fp` 是 **网关证书** SPKI SHA-256（不是桌面自签证书）。

## 1. 身份与配对文件

桌面远程身份持久化在 `%USERPROFILE%\.dsh\remote\identity.json`：

```json
{ "version": 1, "token": "<32 位小写 hex>", "name": "DSH Desktop",
  "createdAt": "...", "updatedAt": "...", "rotatedAt": "..." }
```

- token 首次启动自动生成（16 字节随机 → 32 hex），重启不变；`POST /__relay/pair/rotate` 轮换。
- relay 配置 `gateway.token` 可显式指定（会写回 identity.json；大小写会归一小写）。

网关模式下 `pairing.json`（schemaVersion=2）示例：

```json
{
  "schemaVersion": 2,
  "mode": "gateway",
  "active": true,
  "host": "203.0.113.10",
  "port": 9443,
  "token": "<remoteToken>",
  "fp": "<网关证书 SPKI sha256>",
  "pairingUri": "dshremote://pair?v=2&host=203.0.113.10&port=9443&token=…&fp=…&name=DSH+Desktop&mode=gateway",
  "name": "DSH Desktop",
  "gateway": { "url": "wss://203.0.113.10:9443/__gateway/tunnel", "online": true,
               "fp": "<网关证书 SPKI sha256>", "usable": true },
  "localHost": "192.0.2.10", "localPort": 8443, "localFp": "<桌面 relay 证书 fp>",
  "localPairingUri": "dshremote://pair?v=1&host=192.0.2.10&port=8443&token=<launchToken>&fp=<relay fp>&name=…",
  "capabilities": { … }, "probe": { … }, "download": { … }
}
```

- 顶层字段 = 网关（公网）；`local*` = LAN 直连（v1，token 是 DSH launchToken）。
- `scripts/relay-smoke.ps1` 默认走 LAN（`localHost/localPort/localFp` + localPairingUri 里的 launchToken），
  `-Gateway` 走 v2 公网；`scripts/show-download.ps1`、`show-pairing.ps1` 自动读 pairing。
- 网关不可用（自检/身份失败）时 `gateway.usable=false`，relay 回退写 v1 pairing（LAN 仍可用），
  health/pairing 的 `gateway.error` 给出原因。
- 中继服务器地址（v0.4.0+）来自 `~/.dsh/remote/relay-config.json`：health 中
  `gateway.{host,port,path,source,configFile,configError}` 描述来源与生效值（`source=file` 表示已用 JSON 配置，
  `patch` 表示旧的 profile patch 回退）。

## 2. 连接器注册与帧协议

连接器启动后向 `gateway.url` 发起 WSS：

```
GET /__gateway/tunnel?token=<remoteToken>&name=<desktopName>&launchToken=<DSH launchToken>[&secret=…]
```

- `token` 必须是 32 位小写 hex；`secret` 用于网关侧 `DSH_GATEWAY_REGISTER_SECRET` 可选校验。
- 同一 token 重新注册替换旧隧道；隧道断开后网关 30min 内保留 record（`/connect` 返回 `desktop-offline`）。
- 心跳：连接器每 **20s** 发 `{"kind":"ping"}`，网关回 `{"kind":"pong"}`；网关 60s 无帧判离线。
- 断线指数退避重连（1s→2s→…→30s）。

帧（全部 JSON 文本帧）：

| 方向 | 帧 |
|---|---|
| gateway→connector | `{"id","kind":"http","method","url","headers","bodyB64"}` + `{"id","kind":"http-end"}` |
| connector→gateway | `{"id","kind":"http-res","status","headers","bodyB64"}` + `{"id","kind":"http-end"}` |
| gateway→connector | `{"id","kind":"ws-open","url","headers"}` |
| connector→gateway | `{"id","kind":"ws-open-ok"}` / `{"id","kind":"ws-open-fail","status":<http>}` |
| 双向 | `{"id","kind":"ws-msg","b64","op"?}`（op: 1=text/2=binary） |
| gateway→connector | `{"id","kind":"ws-close"}` |
| 双向 | `{"kind":"ping"}` / `{"kind":"pong"}` |

- 网关把手机请求 URL 里的 **remoteToken 替换成 launchToken** 后再发给连接器；连接器转发到内部 loopback 明文代理，
  由既有 Host/Origin 归一化逻辑送到 DSH（因此 DSH 侧 cookie authority 仍是 `127.0.0.1:<port>`）。
- 响应 Set-Cookie（`dsh-auth-*`）由网关按 token 映射回桌面；后续 `/api/*`、`/__relay/*` 凭 cookie 路由。
- 内部代理只额外开放 `/__relay/health`（无秘密）；`/__relay/pair` 与 `/__relay/download` 仍只在 LAN TLS 监听上，
  不会经公网暴露配对 token。
- 限额（网关侧）：HTTP body 32MiB（base64 ≈43MiB）、WS 单消息 16MiB、响应 64MiB；首版全 JSON base64，后续可流式化。

## 3. 管理页（仅 loopback）

`https://127.0.0.1:8443/__relay/pair`（`scripts/show-pairing.ps1` 打开）：

- 桌面名、公网服务器地址、网关在线状态、`remoteToken`（默认掩码，可显示/复制）、v2 二维码
  （内容 = 当前 `pairingUri`）、LAN 配对 URI/指纹；
- 按钮：「生成新 Token」（`POST /__relay/pair/rotate`，仅 loopback）→ 轮换 identity token →
  连接器用新 token 重连 → 立即重写 pairing（旧 token 在网关上 404），页面刷新后显示新二维码；
  「复制配对信息」。
- 页面无外链脚本，QR 用 vendored `qrcode-generator`；CSP：`default-src 'none'; script-src 'self'; connect-src 'self'; …`。

## 4. 安装为 DSH bundle（插件页可见）

DSH「设置 → 插件」只列 **bundle**（`package.json.dsh.bundle.patch`），纯 patch insert 不会出现。relay 现在是 bundle：

```
relay/package.json        "dsh": { "plugin": true, "bundle": { "patch": "./cordis.patch.yml" } }
                          exports: "." -> src/index.mjs, "./dsh" -> cordis.patch.yml
relay/cordis.patch.yml    - insert: [{ id: remote-relay, name: dsh-remote-relay }]   # 默认配置
```

构建与安装：

```powershell
pwsh -File scripts/package-relay-plugin.ps1      # dist/dsh-remote-relay-<ver>/ + .tgz
pwsh -File scripts/install-relay-bundle.ps1       # 迁移 dev insert -> 宿主 pluginManager/installBundle
```

`install-relay-bundle.ps1` 流程：读 pairing 拿 loopback 端口+launchToken → `/connect` 换 API cookie →
删除 profile 里的受管 dev insert（备份）→ 删除指向仓库的 dev junction → 用宿主 RPC
`pluginManager/installBundle`（绝对 bundle 目录）安装 → `waitForInstall`/`listBundles` 校验 →
（可选）写 id 覆盖 `- id: remote-relay config: {...}` 保留 name/gateway → 等 relay health/自检；
失败自动回滚 patch 并重跑 `dev-install.ps1` 恢复 dev 安装。**不手写 profile package.json、不直接跑 CLI 安装。**

TLS 默认路径改为 `~/.dsh/remote/certs/relay-cert.pem`/`relay-key.pem`（bundle 不含证书；`gen-tls.ps1`
默认输出到该目录），配置 `tls.certFile/keyFile` 仍优先。

## 5. 故障对照

| 现象 | 处理 |
|---|---|
| pairing 仍 schemaVersion=1 / `gateway.enabled=false` | 先看 `health.gateway.source`：`file` = 用 `relay-config.json`（用 `scripts/relay-config.ps1` 或管理页改）；`patch` = 旧 profile patch 回退；`default` = 未配置。v0.4.0+ 不再需要改 patch/HMR tick |
| health `gateway.online=false` | `gateway.error`/`lastError`；确认 VPS 网关 `/__gateway/identity` 可达、`fingerprint` 与 `fp` 一致、9443 未拦 |
| 旧 token 经网关还能连 | 轮换后应 404；若 200，检查连接器是否已用新 token 重注册（health.identity.tokenMasked） |
| 手机连不上但 LAN smoke 通过 | 网关在线但手机端 v2 解析/证书 pin 问题；用 `relay-smoke.ps1 -Gateway -IncludeWs` 区分网关侧 vs App 侧 |
| 插件页看不到 relay | 确认 `pluginManager/listBundles` 含 `dsh-remote-relay`（installed=true）；纯 patch insert 不会出现在插件页 |
