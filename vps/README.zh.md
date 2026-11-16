# dsh-remote-gateway（VPS 公网网关 / 资源中心）

零依赖 Node（>= 22）服务：在一台公网 VPS 上以 TLS 9443 作为**唯一公网入口**，按
token / cookie 把请求路由到不同桌面的 DSH relay 连接器，同时提供**按资源分类 ID 组织的
资源中心**（`dsh-remote` / `smartbill` / `relay` / `docs`）的浏览、下载与令牌鉴权上传。
上传时用 `keepVersions` 控制版本保留：保留最近 3 个版本，或只保留刚上传的 1 个版本。

```
手机 ──TLS:9443──▶ VPS 网关 ──WSS（连接器注册）──▶ 桌面 dsh-remote-relay ──▶ DSH 127.0.0.1:<port>
                     └─ /__gateway/download · /__gateway/resources/<id>/<name>
                        （id: dsh-remote | smartbill | relay | docs）
```

- TLS 在**网关终止**；DSH `launchToken` 只出现在「网关 → 连接器」的内部帧里。
- 手机只持有 `remoteToken`（32 hex）；网关在转发 `/connect` 时把它替换成对应桌面的
  `launchToken`，并把响应里的 `dsh-auth*` cookie 映射回该桌面。
- 多桌面并存、互不串流：同一请求里出现两个桌面的 `dsh-auth` cookie 会被 400 拒绝。

## 文件

| 文件 | 作用 |
|---|---|
| `gateway.mjs` | TLS 监听、路由、连接器注册、HTTP/WS 流复用、systemd 入口 |
| `proto.mjs` | RFC 6455 握手/帧编解码 + 网关↔连接器 JSON 帧协议定义 |
| `config.mjs` | 环境变量解析（systemd EnvironmentFile） |
| `identity.mjs` | 证书 SPKI SHA-256 指纹、公开身份文档 |
| `resources.mjs` | 资源分类 ID 注册表（`RESOURCE_CATEGORIES`）、全局扩展名白名单、版本保留策略（`MAX_RETAINED_VERSIONS=3`）、严格扫描、realpath 防穿越、sha256 缓存 |
| `resources-http.mjs` | 下载页/索引/下载（GET/HEAD/Range/ETag）/上传（`keepVersions` 保留策略）/删除的 HTTP 实现 |
| `download-page.mjs` | 按分类 ID 分组的下载页（无顶部分类标签 chips；内联样式、严格 CSP、无脚本） |
| `compress.mjs` | 手机侧响应压缩：纯函数 `maybeCompressResponse`（gzip 触发条件与响应头改写） |
| `downloads.mjs` | 旧 APK 目录 / 旧 kind 下载路径（`apk`/`image`/`misc`）的下载兼容层（`scanDownloads` / `resolveDownload` / `summarizeDownloads`，仅 GET/HEAD） |
| `test/*.test.mjs` | `node --test` 单测（临时目录 fixture，无需网络/VPS） |
| `package.json` | 版本号（identity 里展示，v0.4.0）、`check` / `test` 脚本 |

## 资源分类 ID 注册表（v0.4.0）

| id | title | flavor | 默认目录 | 说明 |
|---|---|---|---|---|
| `dsh-remote` | `DSH Remote 客户端` | apk | `/opt/dsh-remote/resources/dsh-remote` | Release 签名 APK（arm64-v8a）。安装前请核对 sha256。 |
| `smartbill` | `SmartBill（我的账单）客户端` | apk | `/opt/dsh-remote/resources/smartbill` | SmartBill 我的账单 APK（universal，debugsign 测试签名）。 |
| `relay` | `桌面 Relay 插件包` | file | `/opt/dsh-remote/resources/relay` | 桌面端 dsh-remote-relay 插件发布包（zip / tgz / tar）。 |
| `docs` | `文档资料` | file | `/opt/dsh-remote/resources/docs` | 使用说明、部署文档、变更记录（pdf / md / txt / zip）。 |

- ID 必须命中注册表；目录 = `<RESOURCE_DIR>/<id>/`（`RESOURCE_DIR` 默认
  `/opt/dsh-remote/resources`）。未知 ID：路径类 → 404 `not-found`；`?id=` 过滤 →
  400 `unknown-id`（附 `allowedIds`）。
- **新增 App/发布线**：在 `vps/resources.mjs` 的 `RESOURCE_CATEGORIES` 加一项
  （`id`/`title`/`flavor`/`description`）→ `scripts/deploy-gateway.ps1 -Restart`
  重新部署（分类目录 `/opt/dsh-remote/resources/<id>/` 在首次上传时自动创建）；下载页/索引/
  `upload-resource.ps1` 立即按新 ID 工作。
- **文件名白名单全局统一**（原 5 类并集）：
  `.apk .zip .tgz .tar.gz .tar.bz2 .tar.xz .tar.zst .tar .gz .7z .pdf .md .txt .log .csv .json .png .jpg .jpeg .webp .gif .svg .bin`。
- 文件名规则不变：`^[A-Za-z0-9][A-Za-z0-9._-]*$`，长度 1–160，不含 `..`、无路径分隔符；
  只服务分类目录内的**普通文件**（扫描时跳过符号链接，下载时再用 `realpath` 校验必须落在
  分类目录内）。目录缺失/为空时页面显示空态，不报错。

### 版本保留策略（`keepVersions`）

上传接口 `PUT|POST /__gateway/resources/<id>/<name>?keepVersions=0|1`：

| `keepVersions` | 上传后该 ID 目录保留 | 说明 |
|---|---|---|
| `1`（也接受 `true` / `yes` / `on`） | **最近 3 个版本** | 按文件 mtime 倒序，第 4 个及更旧的直接删除（`MAX_RETAINED_VERSIONS = 3`） |
| `0`（也接受 `false` / `no` / `off`）或缺省 | **只保留刚上传的 1 个版本** | 覆盖同名文件，其余旧版本删除 |
| 其它值 | — | 400 `invalid-keep-versions`（附 `allowedValues`） |

- 策略在原子 rename **成功后**执行，作用于该 ID 目录中**所有通过文件名白名单的普通文件**
  （新文件 mtime 最新，排第 1）；临时文件（`.` 开头）不参与；删除失败只记日志、不改变上传结果。
- 上传响应字段 `retained` / `pruned` 列出实际保留与清理的文件名；`replaced` 表示是否覆盖同名文件。

## 部署

在仓库根目录（Windows PowerShell 7）：

```powershell
pwsh -File scripts/deploy-gateway.ps1                 # 首次部署/更新代码（已运行则需 -Restart 生效）
pwsh -File scripts/deploy-gateway.ps1 -WhatIf         # 只打印计划，不改 VPS
pwsh -File scripts/deploy-gateway.ps1 -Restart        # 更新代码后重启
pwsh -File scripts/deploy-gateway.ps1 -RenewCert -Restart   # 重新生成证书（会改变 fp，需重新配对）

pwsh -File scripts/sync-apk.ps1                       # 上传 dist/apk 最新 APK → resources/dsh-remote/（走上传 API，默认保留最近 3 个版本）
pwsh -File scripts/sync-apk.ps1 -All                  # 全部 APK（按 versionCode 升序上传，最终留最新 3 个）
pwsh -File scripts/sync-apk.ps1 -Id smartbill -File dist\apk\smartbill.apk   # 其它 App/发布线
pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-0.4.0.tgz
pwsh -File scripts/upload-resource.ps1 -Id docs -File vps\README.zh.md -Name gateway-readme.md
pwsh -File scripts/upload-resource.ps1 -Id docs -File vps\README.zh.md -Name gateway-readme.md -KeepVersions
pwsh -File scripts/upload-resource.ps1 -List
pwsh -File scripts/upload-resource.ps1 -Delete -Id docs -Name gateway-readme.md
```

部署脚本会：生成/复用自签证书（SAN = 公网 IP + hostname，私钥 600）→ scp `vps/` 到
`/opt/dsh-remote/gateway` → 创建 `/opt/dsh-remote/{gateway,apk,resources}`（不再按 kind 建
子目录；`apk` 目录保留给旧链路/回滚）→ 写 `/etc/dsh-remote/gateway.env`（600，自动生成并
保留 `UPLOAD_TOKEN`）与 `/etc/systemd/system/dsh-remote-gateway.service` →
`systemctl enable/restart` → 回环健康检查 + 公网 `/__gateway/identity` 与
`/__gateway/resources` 校验（按 `identity.resources.categories[].id/count` 打印）。

## 环境变量（`/etc/dsh-remote/gateway.env`）

| 变量 | 默认 | 说明 |
|---|---|---|
| `PORT` | `9443` | 公网 TLS 端口 |
| `BIND_HOST` | `0.0.0.0` | 监听地址 |
| `GATEWAY_PUBLIC_HOST` / `GATEWAY_PUBLIC_PORT` | 自动探测 / `PORT` | identity 的 `url` 与 connector URL 用 |
| `CERT_FILE` / `KEY_FILE` | `/etc/dsh-remote/tls/gateway-{cert,key}.pem` | TLS 证书/私钥 |
| `APK_DIR` | `/opt/dsh-remote/apk` | **旧 APK 根目录**：仅作旧下载链路的兼容回退与回滚兜底，页面/索引不扫描它 |
| `RESOURCE_DIR` | `/opt/dsh-remote/resources` | 分类根目录：`<RESOURCE_DIR>/<id>`（`dsh-remote`/`smartbill`/`relay`/`docs`） |
| `UPLOAD_TOKEN` | 空 | 上传/删除 Bearer 令牌；为空时上传/删除接口返回 404（禁用） |
| `MAX_UPLOAD_BYTES` | `209715200`（200 MiB） | 单次上传大小上限（流式计数） |
| `REGISTER_SECRET` | 空 | 可选；设置后连接器注册必须带相同 `secret` |
| `LOG_LEVEL` | `info` | `debug` 会打印每条流；token 始终掩码 |

高级调优：`TUNNEL_SILENCE_TIMEOUT_MS`(60000)、`OFFLINE_TTL_MS`(1800000)、
`HTTP_TIMEOUT_MS`(120000)、`WS_OPEN_TIMEOUT_MS`(20000)、`MAX_BODY_BYTES`(32MiB)、
`MAX_WS_MESSAGE_BYTES`(16MiB)、`MAX_STREAMS_PER_TUNNEL`(256)。

## 公开端点

| 端点 | 方法 | 说明 |
|---|---|---|
| `/__gateway/identity` | GET | `{version,name,fp,tunnels,url,node,uptimeSec,download,resources}`；`resources.categories[]` 含 `id/title/count/latest`，无敏感信息 |
| `/__gateway/health` | GET | `{ok:true,...}` + 计数/资源统计（`resourceCategories`，`resourceKinds` 为兼容别名）/最近错误时间 |
| `/__gateway/download` | GET/HEAD | **资源下载页**（按分类 ID 分组卡片，无顶部分类标签；APK 分类保留「下载最新版 APK」；无脚本、严格 CSP） |
| `/__gateway/download/<file>.apk` | GET/HEAD | 旧 APK 直链（兼容：在所有分类目录与旧 `APK_DIR` 中按文件名查找） |
| `/__gateway/resources` | GET/HEAD | JSON 索引（可加 `?id=<id>` 过滤；含 `categories[].id`、`counts.byId`、`latest`/`files`） |
| `/__gateway/resources/index` | GET/HEAD | JSON 索引别名 |
| `/__gateway/resources/<id>/<name>` | GET/HEAD | 流式下载：`content-type`/`x-content-sha256`/`ETag`/`Range`/`?inline=1` |
| `/__gateway/resources/<id>/<name>` | PUT/POST | 上传（`Authorization: Bearer <UPLOAD_TOKEN>`；`?keepVersions=0\|1`；流式 + 原子 rename + 保留策略，返回 sha256/retained/pruned） |
| `/__gateway/resources/<id>/<name>` | DELETE | 删除（同上鉴权） |
| `/__gateway/resources/<apk\|image\|misc>/<name>` | GET/HEAD | 旧 kind 下载别名（仅下载，兼容保留） |
| `/__gateway/resources/<apk\|image\|misc>/<name>` | PUT/POST | **不再接受** → 400 `id-required`（上传改用分类 ID） |
| `/__gateway/tunnel`（WebSocket） | GET | 桌面连接器注册：`?token=<32hex>&name=&launchToken=[&secret=]` |
| `/connect?token=<remoteToken>` | GET | 找桌面 → 替换 token → 转发 → 索引 `dsh-auth*` cookie |
| 其它路径（`/api/*`、`/__relay/*`…） | * | 按 cookie 路由到桌面；无 cookie 404 |

上传/删除在**未配置 `UPLOAD_TOKEN` 时返回 404**（端点禁用）；令牌缺失/错误返回 **403**。
索引 `?id=` 未知 → **400** `unknown-id`（附 `allowedIds`）；路径里的未知 ID → **404**。
旧 kind 上传路径 → **400** `id-required`。错误响应统一为 `{ok:false,error,message}` JSON；
页面/索引/下载响应都不含令牌。

## 兼容矩阵（v0.3.0 → v0.4.0）

| 旧能力（v0.3.0） | v0.4.0 状态 |
|---|---|
| `GET /__gateway/download` 页面 | ✅ 同一 URL；**取消顶部分类标签（chips）**，改为按分类 ID 分组卡片 |
| `GET /__gateway/download/<file>.apk` | ✅ 仍可用：在所有分类目录与旧 `APK_DIR` 根目录按文件名查找 `.apk` |
| `GET /__gateway/resources/apk/<name>` | ✅ 仍可用（旧 `apk` kind 别名，仅 `.apk`，只 GET/HEAD） |
| `GET /__gateway/resources/image/<name>`、`misc/<name>` | ✅ 仍可用（旧别名，仅 GET/HEAD；扩展名须属于旧 image/misc 白名单） |
| `GET /__gateway/resources/relay\|docs/<name>` | ✅ `relay`/`docs` 本来就是注册表 ID，规范路径不变 |
| `PUT\|POST /__gateway/resources/apk\|image\|misc/<name>` | ❌ 不再接受：400 `id-required`（改用 `/__gateway/resources/<id>/<name>`） |
| JSON 索引 `?kind=` | ⚠️ 不再支持（未知名查询参数被忽略、不报错）；改用 `?id=` |
| `upload-resource.ps1 -Kind` | ❌ 被 `-Id` 取代（新增 `-KeepVersions` 开关） |
| `sync-apk.ps1`（scp 到 `/opt/dsh-remote/apk`） | ❌ 改为走上传 API（`-Id` 默认 `dsh-remote`、默认保留 3 个版本；`-NoKeepVersions` 关闭）；移除 `-RemoteDir` |
| 旧 `APK_DIR`（`/opt/dsh-remote/apk`） | ✅ 目录保留，仅作兼容下载回退与回滚兜底；页面/索引不扫描 |

## 手机侧响应压缩（v0.3.0+）

网关把桌面响应写回手机前，对满足条件的 body 做 gzip（桌面↔网关的 base64 隧道流量不变）：

- **触发条件**：方法非 `HEAD`、状态 2xx（`206` 除外）、body ≥ 1024 字节、`content-type` 为
  `application/json` / `text/*` / `application/javascript*`、无既有 `content-encoding`、
  手机 `Accept-Encoding` 接受 gzip（`gzip;q=0` 视为不接受）；
- **改写**：`content-encoding: gzip`、`vary: accept-encoding`、`content-length` = 压缩后长度，
  删除 `etag`；不满足条件原样透传（小响应/二进制/Range/已编码响应不重复压缩）；
- **效果**：实测 DSH 历史/会话列表 JSON 可压 **3.6–4.9×**；App 侧 HTTP 客户端自动发送
  `Accept-Encoding: gzip` 并解压；
- **实现/测试**：`compress.mjs` 的纯函数 `maybeCompressResponse`，`gateway.mjs` 在 `onHttpEnd`
  调用；单测 `test/gzip.test.mjs`。

## 连接器帧协议 v1（摘要）

WebSocket 文本 JSON 控制帧，`id` 为 16 hex 流 ID，body 全量 base64：

```
gateway → connector: {id,kind:"http",method,url,headers,bodyB64} + {id,kind:"http-end"}
connector → gateway: {id,kind:"http-res",status,headers,bodyB64} + {id,kind:"http-end"}
                     {id,kind:"http-error",status,message}（可选，替代 http-res/http-end）
gateway → connector: {id,kind:"ws-open",url,headers}
connector → gateway: {id,kind:"ws-open-ok"} / {id,kind:"ws-open-fail",status,message}
双向:                 {id,kind:"ws-msg",op:1|2,b64}   （op 缺省 1=text；2=binary）
双向:                 {id,kind:"ws-close",code,reason?}
连接器每 20s:         {kind:"ping"}  → 网关回 {kind:"pong"}；网关 60s 无任何帧视为离线
```

完整语义、限制与排障见 `docs/vps-gateway.md`；资源中心的接口规范、运维与本次部署验证
记录见仓库根目录 `服务器文档/`。

## 本地测试

```powershell
cd vps
npm run check   # 全部模块 node --check
npm test        # node --test（临时目录 fixture，不访问网络/VPS）
```

## 版本与变更（v0.4.0，2026-11-13）

- 资源模型从「按文件类型 kind（apk/relay/docs/image/misc）」改为「**分类 ID 注册表**」
  （`dsh-remote` / `smartbill` / `relay` / `docs`），目录 `/opt/dsh-remote/resources/<id>/`；
- 上传新增 `?keepVersions=0|1`：`1` 保留最近 3 个版本、`0` 或**缺省**只保留 1 个；
- 下载页**取消顶部分类标签（chips）**，改为按分类 ID 分组卡片；
- 旧的按 kind 上传路径不再接受（400 `id-required`）；旧的**下载**直链保持可用；
- 迁移：旧 dshremote APK → `resources/dsh-remote/`（按“保留最近 3 个”归档其余），
  smartbill APK → `resources/smartbill/`，`relay`/`docs` 目录原地成为分类，
  `/opt/dsh-remote/apk` 清空保留（仅兼容下载回退/回滚兜底）。

## 安全要点

- 32 hex token 即凭据（注册不要求白名单）；可选 `REGISTER_SECRET` 再加一层。
- 日志/错误页只出现 token 掩码（前 4 + 后 4）；`launchToken`、cookie 值、`UPLOAD_TOKEN`
  从不落日志，页面与 JSON 索引也不含令牌。
- 资源下载只暴露各分类目录内白名单普通文件（拒绝 `..`、子目录、符号链接逃逸）。
- 上传/删除只接受 `Authorization: Bearer <UPLOAD_TOKEN>`；流式写临时文件 + 上限 +
  原子 rename；未配置令牌时端点禁用（404）。
- 版本保留策略只作用于**该 ID 目录**内通过文件名白名单的普通文件，上限 3 个；
  旧 kind 上传路径一律 400（不能借旧路径绕过 ID/保留策略）。
- 下载页保持无脚本 + 严格 CSP（`default-src 'none'`，只允许内联样式）。
- 自签证书：手机端按 `identity.fp`（SPKI SHA-256）固定；换证书后需重新配对。
- systemd 加固：`NoNewPrivileges`、`PrivateTmp`、`ProtectSystem=full`、
  `ProtectHome=read-only`、仅 `APK_DIR` 与 `RESOURCE_DIR` 可写。
