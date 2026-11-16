# DSH Remote Relay

DSH 宿主侧薄插件：把桌面进程内只监听 `127.0.0.1` 的官方 Web/Remote 服务，以 TLS 暴露到局域网，或通过出站连接器接到公网网关，并输出手机配对信息。

## 打开配对二维码

在运行 DSH 的这台电脑上（仅本机可访问，自签证书请选择「高级 → 继续前往」）：

- [打开配对二维码页面](https://127.0.0.1:8443/__relay/pair)
- 或者运行：`pwsh -File scripts/show-pairing.ps1`

页面包含：当前桌面名、中继服务器设置表单（IP/域名 + 端口 + 可选 API 路由端点）、服务器地址、Token（掩码/复制/轮换）、v2 网关配对二维码、LAN 配对 URI。
手机端：APP → 桌面管理 → 扫码（或粘贴 URI / 从 `pairing.json` 加载）。

## 修改中继服务器

公网中继服务器地址持久化在 `%USERPROFILE%\.dsh\remote\relay-config.json`，三种改法（保存后立即重连，无需重启 DSH）：

1. 打开上面的配对页 →「中继服务器设置」→ 填 IP/域名、端口、可选 API 路由端点 →「保存并重连」；
2. `pwsh -File scripts/relay-config.ps1 -Host <IP或域名> -Port <端口> [-Path /__gateway/tunnel]`（不带参数=查看，`-Disable`=关闭）；
3. 直接编辑 JSON 文件，保存后插件自动重载。

`GET/POST /__relay/config` 为同一功能的 loopback-only JSON API。旧 profile patch 中的 gateway 配置仅在
`relay-config.json` 不存在时作为回退，并会在首次加载时迁移到 JSON（一次性）。

## 启动自检与手动重测（0.6.1+）

DSH 的 `/api/remote.mux` WebSocket 路由要等 launcher 提交 `appReady`（整棵插件树 boot 完成）后才注册；
在此之前宿主会直接销毁未注册的 upgrade 连接（客户端表现为 `socket hang up`）。relay 在启动自检前先等
`appReady`，并对 `mux upgrade: socket hang up` 一类瞬时失败做 5 次退避重试（0.75/1.5/3/6 s），避免冷启动
把 `$events`/`session/follow` 永久判成不可用、配对页卡在 `active=false`。

- 手动重测：配对页「重新自检」按钮，或 `curl.exe -k -X POST https://127.0.0.1:8443/__relay/probe`
  （仅 loopback；返回最新的 `protocolProbe` 结果）。
- 结果同时写入 `GET /__relay/health` 与 `~/.dsh/remote/pairing.json`；`active=true` 时配对页才显示二维码。
- 关键日志：`[remote-relay] appReady observed; running the protocol self-check`、
  `[remote-relay] self-check OK (…)` / `ERROR self-check FAILED …`。

## Windows 工作区权限守卫（0.5.0+）

新会话首次工具执行前，插件在宿主进程内保证其工作区可被 DSH 沙箱授权（给工作区根补一条可继承的用户完全控制 ACE，其余交给 DSH），
无需审批、每个工作区只做一次、结果缓存在 `~/.dsh/remote/acl-admission.json`，审计写入 `acl-audit.jsonl`。
配置项 `config.aclGuard`、排除规则与验证命令见 [README.zh.md](README.zh.md) 的「3) Windows 工作区权限守卫」。

## 手机紧凑会话列表（0.6.0+）

`GET /__relay/sessions/summary[?sessionId=<id>][&includeSubagents=1]`：需要
`Authorization: Bearer <remoteToken|launchToken>`，用请求自带的 `dsh-auth` cookie 反代上游
`session/list` 并裁剪 projections（默认隐藏 subagent 会话）；`?sessionId=` 返回该会话未裁剪的
上游 item（详情扩展点）。完整契约、字段来源与错误码见 [README.zh.md](README.zh.md) 的
「手机紧凑会话列表」节。

## 说明

- 配对页与配置 API 仅回环地址可用；局域网/公网返回 404，避免 Token 外泄。
- Token 是访问凭据；轮换或桌面 DSH 重启后需重新扫码。
- 网关模式需要公网 VPS 上的 `dsh-remote-gateway`（见仓库 `docs/vps-gateway.md`）。
- 完整中文说明：[README.zh.md](./README.zh.md)
