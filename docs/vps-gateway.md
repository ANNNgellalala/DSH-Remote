# VPS 通用网关（M6）：按 Token 路由多桌面 + 资源中心

`vps/` 是一个**零第三方运行时依赖**的 Node（>= 22）服务，部署在一台公网 VPS 上，
作为所有桌面 DSH 的**唯一公网 TLS 入口**：

```
手机 App ──TLS 9443──▶ VPS 网关 ──WSS 隧道（桌面主动外连）──▶ 桌面 dsh-remote-relay
                                                                 └─▶ 内部明文监听 127.0.0.1:<port>
                                                                       └─▶ DSH 127.0.0.1:<upstreamPort>
```

v0.2.0 起网关同时是**资源中心**；v0.4.0 起按**资源分类 ID**组织（`dsh-remote` /
`smartbill` / `relay` / `docs`，目录 `/opt/dsh-remote/resources/<id>/`）：下载页按分类 ID
分组卡片展示全部可下载资源（**无顶部分类标签 chips**），`/__gateway/resources` 提供
JSON 索引（`categories[].id`、`counts.byId`、`?id=` 过滤），资源下载支持 HEAD/Range/ETag，
上传/删除通过 `UPLOAD_TOKEN` Bearer 鉴权，上传用 `keepVersions=0|1` 控制保留最近 3 个
还是只留 1 个版本。旧的 APK/旧 kind **下载**直链继续兼容，旧 kind 上传路径返回 400
`id-required`。资源中心的接口规范、运维与部署验证记录见仓库根目录 `服务器文档/`。

它解决了「一台 VPS 服务多个桌面、每个桌面有自己的 DSH `launchToken`」的问题：

- **TLS 在网关终止**。桌面不需要公网 IP / 端口映射，也不需要 SSH 反向隧道；
  连接器由桌面主动连出（NAT 友好）。
- **手机只持有 `remoteToken`**（32 hex，桌面 relay 生成，可轮换）。
- **DSH `launchToken` 只在「网关 ↔ 连接器」内部出现**：网关把 `/connect` 查询里的
  `token` 替换成该桌面注册时上报的 `launchToken` 再经隧道转发；`launchToken` 不落手机、
  不落日志、不出现在 identity/错误页。
- 网关用响应里的 `Set-Cookie: dsh-auth*` 建立「cookie → 桌面」索引，之后的
  `/api/*`（含 WebSocket `/api/remote.mux`）与 `/__relay/*` 都按请求 cookie 路由，
  多桌面互不串流。

## 快速开始（运维三条命令）

```powershell
# 1) 部署/更新网关（生成证书、scp、systemd、健康检查）
pwsh -File scripts/deploy-gateway.ps1                # 首次部署
pwsh -File scripts/deploy-gateway.ps1 -Restart       # 更新代码后重启生效

# 2) 发布 release APK（走上传 API → /opt/dsh-remote/resources/dsh-remote/；默认保留最近 3 个版本）
pwsh -File scripts/sync-apk.ps1                       # 默认最新版（-Id 默认 dsh-remote）
pwsh -File scripts/sync-apk.ps1 -All                  # 全部 APK（按 versionCode 升序上传，最终留最新 3 个）
pwsh -File scripts/sync-apk.ps1 -Id smartbill -File dist\apk\smartbill.apk   # 其它 App/发布线
pwsh -File scripts/sync-apk.ps1 -NoKeepVersions       # 只保留刚上传的 1 个版本

# 2b) 上传其它分类资源（自动读取 VPS 上的 UPLOAD_TOKEN；上传后校验 sha256）
pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-0.4.0.tgz
pwsh -File scripts/upload-resource.ps1 -Id docs -File vps\README.zh.md -Name gateway-readme.md -KeepVersions
pwsh -File scripts/upload-resource.ps1 -List

# 3) 桌面 relay 配置连接器（relay-dev 负责），注册成功后：
curl.exe -k https://203.0.113.10:9443/__gateway/identity
# -> {"version":"0.1.0","name":"DSH Remote Gateway","fp":"b6c6…21b8","tunnels":1,
#     "url":"wss://203.0.113.10:9443", ...}
```

## 当前部署（2026-11-12 部署 v0.3.0；2026-11-13 升级 v0.4.0）

| 项 | 值 |
|---|---|
| VPS | `203.0.113.10`（ExampleVPS，Ubuntu 26.04.1 LTS，x86_64） |
| SSH | `root@203.0.113.10:22222`，key `%USERPROFILE%\.ssh\id_ed25519` |
| Node | `/usr/bin/node` v22.22.1（apt；网关无 npm 依赖。任务原定 Node 24，lead 确认 v22.22.1 可用，全部实测均在该版本通过） |
| 公网入口 | `https://203.0.113.10:9443`（UFW 9443/tcp 已放行） |
| 网关版本 | `0.4.0`（资源分类 ID + `keepVersions` 保留策略 + 页面无分类标签 chips；identity.version 展示；实测数字以 `服务器文档/部署与验证记录.md` 为准） |
| 证书 | 自签，SAN = `IP:203.0.113.10` + `DNS:gateway.example.com`，825 天 |
| SPKI SHA-256（fp） | `0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef` |
| systemd | `dsh-remote-gateway`，`enabled` + `active`，`Restart=always`，`ReadWritePaths=/opt/dsh-remote/apk /opt/dsh-remote/resources` |
| 代码 | `/opt/dsh-remote/gateway` |
| 配置 | `/etc/dsh-remote/gateway.env`（600） |
| APK | `/opt/dsh-remote/resources/dsh-remote`（分类 ID `dsh-remote`；迁移后按“保留最近 3 个”归档；旧 `/opt/dsh-remote/apk` 清空保留作兼容下载回退/回滚） |
| 其它资源 | `/opt/dsh-remote/resources/{smartbill,relay,docs}`（分类 ID；`relay`/`docs` 目录原地成为分类） |
| 下载页 | `https://203.0.113.10:9443/__gateway/download`（按分类 ID 分组卡片，无顶部分类标签） |
| JSON 索引 | `https://203.0.113.10:9443/__gateway/resources`（可加 `?id=<id>`） |
| 上传/删除 | `Authorization: Bearer <UPLOAD_TOKEN>`；上传 URL 带 `?keepVersions=0\|1`；令牌在 gateway.env（600），首次部署自动生成并保留 |
| 日志 | journald：`journalctl -u dsh-remote-gateway` |
| 连接器 URL | `wss://203.0.113.10:9443/__gateway/tunnel` |

VPS 上只**新增**了 9443/tcp 与 `/opt/dsh-remote`、`/etc/dsh-remote`；原有
`xray`(443)、`hysteria`(8443/udp)、`subsvc`(8444)、sshd(22222) 均未改动（实测仍 active）。

## 源码结构

| 文件 | 职责 |
|---|---|
| `vps/gateway.mjs` | TLS 监听、路由、连接器注册表、HTTP/WS 流复用、优雅退出 |
| `vps/proto.mjs` | RFC 6455 握手/帧编解码（server & client 方向）、v1 JSON 帧协议定义、header/cookie 工具 |
| `vps/config.mjs` | 环境变量解析 + TLS 材料加载（fail fast） |
| `vps/identity.mjs` | 证书 SPKI SHA-256、identity 文档、版本号 |
| `vps/resources.mjs` | 资源分类 ID 注册表（`RESOURCE_CATEGORIES`）、全局扩展名白名单、严格扫描、realpath 防穿越、sha256 缓存、索引摘要、版本保留策略（`MAX_RETAINED_VERSIONS=3`）、旧 APK API 兼容实现 |
| `vps/resources-http.mjs` | 资源 HTTP 面：下载页渲染、JSON 索引（`?id=` 过滤）、下载（HEAD/Range/ETag/inline）、Bearer 上传（`keepVersions` 保留策略 + retained/pruned）/删除 |
| `vps/downloads.mjs` | 旧 APK 目录 / 旧 kind 下载路径（`apk`/`image`/`misc`）的下载兼容层（`scanDownloads`/`resolveDownload`/`summarizeDownloads`/`parseApkName`/`hashEntries` 全部保留；仅 GET/HEAD） |
| `vps/download-page.mjs` | 按分类 ID 分组的下载页（内联样式、严格 CSP、无脚本；**无顶部分类标签 chips**；兼容旧 `files` 模型） |
| `vps/compress.mjs` | 手机侧响应压缩的纯函数 `maybeCompressResponse`（gzip 触发条件/响应头改写，可单测） |
| `vps/test/*.test.mjs` | `node --test` 单测（临时目录 fixture，无网络/VPS 依赖） |
| `vps/README.zh.md` | 面向部署者的速查（本文件的摘要版） |
| `vps/package.json` | 版本号（identity 展示）、`node --check` 脚本 |

## 部署细节（`scripts/deploy-gateway.ps1`）

1. 本地检查：ssh/scp 存在、`vps/` 文件齐全、`node --check` 全部模块。
2. 远程检查：SSH 可达、Node >= 22、当前服务状态、9443 是否被非本服务占用
   （若旧 SSH 隧道还占着端口会直接报错并提示 `scripts/vps-tunnel.ps1 -Action stop`）。
3. 生成/复用证书：`openssl req -x509 -newkey rsa:2048 -sha256 -days 825`，
   SAN 自动包含公网 IP、VPS hostname 与 `-ExtraSan`；私钥 600、证书 644。
   `-RenewCert` 强制重签（**fp 会变，手机需要重新配对**）。
4. 创建 `/opt/dsh-remote/{gateway,apk,resources}`（不再按 kind 建子目录；`apk` 目录保留
   给旧链路/回滚；分类目录 `/opt/dsh-remote/resources/<id>/` 在首次上传时自动创建或按
   Lead 迁移方案预建）。
5. `scp` 上传 `vps/*.{mjs,json,md}` 到 `/opt/dsh-remote/gateway`，并在远端再跑一次
   `node --check`。
6. 写 `/etc/dsh-remote/gateway.env`（600，含 `RESOURCE_DIR`/`MAX_UPLOAD_BYTES`/
   `UPLOAD_TOKEN`；令牌首次生成、之后保留）与
   `/etc/systemd/system/dsh-remote-gateway.service`（`ReadWritePaths` 含 apk 与资源目录），
   `systemctl daemon-reload`、`enable`、按需 `restart`。
7. 验证：回环 `https://127.0.0.1:9443/__gateway/health`，公网
   `/__gateway/identity` 且 **identity.fp == 证书 SPKI fp**、`/__gateway/resources`
   索引可读；按 `identity.resources.categories[].id/count` 打印分类计数，
   APK 数量比对 `identity.download.count` 与索引里 `.apk` 文件数（跨分类求和）。
   （公网校验优先用 `curl.exe -k`，若本机 schannel 被拦截会自动回落 Node TLS。）
8. `-WhatIf` 只做只读检查并打印计划，不改 VPS；`-SkipVerify` 跳过公网回连检查。

### 环境变量（`/etc/dsh-remote/gateway.env`）

| 变量 | 默认 | 说明 |
|---|---|---|
| `PORT` | `9443` | 公网 TLS 端口 |
| `BIND_HOST` | `0.0.0.0` | 监听地址 |
| `GATEWAY_PUBLIC_HOST` | 自动探测 | identity `url` / 连接器 URL 使用的公网主机 |
| `GATEWAY_PUBLIC_PORT` | `PORT` | 公网端口 |
| `CERT_FILE` / `KEY_FILE` | `/etc/dsh-remote/tls/gateway-{cert,key}.pem` | TLS 材料 |
| `APK_DIR` | `/opt/dsh-remote/apk` | **旧 APK 根目录**：仅作旧下载链路的兼容回退与回滚兜底；页面/索引不扫描 |
| `RESOURCE_DIR` | `/opt/dsh-remote/resources` | 分类根目录：`<RESOURCE_DIR>/<id>`（`dsh-remote`/`smartbill`/`relay`/`docs`） |
| `UPLOAD_TOKEN` | 空 | 上传/删除 Bearer 令牌；为空时上传/删除接口 404（禁用）。deploy 脚本首次自动生成、后续保留 |
| `MAX_UPLOAD_BYTES` | `209715200`（200 MiB） | 单次上传大小上限（流式计数，超限 413） |
| `REGISTER_SECRET` | 空 | 可选；设置后连接器必须带同值 `secret` |
| `LOG_LEVEL` | `info` | `debug` 打印每条流；token 始终掩码 |
| `TUNNEL_SILENCE_TIMEOUT_MS` | `60000` | 连接器静默多久判离线 |
| `OFFLINE_TTL_MS` | `1800000` | 离线 record 保留多久（错误页/重连体验） |
| `HTTP_TIMEOUT_MS` | `120000` | 单条 HTTP 流超时 |
| `WS_OPEN_TIMEOUT_MS` | `20000` | 连接器接受 ws-open 的超时 |
| `MAX_BODY_BYTES` | `32MiB` | 请求 body（base64 前）上限 |
| `MAX_WS_MESSAGE_BYTES` | `16MiB` | 单条 WS 消息上限 |
| `MAX_FRAME_BYTES` | `64MiB` | 隧道单帧上限 |
| `MAX_STREAMS_PER_TUNNEL` | `256` | 单桌面并发流上限 |

修改后 `systemctl restart dsh-remote-gateway`。

## 公开端点契约

### 资源分类 ID 注册表（v0.4.0）

| id | title | flavor | 目录 | 说明 |
|---|---|---|---|---|
| `dsh-remote` | `DSH Remote 客户端` | apk | `<RESOURCE_DIR>/dsh-remote/` | Release 签名 APK（arm64-v8a）；安装前核对 sha256 |
| `smartbill` | `SmartBill（我的账单）客户端` | apk | `<RESOURCE_DIR>/smartbill/` | SmartBill APK（universal，debugsign 测试签名） |
| `relay` | `桌面 Relay 插件包` | file | `<RESOURCE_DIR>/relay/` | zip / tgz / tar 发布包 |
| `docs` | `文档资料` | file | `<RESOURCE_DIR>/docs/` | pdf / md / txt / zip |

- `RESOURCE_DIR` 默认 `/opt/dsh-remote/resources`；ID 必须命中注册表。未知 ID：
  路径类 → 404 `not-found`；`?id=` 过滤 → 400 `unknown-id`（附 `allowedIds`）。
- 新增 App/发布线：`vps/resources.mjs` 的 `RESOURCE_CATEGORIES` 加一项并重新部署。
- 文件名白名单**全局统一**（原 5 类并集）：`.apk .zip .tgz .tar.gz .tar.bz2 .tar.xz
  .tar.zst .tar .gz .7z .pdf .md .txt .log .csv .json .png .jpg .jpeg .webp .gif .svg .bin`；
  文件名规则 `^[A-Za-z0-9][A-Za-z0-9._-]*$`、1–160 字符、不含 `..`、无路径分隔符。

**`keepVersions`（上传保留策略）**

| 值 | 上传后该 ID 目录保留 |
|---|---|
| `1` / `true` / `yes` / `on` | 最近 3 个版本（按 mtime 倒序，`MAX_RETAINED_VERSIONS = 3`） |
| `0` / `false` / `no` / `off` 或**缺省** | 只保留刚上传的 1 个版本（覆盖同名，其余旧版本删除） |
| 其它 | 400 `invalid-keep-versions`（附 `allowedValues`） |

策略在 rename 成功后执行，作用于该 ID 目录中所有通过文件名白名单的普通文件；
临时文件（`.` 开头）不参与；删除失败只记日志、不影响上传结果。

### `GET /__gateway/identity`

```json
{
  "version": "0.4.0",
  "name": "DSH Remote Gateway",
  "fp": "<证书 SPKI sha256 小写 hex>",
  "tunnels": 1,
  "url": "wss://203.0.113.10:9443",
  "node": "v22.22.1",
  "uptimeSec": 123,
  "download": { "page": "/__gateway/download", "count": 6, "latest": { ... } },
  "resources": {
    "page": "/__gateway/download", "index": "/__gateway/resources", "total": 8,
    "byId": { "dsh-remote": 3, "smartbill": 3, "relay": 1, "docs": 1 },
    "categories": [
      { "id": "dsh-remote", "title": "DSH Remote 客户端", "count": 3, "latest": { ... } }
    ],
    "apk": { "count": 6, "latest": { ... } }
  }
}
```

`download` 字段语义 = 所有分类中的 `.apk` 文件。无 token / cookie / launchToken /
服务器绝对路径等敏感信息。手机可用 `fp` 做证书固定。

### `GET /__gateway/health`

`{ok:true, version, name, fp, tunnels, node, uptimeSec, apkCount, resourceCount, resourceCategories:[{id,count}], resourceKinds(兼容别名，同内容), lastErrorAt, stats:{requests,connect,forwardedHttp,forwardedWs,rejected}}`

### `GET /__gateway/download` + 旧 APK 直链

- 页面每次请求**重扫分类目录**，按注册表顺序输出**有文件的分类卡片**（空分类不输出；总数 0
  时显示空态）。**无顶部分类标签（`<nav class="chips">` 已删除）**。
- 每个分类卡片：`eyebrow` = `<title> · N 个文件`；`desc` = description；最新文件主按钮
  （flavor=apk → `下载最新版 APK`，flavor=file → `下载最新文件`）；版本表（表头
  `版本（仅 apk flavor）| 文件 · sha256 | 大小 | 更新时间`），窄屏保持堆叠卡片样式。
- 页面保留 APK 安装提示、网关信息（配对/指纹）、安全提示、页脚；不出现 token；
  无脚本、严格 CSP 不变。
- 旧直链 `GET|HEAD /__gateway/download/<file>.apk`：在所有分类目录（以及旧 `APK_DIR`
  根目录）里按文件名查找 `.apk` 后流式下载；找不到 404。仅 GET/HEAD 兼容。
- 文件下载：`content-type` 按扩展名、`content-disposition: attachment`、
  `content-length`、`x-content-sha256`、`ETag`、`accept-ranges: bytes`、
  `cache-control: no-store`；`fs.createReadStream` 流式；支持 HEAD、单区间 Range（206/416）、
  `If-None-Match`（304）；`?inline=1` 仅对 pdf/md/txt/log/csv/图片生效（SVG/压缩包/APK 始终附件）。
- 白名单：只服务分类目录内白名单普通文件，拒绝 `..`/子目录/符号链接逃逸（扫描 +
  `realpath` 双重校验）。目录不存在时页面显示空态与上传命令。

### `GET /__gateway/resources`（JSON 索引，别名 `/index`）

- 公开、无敏感信息；`?id=<id>` 过滤，未知 id → **400**：
  `{"ok":false,"error":"unknown-id","message":...,"allowedIds":[...]}`；`?kind=` 不再支持
  （未知名查询参数被忽略、不报错）。
- 响应：`{ok, generatedAt, gateway{name,version,url,fp}, page, index, downloadBase,
  total, counts{total,byId}, categories[{id,title,description,count,latest,files[
  {id,name,sizeBytes,sha256,updatedAt,contentType,path,url,version?,versionCode?,legacyUrl?}]}],
  apk{count,latest}}`。
- 排序：flavor=apk 分类按 versionCode 降序 → 版本号降序 → mtime 降序；其余分类 mtime
  降序 → 文件名升序。
- `path` 为相对直链，`url` 为绝对直链；不含服务器绝对路径/token/cookie。

### `GET|HEAD /__gateway/resources/<id>/<name>`（规范下载）

- 下载头/HEAD/Range/ETag/inline 语义同上；`id` 必须命中注册表、`name` 必须通过全局
  文件名白名单，否则 404（未知 ID / 非法名 / 穿越 / 符号链接 → 404 `not-found`）。

### `PUT|POST /__gateway/resources/<id>/<name>?keepVersions=0|1`（上传）

- 鉴权：`Authorization: Bearer <UPLOAD_TOKEN>`；未配置 `UPLOAD_TOKEN` → **404**
  （`uploads-disabled`，端点禁用）；缺/错令牌 → **403**（`WWW-Authenticate: Bearer`）。
- `keepVersions` 解析见上表，非法值 → **400** `invalid-keep-versions`。
- 流式接收 → 临时文件（同目录 `.<random>-<name>`）→ `fsync` → 原子 `rename` →
  失效 sha 缓存 → **按 keepVersions 执行保留策略**；`MAX_UPLOAD_BYTES`（默认 200 MiB）
  超限 → 413。
- 成功返回（201 新建 / 200 覆盖）：
  `{ok,id,name,sizeBytes,sha256,keepVersions,replaced,retained:[...],pruned:[...],path,url}`；
  响应头保留 `x-content-sha256`；日志记录 `id/name/size/keepVersions/replaced/pruned`。
- 文件名严格校验（同下载白名单），非法 → 400 `invalid-name`。

### `DELETE /__gateway/resources/<id>/<name>`（删除）

- 鉴权同上；成功 200 `{ok,id,name,deleted:true}`；不存在 → 404；无/错令牌 → 403。

### 旧 kind 下载别名（仅下载，兼容保留）

| 路径 | 行为 |
|---|---|
| `GET\|HEAD /__gateway/resources/apk/<name>` | 在所有分类目录按文件名查找 `.apk`（旧 apk kind 别名） |
| `GET\|HEAD /__gateway/resources/image/<name>` | 在所有分类里按文件名查找，扩展名须属于旧 image 白名单 |
| `GET\|HEAD /__gateway/resources/misc/<name>` | 同上（旧 misc 白名单） |
| `PUT\|POST /__gateway/resources/apk\|image\|misc/<name>` | **400** `id-required`（body 见下） |

旧 kind 上传路径的 400 响应体：

```json
{"ok":false,"error":"id-required","message":"上传接口已改为 /__gateway/resources/<id>/<name>?keepVersions=0|1（id: dsh-remote|smartbill|relay|docs）"}
```

`relay` / `docs` 本就是注册表 ID，规范路径不变；旧 `APK_DIR` 仅作兼容下载回退与回滚兜底，
页面/索引不扫描它。

完整接口规范（请求/响应示例、错误码、大小限制）见仓库根目录
`服务器文档/HTTP接口规范.md`；运维命令见 `服务器文档/上传下载运维.md`。

### `GET /connect?token=<remoteToken>`

1. 按 `token` 找在线桌面；不存在或离线 → **404**（`unknown-token` / `desktop-offline`；
   浏览器 `Accept: text/html` 时返回中文错误页，否则 JSON）。
2. 把 query 的 `token` 替换为桌面 `launchToken`（其余参数如 `interactions` 原样保留），
   经连接器转发到桌面 relay 的内部明文监听。
3. 转发响应（302/Set-Cookie/body）给手机；同时把 `Set-Cookie` 中 `dsh-auth*`
   的 `name=value` 记入「cookie → 桌面」索引。
4. 同一 token 重复注册会替换旧连接；新 `launchToken` 会清空旧 cookie 索引。

### 其它路径（`/api/*`、`/__relay/*`、WebSocket `/api/remote.mux`）

- 从 `Cookie` 头中找 `dsh-auth*` 键 → 桌面 → 转发（HTTP body 与响应全量透传）。
- 无 cookie / 未知 cookie → 404；已知 cookie 但桌面离线 → 503。
- 一个请求里出现**两个桌面**的 `dsh-auth` cookie → 400（拒绝串流）。
- WebSocket：网关先向连接器发 `ws-open`，收到 `ws-open-ok` 后才给手机回 101，
  之后双向按 `ws-msg` 转发；不支持时回 404/503/502。
- 网关自有路径（`/__gateway/*`）永不转发给桌面。

### 手机侧响应压缩（gzip，v0.3.0+）

桌面响应经连接器到达网关后，网关在**公网 TLS 出口**按下列条件决定是否 gzip 再写给手机
（桌面↔网关的 base64 隧道流量不变）：

| 条件 | 要求 |
|---|---|
| 方法 | 非 `HEAD` |
| 状态 | 2xx，且不是 `206 Partial Content` |
| body | ≥ 1024 字节 |
| `content-type` | `application/json`（可带 charset）、`text/*` 或 `application/javascript*` |
| 既有编码 | 无 `content-encoding`（绝不二次压缩） |
| 手机 | `Accept-Encoding` 接受 gzip（含 `gzip;q=0` 时视为不接受） |

压缩时写 `content-encoding: gzip`、`vary: accept-encoding`、`content-length` = 压缩后长度，
并**删除 `etag`**（实体已变，避免旧校验器命中未压缩表示）；不满足条件的响应原样透传。
实测 DSH 历史/会话列表 JSON 可压 3.6–4.9×；App 的 HTTP 客户端自动发送 `Accept-Encoding: gzip`
并透明解压。实现与单测：`vps/compress.mjs` + `vps/test/gzip.test.mjs`。

## 连接器帧协议 v1（网关 ↔ 桌面 relay）

连接经由 `GET /__gateway/tunnel` 的 WebSocket（标准 upgrade）承载：

```
GET /__gateway/tunnel?token=<32hex>&name=<桌面名>&launchToken=<DSH launchToken>[&secret=...]
```

- `token` 必须 32 hex（大小写均可，网关归一为小写）；`launchToken` 非空、无空白/控制字符、≤1024；
- `name` 仅用于展示（去控制字符，≤64 字符）；
- 设置 `REGISTER_SECRET` 时 `secret` 必须匹配，否则握手前返回 401；
- 校验失败返回 400/401 后直接关闭（不 upgrade）；成功后返回 101，随后开始帧交换。

所有数据帧使用**文本 JSON 控制帧**，`id` 为 16 hex 流 ID：

| 方向 | 帧 | 语义 |
|---|---|---|
| G→C | `{"id","kind":"http","method","url","headers","bodyB64"}` | 一条 HTTP 请求（body 全量 base64，可为空串） |
| G→C | `{"id","kind":"http-end"}` | 请求体结束 |
| C→G | `{"id","kind":"http-res","status","headers","bodyB64"}` | 响应头 + body（首帧带 status/headers；大响应可分多帧，网关累加） |
| C→G | `{"id","kind":"http-end"}` | 响应结束 |
| C→G | `{"id","kind":"http-error","status","message"}` | 可选，替代 http-res/http-end 表达转发失败 |
| G→C | `{"id","kind":"ws-open","url","headers"}` | 打开一条 WebSocket 流 |
| C→G | `{"id","kind":"ws-open-ok"[,"protocol"]}` | 上游 101 成功 |
| C→G | `{"id","kind":"ws-open-fail","status","message"}` | 上游拒绝（status 透传给手机） |
| 双向 | `{"id","kind":"ws-msg","op":1\|2,"b64"}` | WS 数据帧；`op` 可省略（默认 1=text，2=binary） |
| 双向 | `{"id","kind":"ws-close","code","reason"}` | 关闭流；code/reason 可省略（默认 1000） |
| C→G | `{"kind":"ping"}` → G→C `{"kind":"pong"}` | 连接器每 20s 一次；网关 60s 无任何帧判离线 |

实现约定：

- `url` 为 origin-form（`/connect?token=...&x=1`）；网关已把 `token` 换成 `launchToken`。
- 网关发给连接器的 headers 为小写、已去掉 hop-by-hop、`sec-websocket-*` 与 `host`
  （连接器用 `127.0.0.1:<internalPort>` 自行发请求，relay 内部再归一 Host/Origin）。
- http 流超时 120s、ws-open 超时 20s，超时后网关向手机返回 504/502 并清理流。
- 单桌面并发流上限 256，超限 429。
- 首版 HTTP body 全量 base64（简单可靠）；大文件流式后续再优化。
- 连接器断开/心跳超时：网关把该 token 标记离线（保留 30min），进行中的流立即以
  503/1011 结束；同 token 重连立刻恢复。

## 多桌面与 token 模型

- 每个桌面 relay 生成一个 **32 hex remoteToken**（可轮换，见 relay 侧管理页）。
- 注册不要求白名单：**token 即凭据**。可选 `REGISTER_SECRET` 再加一层共享密钥。
- 手机配对信息 = `网关 host:9443 + remoteToken + 网关 fp + 桌面名`；
  App 走的仍是标准官方 Remote 协议，网关负责把 token 换成 launchToken。
- 桌面重启后 `launchToken` 变化：连接器重新注册时网关自动清空该桌面的 cookie 索引，
  手机需要重新 `/connect`（App 重连时会做）。

## 安全设计

- **公网只暴露 9443/tcp**；桌面 relay 不再需要 SSH 反向隧道或公网映射。
- 自签证书 + **SPKI 固定**：App 按 identity.fp 校验；换证书（`-RenewCert`）后必须重新配对。
- **日志脱敏**：所有日志只出现 `token 前4…后4`（`maskSecret`），`launchToken` 与 cookie
  值从不写日志；错误页也只显示掩码 token。实测 journal 中查不到完整 token/launchToken。
- **cookie 路由隔离**：一个请求混入两个桌面的 `dsh-auth` cookie 会被 400 拒绝；未命中索引
  的 cookie 404，避免误路由。
- **资源下载面最小化**：只暴露各分类目录内白名单普通文件（无目录列表、无任意文件、
  防路径穿越/符号链接逃逸）；页面无脚本 + 严格 CSP；索引/页面不含 token 或服务器绝对路径。
- **上传面鉴权**：`PUT/POST/DELETE /__gateway/resources/<id>/*` 只接受
  `Authorization: Bearer <UPLOAD_TOKEN>`；未配置令牌时端点 404 禁用；令牌比较用
  `timingSafeEqual`；流式写临时文件 + 大小上限 + 原子 rename；`keepVersions` 保留策略
  只作用于该 ID 目录内的白名单普通文件（上限 3 个）；旧 kind 上传路径一律 400
  `id-required`（不能借旧路径绕过 ID/保留策略）；令牌从不落日志/页面。
- **写权限收窄**：systemd `ReadWritePaths` 仅 `/opt/dsh-remote/apk` 与
  `/opt/dsh-remote/resources`；`NoNewPrivileges`、`PrivateTmp`、`ProtectSystem=full`、
  `ProtectHome=read-only` 保持。
- 不记录访问日志中的 query（`/connect?token=...` 的 token 不落日志）。

## 排障

| 症状 | 排查 |
|---|---|
| `/__gateway/identity` 打不开 | `systemctl status dsh-remote-gateway`；`journalctl -u dsh-remote-gateway -n 50`；`ufw status \| grep 9443`；端口占用 `ss -ltnp \| grep 9443` |
| identity `tunnels: 0` | 桌面 relay 未开启 gateway 模式/连接器未连上；看 relay 日志（`[remote-relay] gateway: ...`），确认 URL/fp/token/secret |
| `/connect` 404 `unknown-token` | token 未注册（桌面离线或 token 轮换过）——先看 `tunnels`，再核对配对信息 |
| `/connect` 404 `desktop-offline` | 连接器断了；桌面网络/relay 进程问题；30 分钟内重连可原地恢复 |
| `/connect` 401/403（HTML/JSON 来自 DSH） | 网关替换后的 launchToken 已失效（桌面 DSH 重启过），等 relay 重新注册/重写 pairing |
| `/api/*` 404 `unknown-session` | 没先 `/connect` 或 cookie 丢了；App 重连应重新 `/connect` |
| `/api/*` 400 `ambiguous-cookie` | 同一 cookie jar 混了多个桌面的 `dsh-auth`；清除对应用 cookie |
| `/api/*` 503 `desktop-offline` | 桌面刚掉线；恢复连接后重试 |
| WS 一直不 101 | 桌面 connector 未响应 ws-open（内部监听/relay 异常）；看网关 `journalctl` 的 `ws-open ... timed out` |
| 下载 404 | 文件名不在白名单/未同步；`ssh ... ls -l /opt/dsh-remote/resources/dsh-remote`，重跑 `scripts/sync-apk.ps1`（旧 `/opt/dsh-remote/apk` 仅作兼容回退） |
| 资源 404 | 文件名不在全局白名单/分类目录不对；`ssh ... ls -l /opt/dsh-remote/resources/<id>`；未知 ID 也是 404；详见 `服务器文档/上传下载运维.md` |
| 索引 `?id=` 400 `unknown-id` | id 不在注册表 `dsh-remote\|smartbill\|relay\|docs`；响应里的 `allowedIds` 列出可用 ID |
| 上传 400 `id-required` | 用了旧的按 kind 上传路径（`/__gateway/resources/apk\|image\|misc/...`）；改用 `/__gateway/resources/<id>/<name>?keepVersions=0` 或 `=1` |
| 上传 400 `invalid-keep-versions` | `keepVersions` 只接受 `0\|1`（及 `true/false/yes/no/on/off`），其它值被拒绝 |
| 上传 403 | 令牌缺失/错误：`ssh ... grep '^UPLOAD_TOKEN=' /etc/dsh-remote/gateway.env`；`scripts/upload-resource.ps1` 可自动读取 |
| 上传 404 `uploads-disabled` | 服务端未配置 `UPLOAD_TOKEN`：`scripts/deploy-gateway.ps1 -Restart` 生成（或 `-UploadToken` 指定） |
| 上传 413 | 超过 `MAX_UPLOAD_BYTES`（默认 200 MiB）：调大 env 后重启，或压缩/分卷 |
| 证书/指纹不对 | `-RenewCert` 后重新配对；identity.fp 必须等于 `openssl x509 -pubkey \| openssl pkey -pubin -outform der \| openssl dgst -sha256` 结果 |
| 部署报 9443 被占用 | 旧 `scripts/vps-tunnel.ps1` 隧道还在：`pwsh -File scripts/vps-tunnel.ps1 -Action stop` |

升级/回滚：更新 `vps/` 后 `pwsh -File scripts/deploy-gateway.ps1 -Restart`；
回滚 = 用旧版本代码重跑同一脚本（配置/证书不变）。

## 迁移与兼容（v0.3.0 → v0.4.0）

1. **代码/配置**：更新 `vps/` 后 `pwsh -File scripts/deploy-gateway.ps1 -Restart`；
   部署脚本只创建 `/opt/dsh-remote/{gateway,apk,resources}`（不再按 kind 建子目录），
   env（`APK_DIR`/`RESOURCE_DIR`/`MAX_UPLOAD_BYTES`）不变。
2. **数据迁移**（Lead 部署时执行，实测数字见 `服务器文档/部署与验证记录.md`）：
   - `/opt/dsh-remote/apk/dshremote-*.apk` → `resources/dsh-remote/`，按“保留最近 3 个”归档其余；
   - SmartBill APK → `resources/smartbill/`；
   - `resources/relay`、`resources/docs` 原地成为分类（无需搬动）；
   - 旧 `image`/`misc` 目录中的文件按需迁入 `docs` 或归档，旧目录可保留只读；
   - `/opt/dsh-remote/apk` 清空保留（仅作兼容下载回退与回滚兜底）。
3. **脚本**：`sync-apk.ps1` 改走上传 API（`-Id` 默认 `dsh-remote`、默认保留 3 个版本、
   `-NoKeepVersions` 关闭）；`upload-resource.ps1` 的 `-Kind` 改为 `-Id`（新增
   `-KeepVersions` 开关）；SmartBill 发布脚本改用 `?id=smartbill` / `?keepVersions=1`。
4. **兼容**：旧下载直链（含 `/__gateway/download/<file>.apk`、
   `/__gateway/resources/apk|image|misc/...`）继续可用；旧 kind 上传路径 400
   `id-required`；`?kind=` 被忽略；回滚 = 用旧版 `vps/` 重跑部署脚本（旧 `APK_DIR` 仍保留）。

## 实测记录（2026-11-09，VPS 203.0.113.10）

| 验证项 | 结果 |
|---|---|
| `curl -k https://203.0.113.10:9443/__gateway/identity` | 200；fp=`012345…89abcdef` 与远程证书 SPKI 一致；`url=wss://203.0.113.10:9443` |
| `/__gateway/health` | `{"ok":true,...,"node":"v22.22.1"}` |
| UFW / 外网可达 | 桌面直接访问公网 IP:9443 成功（非 VPS 回环） |
| 无桌面时 `/connect?token=bad` | 404 JSON `unknown-token`；缺 token 400 |
| 假连接器（`_scratch/gateway-gwprobe.mjs`）×2 注册 | identity `tunnels=2`；注册/替换/心跳均正常；**53/53 项检查 PASS** |
| `/connect` token→launchToken 替换 | 连接器侧看到 URL 只剩 launchToken，remoteToken 未泄漏；`interactions=none` 保留 |
| cookie 映射后续 `/api/x` | 两个 token 各自路由到对应假桌面，cookie/query/body 完整透传 |
| 双 token 不串流 | 两个 cookie 同时出现 → 400；未知 cookie 404 |
| WS `/api/remote.mux` | 101；文本 + 二进制帧 echo 均完整往返；未知 cookie → 404 |
| APK 下载 | 页面 200；`dshremote-1.1.0-12-arm64-release.apk` 35,469,848 bytes，`x-content-sha256` 与下载内容/本地磁盘一致（sha256 `dcb3caf0…5ac8`）；HEAD 正常；`../gateway.env` → 404 |
| 离线/断线 | 杀掉假连接器后 identity `tunnels=0`，`/connect` 404 `desktop-offline`（中文错误页） |
| 心跳超时 | 本地以 `TUNNEL_SILENCE_TIMEOUT_MS=2500` 验证：停止 ping 后桌面被判离线 |
| REGISTER_SECRET | 未带/带错 secret → 握手 401；带对 secret 正常注册 |
| `systemctl restart` | 2s 内自动恢复，identity 正常；`enabled` 开机自启 |
| 日志脱敏 | journal 中不存在完整 remoteToken/launchToken；只出现 `5582…0ce9` 形式掩码 |
| VPS 现有服务 | xray(443)/subsvc(8444)/sshd(22222) 仍 active，未改动 |
| 部署脚本 | 首次部署、`-Restart` 更新、`-WhatIf` 计划、证书复用均实测通过 |

真桌面 relay 连接器联调（`_scratch/gateway-live-check.mjs`，**不注册假连接器**，直接使用
relay-dev 配置在运行中的桌面 relay 连接器，pairing v2 / `mode=gateway`）：

| 检查 | 结果 |
|---|---|
| `pairing.json` schemaVersion / mode / fp | 2 / `gateway` / 与 identity.fp 一致 |
| gateway `tunnels` | 1（真实桌面在线） |
| `https://203.0.113.10:9443/connect?token=<remoteToken>&interactions=answer` | **302 + `dsh-auth…` cookie**（真实 DSH） |
| `POST /api/session/list`（带 cookie，经网关→连接器→relay→DSH） | 200，`result.ok=true`，返回 **241 个真实会话** |
| `wss://…/api/remote.mux` Upgrade | **101**，`Sec-WebSocket-Accept` 正确 |
| 通过该 WS 打开 `$events` 逻辑流 | 收到真实 `{"type":"item","value":{"type":"ready",...}}` |
| 未知 token | 404 `unknown-token`（真实桌面在线时） |

合计 15/15 PASS。注意：`$events`（审批/问答事件流）需要 `/connect` 带
`interactions=answer`；`interactions=none` 时该流不会 ready（M0 连接模式，属 DSH 语义）。
早期用 `relay/src/proxy.mjs startPlainProxy` + `relay/src/gateway.mjs createGatewayConnector`
的手工联调脚本 `_scratch/gateway-live-integration.mjs` 保留作排障工具。

## 资源中心实测记录（2026-11-11，网关 v0.2.0）

部署：`scripts/deploy-gateway.ps1 -Restart`（首次，生成 `UPLOAD_TOKEN`）→
再次 `scripts/deploy-gateway.ps1`（验证令牌保留 + 公网校验回落）。完整记录见
`服务器文档/部署与验证记录.md`。

| 验证项 | 结果 |
|---|---|
| `identity` | 200；version `0.2.0`、tunnels=1、apkCount=10、resources.total=12、kinds `apk:10 relay:1 docs:1 image:0 misc:0` |
| `health` | 200；`ok=true`、`resourceCount=12`、node v22.22.1 |
| `GET /__gateway/download` | 200 `text/html`（12,782 B）；5 类标题、APK 最新主卡片、演示 relay/docs 文件；`<script` 0 次；严格 CSP |
| `GET /__gateway/resources` | 200 JSON；total=12、apk latest sha `e595150d…23f42`、绝对直链、无 `/opt/dsh-remote`、无令牌；`?kind=docs` 正常、`?kind=nope` 400 |
| APK 新直链 / 旧直链 | 均 200，36,420,192 B，sha256 与磁盘一致；HEAD 200；Range 206；`If-None-Match` 304 |
| relay / docs 直链 | 200；sha256 与磁盘/本地文件一致（`2359e3f8…`、`0387c6b5…`） |
| 非法名/穿越/未知 kind | 全部 404 |
| 无令牌 / 错令牌上传 | 403；未配置令牌时 404 `uploads-disabled`（本地单测覆盖） |
| 合法上传 → 下载 → 删除 | 201（sha 三处一致）→ 200 → 删除 200 → 再取 404；无令牌删除 403 |
| `/connect` | 坏 token 404、缺 token 400（未回归） |
| 日志 | journal 中完整 `UPLOAD_TOKEN`/`launchToken` 命中 0 |
| 手机业务 | 部署后 journal 持续 `POST /api/session/* 200`，tunnels=1（隧道未回归） |
| 单测 | `cd vps && npm test`：34 项（33 pass / 1 skip / 0 fail）；`npm run check` 通过 |

说明：本机受限 shell 的 `curl.exe`（Schannel）对所有 HTTPS 报
`SEC_E_NO_CREDENTIALS`，外部侧验证改用 Node TLS 等价完成（22/22 PASS）；VPS 侧
`curl -k` 为真 curl 全量通过，正常机器上文档命令按原样可用。

## v0.4.0 迁移与验证（2026-11-13，已部署并回填）

VPS 于 2026-11-12 17:45:14 UTC 迁移（`scripts/migrate-v040-resources.sh`），17:45:59 UTC
重启 v0.4.0；迁移后分类计数 `dsh-remote=3 / smartbill=3 / relay=1 / docs=1`（total=8，
apk=6）。部署后 72 秒并发会话用新版 `sync-apk.ps1` 真实上传 `1.6.1-33`（keepVersions=1，
pruned=1），线上 dsh-remote 变为 `[1.6.1-33, 1.6.0-32, 1.5.2-31]`。完整记录见
`服务器文档/部署与验证记录.md` §10。

| 验证项 | 实测结果 |
|---|---|
| `identity.version` | `0.4.0` |
| `identity.resources.categories[].id/count` | `dsh-remote=3, smartbill=3, relay=1, docs=1`（total=8，byId 同步） |
| `identity.download.count` vs 索引 `.apk` 总数 | `6` vs `6`（一致；health.apkCount 同为 6） |
| `GET /__gateway/download` | 200；`<nav class="chips">`/`class="chip"`/`chip-count` 命中 0；四张分类 ID 卡片；CSP 严格 |
| `GET /__gateway/resources` | 200；`categories[].id` 顺序、`counts.byId`、`latest`/`files`、绝对 `url` 正确；无令牌/绝对路径 |
| `?id=dsh-remote` / `?id=nope` | 200 过滤 / 400 `unknown-id` + `allowedIds`；`?kind=` 被忽略不报错 |
| 旧/新下载直链 | 8 个文件 canonical HEAD 全 200、`x-content-sha256` 一致；旧 APK 直链 6/6 200；Range 206、304、416 正常 |
| 旧 kind 上传路径 PUT | 400 `id-required`（apk/image/misc 一致）；旧下载页 PUT 405 |
| `keepVersions=1` ×4 次上传 | 本地临时网关实测 4→3、`pruned` 1（PASS 17/17）；线上同策略由并发 `1.6.1-33` 上传真实验证（pruned=1） |
| `keepVersions=0` 再上传 | 本地实测只剩 1 个（线上不做破坏性测试） |
| 无令牌/错令牌上传 | 403（带 `www-authenticate: Bearer`）；未配置令牌 404 `uploads-disabled` |
| `invalid-keep-versions` | 400 + `allowedValues:["0","1"]`，不创建文件 |
| 页面/索引/日志 | 无令牌、索引无绝对路径；journal 无完整令牌/launchToken；tunnels=1、手机业务 200 |
| 单测 | `npm run check` exit 0；`npm test` 53 tests / 52 pass / 0 fail / 1 skip（Windows 沙箱符号链接 EPERM；真实仓库非沙箱 53/53） |
| 线上写入复验 | docs 上传 `verify-*`（keepVersions=1）→ 回读 sha 一致 → DELETE → docs 恢复 1 个 `gateway-readme.md`，无残留 |

## 与 relay 连接器实现的最终协议差异

与 `relay/src/gateway.mjs`（relay-dev）比对后，双方一致的字段如上表；差异（均为向后兼容的
可选扩展，不改必填字段）：

1. **`ws-msg.op`**：网关→连接器会带 `op`（1=text/2=binary，来自手机 WS 帧 opcode）；
   连接器当前忽略并按 payload 猜测 UTF-8/JSON。DSH mux 是 JSON 文本帧，故可用；
   建议后续以 `op` 为准。
2. **连接器→网关的 `ws-msg` 不带 `op`**：网关默认按 text 帧发给手机；如内部监听出现
   binary 帧，连接器应补 `op`。
3. **`ws-open-ok.protocol`**：网关会把它回显为手机的 `Sec-WebSocket-Protocol`；
   连接器可不带。
4. **`ws-open-fail`**：网关取 `status` 作为手机侧 HTTP 错误码（缺省 502），`error` 字段忽略。
5. **`http-error`**：网关支持该可选帧（连接器暂未使用，失败时用 502 http-res 也可）。

## 已知限制 / 后续优化

- 首版 HTTP 全量 base64 缓冲：不支持大文件上传/流式响应；请求 32MiB、响应 64MiB、
  WS 消息 16MiB 上限（可按需调 env）。SSE/长轮询类端点建议后续换成流式帧。
- 单进程、内存状态：重启后所有隧道需重连（连接器自动重连），cookie 索引重建。
  cookie 索引每个桌面最多保留 512 条（插入顺序淘汰最旧），防止长期运行内存增长。
- 无 HTTP/2、无 SNI 多证书（单域名/IP 一个自签证书）。
- 桌面连接器断线重连由 relay 侧负责（指数退避）；网关只保留 30min 离线记录。
- 资源中心：单 `UPLOAD_TOKEN` 拥有全部分类写权限；无断点续传/分片（单文件默认上限
  200 MiB）；保留策略只有“最近 3 个 / 1 个”两档，无按时间过期或按版本号保留规则；
  旧 `APK_DIR` 仅作兼容下载回退（页面/索引不扫描）；新增 App/发布线需改
  `vps/resources.mjs` 并重新部署；文件名限 ASCII 白名单；
  页面首次渲染需为缺失 sha256 的大文件计算摘要（有缓存，随 mtime/size 失效）。
  资源相关端点与隧道/路由完全解耦，互不影响。
