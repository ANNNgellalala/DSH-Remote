# 升级与兼容性验证

DSH 的 Remote 协议是**预发布契约（当前 0.2.2-rc.1，无兼容承诺）**。任何一方升级后，按本文顺序验证；出现不兼容时 relay 的自检会明确报告，而不是让手机静默失败。

三条升级路径互不相同，请对号入座：

| 升级对象 | 会发生什么 | 手机侧影响 |
|---|---|---|
| **DSH 桌面版** | 进程重启；`webServer.port` 与 `launchToken` 都变；relay 插件随 profile 重新加载并重写 `pairing.json`（`dshVersion`/`capabilities` 刷新） | 旧 pairing 的 token/端口失效 → 重新加载 pairing |
| **relay 插件** | HMR 热重载（不重启桌面）；端口/证书配置不变时 token 不变 | 通常无感；pin 的 `fp` 不变 |
| **手机 App（APK）** | 覆盖安装 | 无（配对存在 App 内） |

---

## 1. 升级 DSH 桌面版

1. 安装新版本并启动 DSH，确认桌面 GUI 正常。
2. 等待 relay 重新加载（插件在 profile patch 里，启动即加载）。检查 health：

   ```powershell
   curl.exe -k https://<host>:8443/__relay/health
   ```

   关注三件事：

   - `relayVersion`：插件版本（升级 relay 后应变化）
   - `dshVersion`：应等于新 DSH 版本；若为 `unknown` 说明版本解析路径变了（不影响配对，但请记录）
   - `protocolProbe.status`：`ok` / `failed`，以及 `capabilities`、`missing`、`errors`

3. 跑 relay smoke（会校验 pairing active、五端点能力、/connect、session/list、WSS）：

   ```powershell
   pwsh -File scripts/relay-smoke.ps1 -IncludeWs
   ```

4. 跑协议客户端与单测：

   ```powershell
   dotnet test app/tests/DshRemote.Core.Tests/DshRemote.Core.Tests.csproj --nologo
   dotnet run --project app/tools/DshRemote.Smoke/DshRemote.Smoke.csproj -- --loopback --sessions 5 --history 5
   ```

5. 把新 `pairing.json` 推到手机（token/端口已变）：

   ```powershell
   $dir = '/sdcard/Android/data/dev.dshremote.app/files'
   adb shell mkdir -p $dir
   adb push "$env:USERPROFILE\.dsh\remote\pairing.json" "$dir/pairing.json"
   ```

   App 内「从文件加载配对」→「连接」。

6. 一键回归（推荐，等价于 3–5 的自动化）：

   ```powershell
   pwsh -File scripts/e2e.ps1 -SkipAndroid
   ```

### 自检失败时怎么读

`health.protocolProbe` / `pairing.json` 的 `error`：

```jsonc
"protocolProbe": {
  "status": "failed",
  "missing": ["session/follow", "$events"],
  "capabilities": { "$connect": true, "session/list": true, "session/events": true,
                    "session/follow": false, "$events": false },
  "errors": ["session/follow: gateway/invocation-unavailable", "$events: no ready frame"]
}
```

- `gateway/*`：端点不存在/参数形状变化 → relay/Core 需要适配（见 `docs/protocol-v1.md` 的 `args`、`_request` 规则）。
- `session/*`：端点存在但会话状态不允许（例如 `session/agent-busy`）——这类**不算**不兼容。
- `$connect` 失败先看 token（DSH 重启后 token 变化，插件应已重写 pairing；若插件没加载则 pairing 会缺失或 `active:false`）。
- `mux upgrade: socket hang up`：DSH 冷启动时 `/api/remote.mux` 路由要等 `appReady` 才注册，属**启动瞬时失败**；relay ≥ 0.6.1 会等 `appReady` 并自动退避重试。若仍看到旧的 failed 结果，先点配对页「重新自检」或 `POST https://127.0.0.1:8443/__relay/probe`（loopback），不必重启。
- `active:false` 时**不要配对**：修好自检后重跑 `scripts/dev-install.ps1`（开发流）或 `scripts/install-relay.ps1 -FromZip ...`（分发流）。

### 回滚 DSH

装回旧版 DSH → 等待 relay 重载 → 重复上面的验证。`pairing.json` 会再次刷新（token 变化 → 手机重新加载 pairing）。

---

## 2. 升级 relay 插件

### 开发机（工作树）

```powershell
# 改完 relay/src/*.mjs 后
node --check relay/src/index.mjs   # 以及其它模块
pwsh -File scripts/dev-install.ps1 -RequireReload   # 断言新实例真的起来了
pwsh -File scripts/relay-smoke.ps1 -IncludeWs
```

说明：仅改 relay 源码不会自动热载（链接包文件在 profile 重组时比较）。重跑 `dev-install.ps1` 会写 reload tick 触发重组；`-RequireReload` 在实例没有重启时直接失败，避免“假成功”。

### 其它机器 / 分发

```powershell
# 1) 在发布机构建包
pwsh -File scripts/package-relay-plugin.ps1   # dist/dsh-remote-relay-<ver>.zip/.tgz + 包内 INSTALL.md

# 1b) 发布到资源中心 relay 分类（标准流程，版本变化时必须做）
pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-<ver>.tgz -KeepVersions

# 2) 目标机安装（bundle；保留已装证书 → 手机 pin 的 fp 不变）
pwsh -File scripts/install-relay-bundle.ps1 -NoBuild -BundleDir .\dist\dsh-remote-relay-<ver> -ForceReinstall

# 3) 验证
pwsh -File scripts/relay-smoke.ps1 -IncludeWs
```

- 版本兼容声明在 `relay/package.json` 的 `engines.dsh`（当前 `>=0.2.2-rc.1`）。DSH 组合行会做兼容性检查；不匹配时插件可能根本不加载 → 先升级 relay 包。
- **中继服务器配置**（relay 0.4.0+）保存在 `~/.dsh/remote/relay-config.json`，升级插件/重启 DSH 都不会丢；
  升级后可用 `pwsh -File scripts/relay-config.ps1` 查看，或 `health.gateway.source=file` 确认已读取持久化配置。
  修改地址/端口/API 路由端点无需重装，见 [relay-config.md](relay-config.md)。
- **自动审批规则**（relay 0.7.1+）保存在 `~/.dsh/remote/auto-approve.json`，升级插件/重启 DSH 都不会丢；
  升级后确认 `health.relayVersion=0.7.1` 且 `health.autoApprove` 存在。手机端旧「自动允许审批」开关已被
  删除，需升级到 App 1.8.3+ 使用新的桌面端范围设置。详见 [auto-approve.md](auto-approve.md)。
- 卸载：`pwsh -File scripts/uninstall-relay.ps1`（幂等；热卸载并释放端口）。需要清理插件目录/配对时加 `-RemovePluginDir -RemovePairing`。

### relay 升级后手机何时需要重新配对

| 变化 | 手机是否需要重新配对 |
|---|---|
| 仅代码升级，端口/证书不变 | 否（token 也未变，HMR 后 pairing 内容基本不变） |
| 换了 `-BindHost` 或 `-Port` | 是（`/connect` 地址变化） |
| 重新生成证书（新私钥） | 是（`fp` 变化，pin 失败） |
| 仅替换证书文件、私钥不变 | 否（`fp` 是 SPKI） |
| 修改中继服务器 IP/端口/API 路由端点（`relay-config.json`） | 是（top-level host/port 变化；重连后 pairing 自动重写，手机重新扫码/加载配对） |
| 桌面 DSH 重启 | 是（token 变；重新 push pairing 即可） |

---

## 3. 升级 App（APK）

**标准发布（强制）**：一条命令构建、签名、上传 VPS 并校验回读 sha256；只本地构建的包不得作为发布包。

```powershell
pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.8.3 -VersionCode 42
# → dist/apk/dshremote-1.8.3-43-arm64-release.apk
# → https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.8.3-43-arm64-release.apk
```

- 密码来源：`-StorePass`、`DSHREMOTE_KEYSTORE_PASS` 或 `app/keys/local-password.txt`。
- `-NoPublish` 仅用于本机调试/模拟器；发布包必须经过上传与回读校验。
- 上传保留最近 3 个版本；需要只留当前版时加 `-NoKeepVersions`。

2. 覆盖安装：`adb install -r -t dist/apk/dshremote-1.8.3-43-arm64-release.apk`
   - 报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` = keystore 换了 → 只能卸载重装（App 内配对需重新加载）。
3. 配对存在 App Keystore 中，通常无需重新加载；若 relay 侧 token 变了则按 §1 重新 push pairing。

### 用下载页升级（手机不在电脑旁，无需 adb）

relay 的下载页会自动扫描新构建的 APK（无需重载 relay），VPS 资源中心也会显示标准发布上传的版本：

```powershell
pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.8.3 -VersionCode 42
pwsh -File scripts/show-download.ps1        # 打印 https://<host>:8443/__relay/download
```

手机浏览器打开下载页 → 「下载最新版 APK」→ 安装（ColorOS 安装增强防护：更多 → 开始深度扫描 → 继续安装）。
页面会显示最新版版本号/大小/sha256；下载接口返回 `X-Content-SHA256`，可核对。旧版本仍保留在页面「历史版本」里可回退安装（降级需先卸载，因为 APK 不允许降级覆盖）。

注意：

- 同一 `versionCode` 不能覆盖已安装版本 → 新构建务必递增 `-VersionCode`。
- 下载页只暴露白名单 `*.apk`，无需 token；`/__relay/pair` 仍是仅本机可访问，配对仍在电脑上扫码。
- relay 未运行或手机不在同一网段时页面不可达（见 §5 故障对照）。

签名密钥注意：

- `app/keys/release.keystore` 是**唯一**发布签名，丢了就无法覆盖升级（只能换包名或卸载重装）。备份到密码管理器/CI secret。
- 明文密码不要进仓库；本机开发用 `app/keys/local-password.txt`（同样被 git 忽略）。

---

## 4. 兼容性矩阵与发布记录

升级后在发布说明/交接记录里补齐一行：

| 日期 | DSH 版本 | relay 版本 | Core/App | 自检结果 | 备注 |
|---|---|---|---|---|---|
| 2026-11-08 | 0.2.2-rc.1 | 0.2.0 | M0 | `$connect/session/list/session/events/session/follow/$events` 全 true | M0 真机验收 |
| … | … | … | … | … | … |

判断“能发布”的最低条件：

- `relay-smoke.ps1 -IncludeWs` 全 PASS
- `protocolProbe.ok = true`（五个能力位全 true；无会话时 `session/events`/`session/follow` 允许 `unknown`）
- `dotnet test` 全绿
- `e2e.ps1 -SkipAndroid` PASS（Android 步骤在真机可用时再跑全量）

---

## 5. 故障对照

| 症状 | 最可能原因 | 处理 |
|---|---|---|
| DSH 升级后 health 一直 404/连不上 | relay 插件没被加载（patch 丢失/engines 不匹配/端口占用） | `Get-NetTCPConnection -LocalPort 8443`；重跑 `dev-install.ps1`；看桌面日志 `[remote-relay] ERROR` |
| `dshVersion` = `unknown` | 打包布局变化，版本解析候选路径都失效 | 记录该版本；不影响功能；必要时更新 `relay/src/probe.mjs` 的候选路径 |
| `protocolProbe.status=failed` 但手机还能用 | 探测太严格（例如某端点在新版本改参数名） | 以 `errors` 为准更新 probe/Core；短期可先不阻断，但要在发布说明标注 |
| relay 升级后手机静默失败 | pairing 未刷新（旧 token） | 重新 push pairing；确认 `active:true` |
| `uninstall-relay.ps1` 后 8443 仍监听 | HMR 未触发/桌面未运行 | 等几秒；确认 patch 块已删除；必要时重启 DSH |
| Release APK 变大/崩溃 | 裁剪策略或 ABI 变化 | 用 `-LinkMode None` 对照；把差异写进发布说明 |
