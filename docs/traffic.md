# 手机端流量优化（2026-11-12，App 1.5.2 / relay 0.6.0 / gateway 0.3.0）

本文记录六项降流量改造的设计、实测数据与扩展方式。目标：手机经公网网关使用 DSH Remote 时，冷启动/重连/切会话不再反复传输整份会话列表与整段历史。

## 0. 改造前实测（本机 311 会话的真实 DSH）

| 请求 | 原始 JSON | 说明 |
|---|---:|---|
| `session/list` | **1,060,886 B** | 92.1% 是 App 不用的 `projections`（单会话 `agentTeam` 一项 80 KB）；含 197 个子代理会话，下载后才在本地过滤 |
| 打开会话首屏 | **~2.8 MB** | `session/events` latest(30) 1.19 MB（200 条事件）+ turn-boundary 读前 1–3 页 + follow snapshot(1) 22 KB + **follow pump 的 snapshot(80) 1.44 MB（与已加载历史完全重复）** |
| 经公网网关 | ×1.33 | HTTP body 与 WS 帧都 base64，且不压缩 |

## 1. App 本地缓存（`app/src/DshRemote.App/Services/SessionCacheStore.cs`）

- 布局（`<StateDirectory>/cache/`）：`sessions.json.gz`（紧凑列表）、`workspace.json.gz`（分组/归档基线）、`sessions/<id>.json.gz`（该会话已渲染窗口 + `lastSeq/cursor/oldestSeq`）、`images/<sha256>.<ext>`（附件字节）。
- 全部 gzip、原子写（tmp + rename）、按时间/体积裁剪（会话 ≤20 个/24 MB，图片 ≤64 MB）。
- **冷启动先渲染缓存再做增量同步**：
  - `MainViewModel.ConnectAsync` 先 `HydrateFromCache()` 画出上次的列表与分组，再后台刷新；`SessionViewModel` 打开会话时先用缓存窗口渲染。
  - 缓存命中时把 `lastSeq/cursor` 通过 `ISessionBaselineClient.SeedFollowBaseline` 种给 Core；`session/follow` 随后只送缓存之后的增量（无缺口时几乎只发 1 条 snapshot；有缺口时 `session/events` 只回补缺口）。
- 失效保护：服务端 `session/state.cursor < cached.lastSeq`（日志被重写/回退）→ 丢弃该会话缓存走全量路径；文件损坏/缺失一律按 miss 处理，永不抛到 UI。
- 扩展位：每个缓存文档带 `Version` + `[JsonExtensionData]` 透传未知字段；新增列表字段不会因旧版本 App 读写而丢失。

## 2. follow 快照去重（Core）

- `DshClientOptions.FollowMaxMessages` 默认 80 → **1**：follow pump 的快照只带 1 条消息预算；漏掉的事件交给现有 `session/events` gap-repair 精确回补，不再每次重连重传整段快照。
- 缓存命中时用 seed 的 baseline 取代 `OpenSessionAsync` 全量快照；`OpenSessionAsync` 默认值保持兼容。
- 扩展位：`ISessionBaselineClient.TryGetFollowBaseline` 可用于诊断；如未来需要“强制重置 baseline”另加 API。

## 3. 会话列表瘦身 + 按需拉取（relay v0.6.0 + Core）

**relay 新增端点**（TLS 监听与网关内部代理都提供，Bearer 鉴权与 ACL API 同款）：

```
GET /__relay/sessions/summary[?includeSubagents=1]
GET /__relay/sessions/summary?sessionId=<id>        # 按需详情（扩展点）
Authorization: Bearer <remoteToken|launchToken>；需携带请求的 dsh-auth cookie（网关按它路由）
```

- relay 用手机请求里的 `dsh-auth` cookie 反代上游 `POST /api/session/list`，裁剪为 schema v1 的 11 个字段并默认过滤 subagent（不新增 Peer）。
- 响应 v1（**扩展空间**：`schema` + `fields` + `counts`，客户端忽略未知字段）：

```jsonc
{"ok":true,"schema":1,
 "fields":["sessionId","updatedAt","running","blank","agentAvailable","title","cwd","model","lastPromptAt","parentSessionId","origin"],
 "generatedAt":"...","cursor":null,
 "counts":{"total":313,"hiddenSubagents":199,"returned":114},
 "items":[{"sessionId":"session-…","updatedAt":1794461102442,"running":true,"blank":false,
           "agentAvailable":true,"title":"…","cwd":"D:\\…","model":"deepseek-v4.1-flash",
           "lastPromptAt":1794461102442,"parentSessionId":null,"origin":null}]}
```

- `?sessionId=` 原样返回该会话的上游 item（含 projections），作为**按需拉取更多字段**的稳定入口；未来新增列表字段时：先扩 `fields`/`schema`（客户端未知字段丢弃，不会崩），再在 App 需要时用 detail 端点或扩字段拉取。`?fields=` 名称已保留。
- Core：`ISessionSummaryClient.ListSessionSummariesAsync()`——完整请求 + 容错解析 + **旧 relay 自动回退** `session/list`（404/405/401/403/坏 JSON/schema≠1/非 2xx 均回退，不抛）；只有网络传输失败才抛 `Codes.Transport`。App 通过 `UsedCompactEndpoint` 区分并在缓存里存 `SubagentsFiltered`。

**实测（本机真实 DSH → relay 0.6.0）**：

| 路径 | 体积 |
|---|---:|
| `session/list`（旧） | 1,060,886 B |
| `/__relay/sessions/summary`（relay 直连） | **35,570 B（3.35%）** |
| 同上，经公网网关 gzip | **7,110 B（0.67%）** |
| `?sessionId=` 详情（扩展点） | 8,781 B |

## 4. 网关手机侧 gzip（gateway v0.3.0）

- 命中条件（`vps/compress.mjs` `maybeCompressResponse`）：请求 `Accept-Encoding: gzip`、方法非 HEAD、2xx 且非 206、body ≥1024 B、`content-type` 为 JSON/`text/*`/`javascript`、无既有 `content-encoding`。
- 响应改写：`content-encoding: gzip`、`vary: accept-encoding`、`content-length` 改为压缩后长度、删除 `etag`（避免编码差异导致缓存校验不一致）。
- App 侧 Core 的 `SocketsHttpHandler` 打开 `AutomaticDecompression = GZip | Deflate`，自动发送 `Accept-Encoding` 并解压；旧版 App 不发送该头 → 网关原样返回，向后兼容。
- 实测 JSON 压缩比 3.6–5.1×（列表 35.6 KB → 7.1 KB；历史页 1.0 MB → 约 228 KB）。

## 5. 图片磁盘缓存（App）

- `SessionCacheStore`：以 `sha256(attachmentId)` 为文件名（内容寻址）存原始附件字节，读取时同时匹配任意扩展名；`SessionViewModel.LoadImageAsync` 先磁盘后网络，命中即不再走 `session/attachment`。
- 上限 64 MB，按最后写入时间淘汰；同一附件跨会话/跨重启复用。

## 6. 首屏 read-ahead 懒加载（App）

- 打开会话只拉 **1 页**（`session/events latest(30)`），不再在打开时补到 turn 边界（此前 completed 最多 3 页、running 最多 8 页）。
- 首次「加载更早」时才执行一次有界的 turn-boundary 补齐（仍保留原 bug 修复：把本轮提示词/插话找回来），之后按普通分页向上加载。
- 行为变化：极长 turn 的会话首屏可能先看不到本轮提示词，向上滚动一次即可补齐（`HasOlder` 提示仍在）。

## 7. 验证

```powershell
# Node 侧
cd relay; npm test            # 66/66
cd ../vps; npm test           # 45 passed / 1 skipped（沙箱 symlink 限制）

# C# 侧（本机 vstest 受限，本仓库用反射 runner）
dotnet build build/test-runner/TestRunner.csproj -m:1
dotnet build/test-runner/bin/Debug/net10.0/TestRunner.dll     # RESULT passed=198 failed=0

# 真实 DSH 验证（relay 0.6.0 内部代理；plain HTTP 便于本机验证）
#   /connect 取 cookie → GET /__relay/sessions/summary（Bearer + cookie）→ 35 KB、无 projections
# 公网网关验证（在 VPS 上对 127.0.0.1:9443）：
#   /connect 302 → GET /__relay/sessions/summary（Accept-Encoding: gzip）
#   → content-encoding: gzip / vary: accept-encoding / 7,110 B（未压缩 35,570 B）
```

## 8. 已知边界

- 列表瘦身依赖 relay ≥0.6.0；旧 relay 自动回退 `session/list`（行为不变，流量回到旧水平）。
- 缓存是“视图缓存”，不是离线数据库：离开桌面时只能看缓存窗口；更老历史仍需桌面在线。
- 网关 gzip 只覆盖 HTTP（列表/历史/一元 RPC）；WS 帧当前不压缩——由第 2 项把 WS 快照从 ~1.4 MB 降到接近 0，暂不需要协议级压缩。
- 图片缓存按附件 id 内容寻址，附件改 id（重新上传）会重新缓存，旧文件由 64 MB 预算淘汰。

