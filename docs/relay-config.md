# 中继服务器配置（v0.4.0）：手动填写 IP/端口/API 路由端点 + 持久化

从 relay **0.4.0** 起，桌面插件连接的公网中继服务器（VPS 网关）**不再写死在
DSH profile patch 里**，而是：

- 由用户在**本机管理页 / CLI / JSON 文件**里手动填写 `IP（或域名）`、`端口`、可选的
  `API 路由端点`；
- 持久化保存到 `~/.dsh/remote/relay-config.json`；
- 保存后**立即重连**（无需重启 DSH、无需改 YAML、无需 reload tick）。

旧版的 `- id: remote-relay` / `config.gateway`（profile patch）仍然兼容，作为回退；
首次加载时若 JSON 不存在会把旧值**一次性迁移**进去（见「迁移」一节）。

## 1. 配置文件

默认路径：`%USERPROFILE%\.dsh\remote\relay-config.json`

可用环境变量覆盖：`DSH_HOME`（改 `.dsh` 根）、`DSH_REMOTE_CONFIG`（直接指定文件）；
插件 config 的 `configFile` 也可显式指定（优先级最高）。

```json
{
  "version": 1,
  "updatedAt": "2026-11-10T13:30:00.000Z",
  "initializedAt": "2026-11-10T13:30:00.000Z",
  "initializedBy": "one-time-migration:profile-patch",
  "gateway": {
    "enabled": true,
    "host": "203.0.113.10",
    "port": 9443,
    "path": "/__gateway/tunnel",
    "secure": true,
    "fingerprint": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    "name": "DSH Remote Gateway"
  }
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `gateway.enabled` | 是 | 是否启用公网中继；`false` = 只用 LAN 直连（pairing 回退 v1） |
| `gateway.host` | 启用时必填 | 中继服务器 **IP 或 DNS 域名**（IPv4/IPv6/域名；可粘贴 URL 只取主机名） |
| `gateway.port` | 否 | TLS 端口，默认 `9443`，范围 1–65535 |
| `gateway.path` | 否 | **可选 API 路由端点**，默认 `/__gateway/tunnel`；支持反代前缀如 `/dsh/__gateway/tunnel`。该端点用于桌面连接器注册隧道与 identity 探测（`.../identity`），手机仍按 `host:port` 连接（反代需把 `/connect`、`/api/*` 路由到网关） |
| `gateway.secure` | 否 | `true`=wss（默认）/ `false`=ws（仅本机调试） |
| `gateway.fingerprint` | 否 | 网关证书 SPKI SHA-256（大小写/冒号分隔均可）；留空 = 首次连接自动信任（TOFU） |
| `gateway.name` | 否 | 网关展示名（默认 `DSH Remote Gateway`） |
| `gateway.secret` | 否 | 网关启用 `REGISTER_SECRET` 时的共享密钥（持久化，文件权限 0600） |
| `initializedAt/By` | 否 | 一次性初始化标记（`one-time-migration:profile-patch` = 插件从旧 patch 迁移；`one-time-init:installer-seed` = 安装脚本写入），之后保存会保留 |

- 文件用**原子写**（同目录 tmp + rename，权限 0600）；插件目录监听该文件，手动编辑保存后 ~300ms 内自动重载。
- 文件损坏/字段非法时插件**不会崩溃**：继续使用旧配置并在管理页/health 的 `configError` 里报告原因。

## 2. 修改方案（三种，等价）

### 2.1 管理页（推荐）

本机打开 <https://127.0.0.1:8443/__relay/pair>（自签证书选「高级 → 继续前往」），
「中继服务器设置（持久化）」表单：

- 启用公网中继（勾选）；
- 服务器 IP / 域名；
- 端口；
- API 路由端点（可选，占位符 `/__gateway/tunnel`）；
- 证书指纹（可选，留空 = TOFU）。

点「保存并重连」→ 插件校验 → 写入 JSON → 断开旧隧道、按新地址重连 → 重写 pairing/v2 二维码。
页面底部显示配置文件路径、配置来源（`file`/`patch`）、在线状态、保存时间、迁移标记。

> 该页面与配置 API 仅 loopback 可访问，LAN/公网请求一律 404（避免配置和 Token 外泄）。

### 2.2 CLI

```powershell
# 查看当前配置（来源、地址、路由端点、指纹、隧道状态）
pwsh -File scripts/relay-config.ps1

# 指到自己的中继服务器（保存 + 立即重连）
pwsh -File scripts/relay-config.ps1 -Host 203.0.113.10 -Port 9443

# 反代前缀 / 自定义 API 路由端点
pwsh -File scripts/relay-config.ps1 -Host relay.example.com -Port 443 -Path /dsh/__gateway/tunnel

# 固定证书指纹；或清空指纹改回 TOFU
pwsh -File scripts/relay-config.ps1 -Fingerprint 0123456789abcdef...
pwsh -File scripts/relay-config.ps1 -ClearFingerprint

# 关闭 / 重新启用公网中继（LAN 直连始终可用）
pwsh -File scripts/relay-config.ps1 -Disable
pwsh -File scripts/relay-config.ps1 -Enable -Host 203.0.113.10 -Port 9443

# 打印原始 JSON
pwsh -File scripts/relay-config.ps1 -Json
```

脚本只做「GET 当前值 → 合并参数 → POST」，校验/持久化/重连都由插件完成。

### 2.3 直接编辑 JSON

编辑 `~/.dsh/remote/relay-config.json` 保存即可；插件通过目录监听自动重载并重连。
字段规则见上表。若 JSON 写坏，管理页会显示 `configError`，修好后保存生效。

## 3. HTTP API（loopback only）

```powershell
# 查看
curl.exe -k https://127.0.0.1:8443/__relay/config

# 保存并应用（等价于页面表单）
curl.exe -k -X POST -H "content-type: application/json" `
  --data-binary '{\"gateway\":{\"enabled\":true,\"host\":\"203.0.113.10\",\"port\":9443,\"path\":\"/__gateway/tunnel\",\"fingerprint\":\"\"}}' `
  https://127.0.0.1:8443/__relay/config
```

- `GET` → `{ ok, file, exists, source, updatedAt, initializedAt, initializedBy, gateway:{enabled,host,port,path,secure,url,fingerprint,online,usable,error,source,...}, stored, legacy }`
- `POST` 必须带 `content-type: application/json`（跨站页面无法在不触发 CORS 预检的情况下发送该类型，因此不能驱动本机 API；
  `text/plain` 一律 `415`）；body 可为 `{ gateway: {...} }` 或直接 gateway 对象；未提供的字段沿用当前有效值；
  可选 `initializedBy` 只在**文件首次创建**时记为初始化标记。
- 校验失败 → `400 { ok:false, error:"..." }`；relay 尚未就绪 → 503；body > 64KiB → 413。
- `health`（`GET /__relay/health`）里也有 `gateway.source`（`file`/`patch`/`default`）、
  `gateway.configFile`、`gateway.configError`、`gateway.host/port/path`，便于脚本断言。

## 4. 优先级与一次性迁移

优先级：**`relay-config.json` > profile patch 的 `config.gateway` > 默认（不启用）**。

一次性初始化（"本机重装插件后自动填入原来的 VPS 配置"）：

1. 新版本插件启动时，如果 `relay-config.json` **不存在**、而 profile patch 里仍有 `config.gateway`
   （旧的固定 VPS 配置），把旧值写入 JSON，标记
   `initializedBy: "one-time-migration:profile-patch"` / `initializedAt`；
2. `scripts/install-relay-bundle.ps1` 在重装/升级后检测到 JSON 已就位，就删除 profile patch 里的
   gateway 段落（保留桌面名），并在日志中打印
   `one-time migration done — gateway moved to relay-config.json`；
3. 之后 JSON 是唯一可编辑来源；该迁移不会在别的机器上"凭空"填入地址——它只搬运本机
   已有配置，属于一次性动作，不固化进插件默认值。

跳过迁移：`install-relay-bundle.ps1 -NoSeedConfig`。
迁回旧 patch 方式：删除 JSON 文件并重载插件即可回退到 patch 配置。

## 5. 重装 / 升级

```powershell
# 1) 打包新版本（自动跑 src 语法检查 + relay/test/config.test.mjs 单测）
pwsh -File scripts/package-relay-plugin.ps1

# 2) 安装/升级到桌面 profile（自动完成一次性迁移 + 清理旧 patch gateway 段）
pwsh -File scripts/install-relay-bundle.ps1

# 2b) 同版本开发重载（强制卸载重装，不会改动 relay-config.json）
pwsh -File scripts/install-relay-bundle.ps1 -ForceReinstall

# 3) 校验
curl.exe -k https://127.0.0.1:8443/__relay/health      # gateway.source=file, gateway.online=true
pwsh -File scripts/relay-config.ps1                    # 表格形式确认地址/端点/在线状态
```

安装脚本会等待 `health.relayVersion` 等于新包版本再断言，因此不会把旧实例当成新版本；
升级（版本变化）或 `-ForceReinstall` 会先 `pluginManager/removeBundle` 再安装（宿主对
`already-installed` 不会自动更新）。安装期间会保留原桌面名（`-Name` 可显式覆盖）。

## 6. 排障

| 现象 | 排查 |
|---|---|
| 管理页没有「中继服务器设置」卡片 | 插件仍是旧版本：重跑 `scripts/install-relay-bundle.ps1`，看 `health.relayVersion` 是否 0.4.0+ |
| 保存返回 `400` | 看响应 `error`：host 为空/端口越界/端点含空格或 `?`/指纹不是 64 hex |
| `health.gateway.source=patch` | `relay-config.json` 不存在或损坏；打开管理页看 `configError`，保存一次即写 JSON |
| `gateway.online=false` | 看 `gateway.error`：DNS/端口不可达、指纹不匹配、网关未部署 `vps/`、UFW 9443 未放行 |
| 改了 JSON 没生效 | 确认改的是 `health.gateway.configFile` 指向的文件；保存后等待 ~300ms；仍无效则看 DSH 日志 `[remote-relay] config:` |
| 手机仍连旧地址 | 磁盘上的 `pairing.json` 会在重连后自动重写；手机重新加载配对（重新扫码）即可 |
| 迁移后 patch 里 gateway 段还在 | 用了 `-NoSeedConfig` 或迁移前 JSON 已存在；可手动删除该段或重跑安装脚本 |

## 7. 验证记录（2026-11-10，本机 Windows + DSH 0.2.2-rc.1）

| 验证项 | 结果 |
|---|---|
| `relay/test/config.test.mjs` + `relay/test/downloads.test.mjs`（`node --test`，打包脚本自动执行） | **19/19 PASS**：host/port/path/fingerprint 校验、URL 构造（含 IPv6）、原子写/0600、坏文件降级、DSH_HOME/覆盖路径 |
| 假宿主集成探针（`_scratch/relay-config-it.mjs`：假 webServer + effect） | **12/12 PASS**：一次性迁移、GET/POST API、实时应用、400 不落盘、页面表单、文件监听热重载、disable/enable、LAN 无配置 API、415 内容类型门、初始化标记 |
| 本机插件自动迁移（dev 安装，patch 里仍是旧 VPS 配置） | relay-config.json 生成，`initializedBy=one-time-migration:profile-patch`，gateway online=true，`health.gateway.source=file` |
| 本机重装 bundle（删除 relay-config.json 后运行 `install-relay-bundle.ps1`） | 打印 `pre-install relay server captured ... -> 203.0.113.10:9443/__gateway/tunnel`、`one-time initialization filled relay-config.json from the pre-install setup`；bundle 0.4.0 installed，`listBundles` 可见 |
| 插件 reload 持久化（`pluginManager/setBundleEnabled` false→true） | 新实例 startedAt 变化，`relay-config.json` sha256 不变，`gateway.source=file`，gateway 自动恢复 online=true；pairing v2 name 保留 |
| CLI 修改（`scripts/relay-config.ps1`） | `-Host/-Port/-Path/-Fingerprint/-Enable` 保存并立即生效；`-Disable` 后 pairing 回到 v1，再 `-Enable` 恢复 v2 online；`-Json` 可用 |
| 在线改端口（9443→19443→9443） | 文件即时更新、connector 断开重连，改回后 online=true |
| 访问边界 | LAN `https://192.0.2.10:8443/__relay/config` 与 `/__relay/pair` 均 404；loopback 配对页含「中继服务器设置」表单 |
| APK 下载目录（bundle 布局回归） | 修复 `resolveDownloadDir`：从 `<repo>/dist/dsh-remote-relay-<ver>` 安装的 bundle 也能解析到 `<repo>/dist/apk`（新增 4/4 单测）；装后 `health.download.count=13` |
| 回归 | `relay-smoke.ps1 -IncludeWs` LAN PASS；`relay-smoke.ps1 -Gateway -IncludeWs`（经 VPS 203.0.113.10:9443）PASS |

证据文件：[docs/evidence/relay-config/](evidence/relay-config/)（已脱敏：用户路径 → `<user>`，token/launchToken → `<masked>`）。
