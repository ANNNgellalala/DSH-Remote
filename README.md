# DSH Remote — 手机远程操控桌面 DSH

用 Android 手机远程查看/继续桌面 DSH（DeepSeek Harness）里正在跑的 Agent 会话：看历史与实时输出、看工具调用、发消息、停止本轮、处理审批/提问。

手机是官方 Remote 协议的另一个 **owner 客户端**；桌面侧只装一个薄插件 `dsh-remote-relay`，它不改桌面绑定（桌面仍只监听 `127.0.0.1`），只额外做 TLS 终止、HTTP/WS 反代、协议兼容自检与配对信息输出。

## 配置与仓库说明

**本仓库不包含任何真实主机、账号、密钥或证书。** 部署/发布脚本从本机配置文件读取这些值：

```powershell
Copy-Item config/dshremote.settings.example.json config/dshremote.settings.json
# 编辑 sshHost / sshPort / sshUser / keyFile / publicHost / lanIp / openSslPath ...
```

`config/dshremote.settings.json` 已被 `.gitignore` 忽略；参数优先级为 **命令行参数 > 本机配置 > 示例占位值**。字段说明见 [config/README.md](config/README.md)。

文档中的地址均为占位示例：`203.0.113.10`（公网）、`192.0.2.10`（LAN）、`TestDevice`（手机）、`DESKTOP-EXAMPLE`（桌面名）。替换成你自己的值后再执行命令。

以下内容不随仓库发布，仅保留在本机：`app/keys/`（签名密钥与密码）、`relay/certs/`（TLS 私钥）、`dist/`（APK 产物）、`docs/evidence/`（真机截图与运行日志，含真实会话内容）、`scripts/_extract*/`（从 DSH 安装包提取的闭源文件）、`OPPOSign/`（第三方签名工具）以及本机的 `NuGet.config`。因此 README 中指向 `docs/evidence/` 的链接在公开仓库里会失效，属于预期行为。

## 当前状态

**M0–M4 全部完成**（2026-11-09），M5（手机自取 APK）已交付。验收矩阵：

| 里程碑 | 交付 | 桌面壳 | Android 模拟器 | 真机 TestDevice |
|---|---|---|---|---|
| **M0** 骨架 + 最小闭环 | relay + Core + App：配对→会话→历史/实时→发消息→停止，TLS pinning | ✅ | ✅ | ✅ |
| **M1** 可读性 | `session/follow` 快照 + 断线 seq 补洞、Markdown、流式打字机、推理折叠、工具卡展开、会话切换、队列/模型/运行态、子代理过滤 | ✅ | ✅ | ✅ |
| **M2** 审批 + 写操作 | `$events` waterfall + `$events/result`、审批托盘（允许/拒绝）、问答卡、steer、队列编辑/删除 | ✅ | ✅（真审批/真提问） | ✅（真审批/真提问） |
| **M3** 移动端体验 | 前台服务、双通知渠道（常驻/审批/完成）、Keystore、深链扫码配对、IME 避让、安全区、返回键、窄屏、列表虚拟化 | ✅ | ✅ | ✅ |
| **M4** 工程化/分发 | relay 自检/打包/安装/卸载、`e2e.ps1`、Release 签名 APK、安装/升级/排障文档 | ✅ | ✅（e2e PASS） | ✅（e2e PASS） |
| **M5** 手机自取 APK | relay LAN 下载页 `https://<host>:8443/__relay/download`（白名单 APK 流式下载 + sha256/HEAD，自动取最新构建）、`scripts/show-download.ps1` | ✅ | ✅（curl 校验） | 待手机回网 |

证据见 [docs/evidence/README.md](docs/evidence/README.md)。真机 M1–M3 补验已完成：`verify-m3.ps1` 全 PASS（FGS/通知/深链/Keystore/insets/返回键）、真实审批与问答、重复气泡修复（uiautomator 计数=1）、IME/深链/前台服务均验证。

> 真机安装提示：ColorOS/OPlus 对**全新安装**会弹「安装增强防护」，adb 安装会被拦（`Failure [-99]`）；在手机上解锁后按提示「更多 → 开始深度扫描 → 继续安装」即可。同签名升级安装无此问题。

### 验收命令

```powershell
# 全链路（relay smoke → 64 单测 → Smoke → APK 构建/安装 → 启动截图）
pwsh -File scripts/e2e.ps1 -Serial <真机或模拟器 serial>

# Release 签名 APK（v1.0.1）；签名密钥/密码不在仓库里，见 config/README.md
$env:DSHREMOTE_KEYSTORE_PASS = '<keystore password>'
pwsh -File scripts/build-release-apk.ps1 -StorePass $env:DSHREMOTE_KEYSTORE_PASS -DisplayVersion 1.0.1 -VersionCode 11
# 产物：dist/apk/dshremote-1.0.1-11-arm64-release.apk（33.78 MB，apksigner verify 通过）
```



## 架构（30 秒版）

```
┌──────────────┐   TLS + SPKI pin   ┌──────────────────────────────┐
│ Android App  │ ─────────────────► │ dsh-remote-relay (宿主插件)    │
│ (Core+UI)    │   /connect, /api,  │  TLS 终止 + 反代 + 自检        │
└──────────────┘   /api/remote.mux  │  Host=127.0.0.1:<随机端口>     │
                                    └──────────────┬───────────────┘
                                                   │ http/ws loopback
                                    ┌──────────────▼───────────────┐
                                    │ 桌面 DSH 进程内官方 Web 服务    │
                                    │ 127.0.0.1:54306（不对外）      │
                                    └──────────────────────────────┘
```

- 桌面 DSH 的 loopback 端口与 `launchToken` 都是进程级随机值 → 手机不可能直连，必须有 relay。
- 手机用 `/connect?token=` 换 owner API cookie，之后走 `POST /api/<ns>/<method>` 与 WS `/api/remote.mux`。
- relay 只监听**一个具体的 LAN/VPN 地址**（默认 8443，另绑 `127.0.0.1:8443` 供本机 E2E），**拒绝 `0.0.0.0`/`::`**。

## 快速开始（本仓库开发流）

前置：Windows + .NET 10 SDK + Android workload/JDK 21 + Android SDK（本机 `D:\SmartBillAndroidSdk`，可用 `DSHREMOTE_ANDROID_SDK` 覆盖）、桌面 DSH 正在运行。

```powershell
# 1) 自签证书（SAN 含本机 LAN IP，输出 SPKI 指纹）
pwsh -File scripts/gen-tls.ps1

# 2) 把 relay 装进 desktop profile（junction + 受管 patch 块，HMR 热加载，不重启 DSH）
pwsh -File scripts/dev-install.ps1

# 3) 验证链路：/connect 302 + session/list + WSS 101 + $events ready + 自检能力
pwsh -File scripts/relay-smoke.ps1 -IncludeWs

# 4) 构建并安装 Debug APK 到手机
pwsh -File scripts/build-apk.ps1 -Install

# 4b) 或：让手机自己从 relay 下载 Release APK（无需 adb / 不用在电脑旁）
pwsh -File scripts/show-download.ps1      # 打印 https://<host>:8443/__relay/download

# 5) 扫码配对（推荐）：打开本机 pairing 页，手机系统相机扫码 → dshremote:// 深链拉起 App
pwsh -File scripts/show-pairing.ps1

# 5b) 或用文件推送（App 内「从文件加载配对」读取）
adb push "$env:USERPROFILE\.dsh\remote\pairing.json" /sdcard/Android/data/dev.dshremote.app/files/pairing.json
```

扫码页只在本机开放（非 loopback 访问 404），二维码内容即 `pairingUri`（含 token/fp）。打开 App 后扫码/加载配对 →「连接」→ 选会话 → 发消息 / 停止。

> 📄 **运行/启动规范**（三层各自的标准入口、为什么 relay 不能单独 `node` 起、测试与 E2E、排障对照）见
> [`docs/local-run.md`](docs/local-run.md)。

`pairing.json` 含 `token`（等价密码，勿外传）与 `fp`（证书 SPKI pin）。桌面 DSH 重启后 token/端口变化，插件会自动重写 pairing，手机重新加载即可。

## 安装 / 升级 / 卸载 relay（分发给别的机器 / 别的 $DSH_HOME）

开发流（上面第 2 步）用 junction 指向工作树，改 relay 源码后重跑 `dev-install.ps1` 即可热载。**分发流**用 zip：

```powershell
# 打包：dist/dsh-remote-relay-<version>.zip（不含私钥/证书）+ .sha256
pwsh -File scripts/package-relay.ps1

# 安装（复制到 %DSH_HOME%\plugins\dsh-remote-relay，生成/复用证书，建 junction，写 patch，热加载）
pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-0.2.0.zip -BindHost 192.0.2.10

# 只演练不落盘
pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-0.2.0.zip -WhatIf

# 升级：直接装新 zip（保留已装证书 → 手机 pin 的 fp 不变）
pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-0.3.0.zip

# 卸载（移除受管 patch 块 + junction，热卸载并释放端口）
pwsh -File scripts/uninstall-relay.ps1
pwsh -File scripts/uninstall-relay.ps1 -RemovePluginDir -RemovePairing   # 连插件目录与配对一起删
```

细节：[docs/install.md](docs/install.md)；升级与重新配对：[docs/upgrade.md](docs/upgrade.md)。

## 协议漂移自检（DSH 升级后的兼容性）

`dsh-remote-relay` 启动时会做一次自检，结果同时写入 `/__relay/health` 与 `pairing.json`：

```powershell
curl.exe -k https://192.0.2.10:8443/__relay/health
```

```jsonc
{
  "relayVersion": "0.2.0",
  "dshVersion": "0.2.2-rc.1",          // 从打包 app.asar / profile 解析，拿不到则 "unknown"
  "protocolProbe": {
    "status": "ok",                     // ok | failed | pending
    "required": ["$connect", "session/list", "session/events", "$events", "session/follow"],
    "capabilities": { "$connect": true, "session/list": true, "session/events": true,
                      "session/follow": true, "$events": true },
    "missing": [], "errors": [], "checkedAt": "...", "sessionSample": "..."
  }
}
```

- 自检失败不会静默降级：`pairing.json` 写成 `active:false` + `error:{code:'protocol-self-check-failed', missing, errors}`，并在日志打印 `[remote-relay] ERROR ...`。
- 自检在 DSH 冷启动时会先等 launcher 的 `appReady`（`/api/remote.mux` upgrade 路由在其后才注册），并对 `mux upgrade: socket hang up` 一类瞬时失败做退避重试；配对页有「重新自检」按钮（`POST /__relay/probe`），无需重启 DSH 即可重跑（relay ≥ 0.6.1）。
- `scripts/relay-smoke.ps1` 会校验 `active`、`protocolProbe.ok` 与五个能力位；`scripts/e2e.ps1` 把它作为第一步。
- DSH 升级后的标准动作见 [docs/upgrade.md](docs/upgrade.md)：重启 DSH → 确认 health 版本/自检 → `relay-smoke.ps1 -IncludeWs` → `dotnet test` → 手机重新加载 pairing。

## Release APK 签名与构建（标准发布流程）

> **标准流程（强制）**：凡是装到手机上的 APK，必须走一条命令完成「构建 → 签名 → 上传 VPS →
> sha256 回读校验」。只本地构建、不上传的包仅用于调试/模拟器，不得当作发布包。

```powershell
# 一次性生成签名密钥（app/keys/release.keystore，git 忽略；密码存入密码管理器/CI secret）
pwsh -File scripts/gen-signing-key.ps1
$env:DSHREMOTE_KEYSTORE_PASS = '<password>'   # 或放在 app/keys/local-password.txt（git 忽略）

# 标准发布：构建 + 上传资源中心 dsh-remote + 校验回读 sha256
pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.8.5 -VersionCode 45
# → dist/apk/dshremote-1.8.5-45-arm64-release.apk
# → https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.8.5-45-arm64-release.apk
```

- `release-apk.ps1` 内部调用 [build-release-apk.ps1](scripts/build-release-apk.ps1)（签名/体积/apksigner 校验），
  再用 [sync-apk.ps1](scripts/sync-apk.ps1) 上传到 VPS 的 `dsh-remote` 分类（默认保留最近 3 个版本）。
- 只构建不上传：`pwsh -File scripts/release-apk.ps1 ... -NoPublish`（仅调试用），或直接
  `pwsh -File scripts/build-release-apk.ps1 ...`。
- 上传后可手动复核：

  ```powershell
  pwsh -File scripts/upload-resource.ps1 -List -Id dsh-remote
  ```

- relay bundle 版本变化时同样必须发布到资源中心 `relay` 分类：

  ```powershell
  pwsh -File scripts/package-relay-plugin.ps1
  pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-<ver>.tgz -KeepVersions
  ```

构建形态：`Release` + `net10.0-android` + `AndroidPackageFormats=apk` + `RuntimeIdentifier=android-arm64`（仅 arm64-v8a）+ `AndroidLinkMode=SdkOnly`（框架裁剪、App/UI 程序集保留）+ 本地 keystore 签名。体积目标 ~20–30MB；超 40MB 会 WARNING 并要求记录原因（见 `docs/install.md` 的排障表）。

实测（2026-11-09，arm64-only + SdkOnly）：**约 33.7MB**（`dist/apk/dshremote-0.2.0-2-arm64-release.apk`，35338776 bytes）— 略高于目标区间，原因是 Avalonia 12 + .NET 10 运行时 + 单 ABI 的实打实体积；`apksigner` 已校验签名（DN `CN=DSH Remote…`）。若要更小可评估 AOT/裁剪策略，但本项目未验证 `Full` 裁剪下的 Avalonia 反射稳定性，发布保持 `SdkOnly`。

## 一键 E2E

```powershell
pwsh -File scripts/e2e.ps1 -SkipAndroid   # relay smoke → Core 单测 → Smoke 工具（--loopback）
pwsh -File scripts/e2e.ps1                # 再加：默认 Debug APK 构建 → adb 安装 → 启动 → 截图
pwsh -File scripts/e2e.ps1 -Serial emulator-5556   # 指定 adb 设备/模拟器（别名 -Device）
pwsh -File scripts/e2e.ps1 -Release       # 改用签名 Release APK（-Apk <path> 可直接复用任意 APK）
```

任一步失败即非零退出，并输出 `PASS/FAIL` 摘要；每步完整日志与截图在 `_scratch/e2e/<timestamp>/`。

## 脚本一览

| 脚本 | 作用 |
|---|---|
| `scripts/gen-tls.ps1` | 自签证书（SAN 含 LAN IP）→ `relay/certs/`，打印 SPKI；`-OutDir` 可指定目录 |
| `scripts/dev-install.ps1` | 工作树 → desktop profile（junction + 受管 patch 块 + HMR 热载 + 健康等待，`-RequireReload` 断言重启） |
| `scripts/relay-smoke.ps1` | TLS `/connect` 302+cookie、`session/list` 200、WS 101、`$events` ready、pairing 自检校验（`--pinnedpubkey` 验 fp） |
| `scripts/relay-config.ps1` | 查看/修改持久化的中继服务器配置（IP/域名 + 端口 + 可选 API 路由端点），保存后立即重连 |
| `scripts/show-pairing.ps1` | 打开本机扫码配对页 `https://127.0.0.1:<port>/__relay/pair`（loopback-only 二维码，手机系统相机扫码 → `dshremote://` 深链） |
| `scripts/show-download.ps1` | 打印 LAN APK 下载页 `https://<host>:<port>/__relay/download` 与最新 APK 信息（`-Json`/`-Open`） |
| `scripts/package-relay.ps1` | relay → `dist/dsh-remote-relay-<version>.zip` + sha256 + 包内 INSTALL.md |
| `scripts/install-relay.ps1` | zip/目录 → `$DSH_HOME/plugins/dsh-remote-relay` + 证书 + junction + patch + 热载（`-WhatIf`、`-NoReload`） |
| `scripts/uninstall-relay.ps1` | 移除受管 patch 块/junction/可选插件目录与 pairing，热卸载 |
| `scripts/e2e.ps1` | 全链路 E2E（relay → 单测 → Smoke → 可选 Android：默认 Debug APK、`-Serial` 指定设备、`-Release` 签名包、`-Apk` 复用） |
| `scripts/run-tests.ps1` | 运行 Core/App 单测；`dotnet test` 的 testhost 在受限父进程环境无法启动时自动切换到进程内 runner（`-Filter`/`-Project`/`-PreferDotnetTest`，见 `docs/build-verification-vs2026.md`） |
| `scripts/gen-signing-key.ps1` | keytool 生成 `app/keys/release.keystore`（PKCS12，git 忽略） |
| `scripts/release-apk.ps1` | **标准发布**：构建签名 Release APK → 上传 VPS `dsh-remote` → 校验回读 sha256（参数：`-DisplayVersion`/`-VersionCode`；`-NoPublish` 仅本地调试） |
| `scripts/build-release-apk.ps1` | 签名 Release APK（`-p:` 属性，不改 csproj）+ 体积/签名校验；`-Publish` 可顺手上传 |
| `scripts/oppo-sign.ps1` | 用 OPPO 官方 **APK Signature Info**（`OPPOSign/`）的输出逻辑生成 APK 签名报告（V1/V2/V3 证书、MD5/SHA-1/SHA-256、OPPO Sign 值）；默认取 `dist/apk` 最新 release APK，`-OutFile` 落盘 |
| `scripts/build-apk.ps1` | Debug APK 构建/安装（M0 起沿用） |
| `scripts/deploy-gateway.ps1` | 部署 VPS 公网网关（scp + env/unit + systemd；`-Restart` 应用代码；`-RenewCert` 换证书） |
| `scripts/sync-apk.ps1` | Release APK → 走上传 API 到 VPS `resources/<id>/`（`-Id` 默认 `dsh-remote`；默认保留最近 3 个版本，`-NoKeepVersions` 只留 1 个；`-All` 按 versionCode 升序上传，最终留最新 3 个） |
| `scripts/upload-resource.ps1` | 资源中心上传/列表/删除（`-Id` 指定分类 `dsh-remote`/`smartbill`/`relay`/`docs`；`-KeepVersions` 保留最近 3 个版本；Bearer 令牌自动从 VPS 读取；上传后远端/回读 sha256 校验） |
| `scripts/relay-common.ps1` / `android-env.ps1` | 脚本共用库（profile/patch/junction/健康检查、SDK/JDK 解析） |

## 仓库结构

```
docs/PLAN.md            整体方案（v1）与决策记录
docs/protocol-v1.md     手机用到的 DSH 官方 Remote 协议子集（含实测坑）
docs/install.md         从零安装：证书 → relay → 配对 → APK → 真机
docs/upgrade.md         升级 DSH/relay/App 后的验证与重新配对
docs/session-compaction-support.md  会话压缩（surface replace / compaction）支持设计与实施状态
docs/build-verification-vs2026.md   VS 2026 环境构建/测试验证与工具链问题记录
docs/research/          DSH 宿主插件与官方协议调研、Arc/Android 调研
docs/evidence/          真机验收截图
relay/                  dsh-remote-relay：Cordis 薄插件（TLS 反代 + 自检 + 配对）
  src/proxy.mjs         HTTP/WS 反代（Host/Origin 重写、101 透传、健康端点）
  src/probe.mjs         启动自检（五端点探测、版本解析）
  src/pairing.mjs       SPKI/配对 URI/原子写 pairing.json
app/                    DshRemote.sln
  src/DshRemote.Core     官方 Remote 协议客户端（/connect、RPC、WS mux、重连、pinning）
  src/DshRemote.App      Avalonia 共享 UI（配对/会话/聊天/审批/设置）
  src/DshRemote.App.Desktop   桌面壳（调试用）
  src/DshRemote.App.Android   Android 头（前台服务/通知/Keystore）
  tests/DshRemote.Core.Tests  单元测试
  tools/DshRemote.Smoke       协议 E2E 冒烟工具（--loopback / --dev / --prompt / --watch）
app/keys/               本地 release 签名密钥（git 忽略）
OPPOSign/               用户提供的 OPPO 官方 **APK Signature Info**（OPPOSign v1.1.0，签名信息查看/校验；非签名器）
build/oppo-sign/        OppoSignReport.java：复用 OPPOSign jar 的类、无窗口重放同一签名报告（供 scripts/oppo-sign.ps1）
build/link-probe/       Arc/PuraMarkdown 链接/块布局/分帧渲染探针（见 v1.7.0）
scripts/                构建/安装/自检/打包/签名脚本（见上表）
vps/                    VPS 公网网关（Node 零依赖：TLS/多桌面路由/隧道/资源中心）与部署说明
服务器文档/             服务器侧文档：资源中心总体设计、HTTP 接口规范、上传下载运维、部署与验证记录
dist/                   打包产物：relay zip、release APK（git 忽略）
_scratch/               本机构建草稿、E2E 日志与截图（git 忽略）
_work/                  沙箱构建/长截图探针临时目录（git 忽略）
```

## 故障排查（高频）

| 现象 | 排查 / 处理 |
|---|---|
| `dev-install`/`install-relay` 后 health 不通 | 看桌面日志 `[remote-relay]` 行；`HMR 未触发`→重跑脚本（tick 会触发）；`EADDRINUSE`→换 `-Port`；`engines.dsh` 不匹配→升级 relay 包 |
| relay 起来了但 pairing 是 `active:false` | 看 `protocolProbe.missing/errors`：DSH 升级导致端点变化或 `args` 形状变化 → 更新 relay/Core，或回滚 DSH |
| `/connect` 401 | token 过期（DSH 重启换 token）→ 重新读 pairing（插件已自动重写）；确认手机用最新配对 |
| `/connect` 403 | relay 的 Host/Origin 重写被绕过（改了代理？）；确认直连 relay，不要中间再加一层反代 |
| 手机连不上但本机 smoke 通过 | Windows 防火墙：当前以太网为「公用网络」，`DeepSeek Harness.exe` 已有 Public 入站 Allow；换网络后需放行入站 8443 |
| 手机 TLS pin 失败 | 证书已轮换（换了私钥）→ 重新配对；只换证书不换私钥时 `fp` 不变 |
| Release APK 启动即崩 | `AndroidLinkMode=SdkOnly` 与 Avalonia 反射裁剪冲突 → `build-release-apk.ps1 -LinkMode None` 重试（体积变大，记录原因） |
| Release 构建报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | 签名 key 变了 → `adb uninstall dev.dshremote.app` 后重装（`e2e.ps1` 会自动处理一次） |
| `adb install` 报 `Failure [-99]` / logcat 有 `PC install attack detected` / `OPLUS_ADB_INSTALL_CANCEL` | 厂商反诈安装守护拦截 adb 安装（ColorOS/OPPO/一加等），**不是 APK 问题**：把 `dist/apk/*.apk` 推到手机 `Download/`，在手机上手动点安装；或在本机安全设置里关闭厂商安装守护后重试 |
| `e2e.ps1` 的 Core/App 编译失败 | 并行开发中的 `app/src` 可能正在编辑；单测只依赖 Core，Android 步骤会失败——等代码合入或先 `-SkipAndroid` |

## 安全模型（摘要）

- 配对 URI / `pairing.json` 等同密码：只走安全渠道，App 内用 Keystore 存；relay 日志不打印 cookie。
- 自签 TLS + SPKI pin（`fp`）；relay 拒绝通配绑定，只开一个 LAN/VPN 地址。
- 上游固定 `127.0.0.1:<webServer.port>`，Host/Origin 重写；不做任意目标转发。
- 手机是 owner Peer（官方模型 owner 恒放行）；不暴露公网，外网请用 WireGuard/Tailscale 并让 relay 绑 VPN 地址。

## 文档

- [docs/PLAN.md](docs/PLAN.md)：方案与里程碑、决策记录
- [docs/protocol-v1.md](docs/protocol-v1.md)：协议子集与 6 个实测坑（`args` 包裹、`_request` 参数名、IANA 时区、子代理 busy、WS scheme、pairing 热重载窗口）
- [docs/install.md](docs/install.md)：从零安装（15 分钟路径）
- [docs/upgrade.md](docs/upgrade.md)：DSH/relay/App 升级与重新配对
- [docs/auto-approve.md](docs/auto-approve.md)：桌面端自动审批（规则/范围/API/安全边界）
- [docs/release.md](docs/release.md)：标准发布流程（APK/relay 构建、上传 VPS、sha256 回读校验）
- [docs/relay-config.md](docs/relay-config.md)：中继服务器配置（IP/端口/API 路由端点）持久化、三种修改方式、一次性迁移
- [docs/remote-v2.md](docs/remote-v2.md)：公网网关配对/连接器协议（v2）
- [docs/vps-gateway.md](docs/vps-gateway.md)：VPS 网关部署与运维
- [docs/traffic.md](docs/traffic.md)：手机流量优化（本地缓存 / 列表瘦身 / gzip / 图片缓存）与实测数据
- [docs/session-copy.md](docs/session-copy.md)：会话内容复制（整条消息 / 选中文本）与 Arc/宿主的职责划分
- [relay/README.zh.md](relay/README.zh.md)：插件内部结构、配置、生命周期





## M5 增强（2026-11-09）

在 M0–M4 基础上新增：

1. **图片显示与发送**：照片选择器（Android PickVisualMedia，桌面 StorageProvider）、发送前缩略图/可移除、仅图片可发；会话内用户/助手/工具结果图片异步加载（`session/attachment` base64），缩略图 + 点击全屏查看；Core 新增 `IImageClient` 和图片 prompt 内容块。
2. **会话按工作区分类**：会话列表按 workspace 分组（组标题/路径/「＋新建会话」/归档折叠区）；聊天页「会话」抽屉按工作区展示全部会话，点击切换、当前高亮。
3. **新建 / 归档**：工作区行「＋新建会话」；会话行左滑露出「归档」，支持 running 会话 `stopActivity` 自动重试，归档区可「恢复」。
4. **文字重叠修复**：根因是 ArcMarkdownView 在 Android/Skia 上对长段落的渲染会叠层；改为自研轻量 Markdown 渲染（标题/段落/加粗斜体行内代码/列表/代码块/引用），全部使用普通 TextBlock 系列控件，长消息不再叠印。
5. **APK 下载接口**：relay 新增 `GET /__relay/download` 页面与 `GET /__relay/download/<file>.apk` 流式下载（严格白名单、sha256、HEAD），自动扫描 `dist/apk` 取最新版本；`scripts/show-download.ps1` 打印链接。

**下载最新 APK（手机需与电脑同一 Wi-Fi/VPN；自签证书提示选「高级 → 继续前往」）**

- 页面：<https://192.0.2.10:8443/__relay/download>
- 直链：<https://192.0.2.10:8443/__relay/download/dshremote-1.1.0-12-arm64-release.apk>
- 版本：1.1.0 (12)，33.83 MB，sha256 `dcb3caf03cfc0aae704d336bc6733ae3c3d1dc8cc8df16c65dd86f8cff555ac8`
- 安装：ColorOS 全新安装如弹「安装增强防护」，按「更多 → 开始深度扫描 → 继续安装」。

**验证**：`dotnet build` 0/0、`dotnet test` 85/85、`e2e.ps1 -Serial emulator-5556` 8/8 PASS；模拟器实测：图片选择/发送/显示、工作区分组、新建会话、侧栏切换、左滑归档、长消息无叠印、下载页 200 且 APK 字节/sha256 与磁盘一致。真机 TestDevice 也已补验：v1.1.0 安装后工作区分组、左滑归档、系统选择器选图/发送、问答卡、及通过 VPS 公网地址连接均正常（见下文「外网访问」与 `docs/evidence/`）。

## 外网访问（VPS，2026-11-09 部署）

桌面 relay 不变，VPS 只做公网入口 + SSH 反向隧道：

- 公网地址：`https://203.0.113.10:9443`（手机与电脑无需同一局域网）
- 下载页：`https://203.0.113.10:9443/__relay/download`（公网可直接下载 APK）
- 直链：`https://203.0.113.10:9443/__relay/download/dshremote-1.1.0-12-arm64-release.apk`
- 配对：pairing.json 已切到 `host=203.0.113.10 port=9443`，并保留 `localPairingUri`（LAN 直连 `192.0.2.10:8443`）；手机重新加载一次 pairing 即可在外网使用
- 隧道托管：`pwsh -File scripts/vps-tunnel.ps1 -Action start|status|restart|install`（自动重连 + 登录自启）
- 完整部署/运维/安全说明：[docs/vps-deploy.md](docs/vps-deploy.md)

> token 随桌面 DSH 重启变化；重启后重新加载 pairing。桌面关机/睡眠时外网入口不可用。

### 真机 + 公网验证

- TestDevice 安装 v1.1.0 后通过 `203.0.113.10:9443` 连接成功：工作区分组、左滑归档、图片选择/发送、问答卡均正常；列表 18 个会话正常加载。
- 公网健康检查 `ok=true`；公网下载 APK 35,469,848 bytes、sha256 与磁盘一致。

## M6（2026-11-10）插件化 / 多桌面网关 / 移动端体验

1. **插件市场可见（bundle 化）**：relay 0.3.0 已打包为 DSH bundle（`dsh.bundle.patch` + `cordis.patch.yml`），用 `scripts/install-relay-bundle.ps1` 经宿主 `pluginManager/installBundle` 安装到 desktop profile（已实测 `listBundles` 出现 `dsh-remote-relay`、行 active、启停可用）。旧的手写 patch/junction 已由脚本迁移删除；开发期仍可用 `dev-install.ps1`。详见 `docs/research/dsh-plugin-market.md`。
2. **VPS 通用网关（按 Token 多桌面）**：`vps/` 零依赖 Node 网关部署在 203.0.113.10:9443（TLS 自签、systemd、APK 下载），桌面 relay 通过 `wss://…/__gateway/tunnel` 注册；`/connect?token=<remoteToken>` 按 Token 路由并替换为 DSH launchToken，cookie→桌面粘滞，WS 透传；支持多桌面并存、Token 轮换、心跳重连。scripts：`deploy-gateway.ps1`/`sync-apk.ps1`；文档 `docs/vps-gateway.md`。
3. **桌面插件 Token/二维码**：relay 自动生成/持久化 `~/.dsh/remote/identity.json` 远程 Token，`/__relay/pair` 管理页（仅 loopback）显示桌面名、服务器地址、Token（掩码/复制）、v2 配对二维码、LAN URI 与「生成新 Token」；`docs/remote-v2.md`。
4. **App 多桌面管理**：`ProfileStore` + `ProfilesViewModel` + `DesktopsView`（列表/添加/扫码/粘贴 URI/重命名/删除/切换连接），v2 字段（mode/gateway）持久化；连接后显示桌面名称（缺省 token 前缀）；`PlatformServices.ScanQrAsync` Android 扫码（ZXing.Net 解码，18 PASS）。
5. **移动端体验**：
   - 会话懒加载：`OpenSessionLiteAsync(30)` 先画最新页再后台 follow，顶部加载更早（滚动锚点保持）；实测首屏 112ms→33ms（中型）/8814ms→1224ms（冷大历史）。
   - 会话列表顶部改为**工作区选择器**（名称+路径+会话数，持久化 LastWorkspaceId），只显示当前工作区；聊天头部显示所属工作区。
   - **Arc Markdown 渲染**：按方案 B，消息正文改用 `ArcMarkdownView`；App 内置 Noto Sans SC 子集（`Assets/Fonts/NotoSansSC-DshRemote.otf`，~1.6MB）并设置 `ArcMarkdownStyle.Current.BodyFont/DisplayFont`，解决 Android 上 Inter 无 CJK 导致的 tofu 与行高塌陷叠层；真机验证中文正文渲染正常（截图 `phone-m6-arc-markdown.png`）。工具卡/推理/队列/图片暂仍为自研行（Arc Chat/Agent 缺组件，待上游补齐后迁移，清单见 `docs/research/arc-agent-integration.md`）。

**验证**：Core 111 单测；Android 二维码 18/18 + M3 回归 31/31；网关 53/53（假连接器）+ 15/15（真 relay）；VPS 729 真机 v1.2.0 经 `203.0.113.10:9443` 连接成功（2 会话、工作区下拉、桌面管理、中文 Arc Markdown 正常）；Desktop/Android 构建 0/0。发布 APK：`dist/apk/dshremote-1.2.0-13-arm64-release.apk`（35.4MB，sha256 `20e773d5…5eda4`）。

## M6 收尾修复（Arc 长 Markdown，2026-11-10）

- 复现：ArcMarkdownView 单文档同时含**表格 + 代码块**时，Android 上表格行与代码行仍叠层（即使已换 CJK 字体）。不是字体问题，是 Arc 文档级测量缺陷。
- 修复：`MarkdownBodyView` 把最终 Markdown 按空行拆成**顶级块**（fenced code 不拆），每块独立 `ArcMarkdownView` 放入 StackPanel；单块仍直接渲染。块级独立测量避免跨块高度污染。
- 真机验证（TestDevice，v1.2.1）：长中文回答（列表 + 带语法高亮代码块 + 表格）全部正常、无叠层、CJK 字形正确；模拟器同样通过。截图：`docs/evidence/phone-m6-arc-long-markdown.png`、`emulator-m6-arc-long-markdown.png`。
- 新 APK：`dshremote-1.2.1-14-arm64-release.apk`（35.4MB，sha256 `901a3611…a4e6b`），已同步 VPS 网关下载页。

## 2026-11-10 收尾更新（v1.2.2）

- **Arc 重新引用**：App 从 NuGet `Arc.Avalonia 0.9.0-alpha` 改为直接引用本机 `Arc.Avalonia.Meta` 工程（含已修复的 CJK 字体解析），删除内置 Noto Sans SC 字体与 `ArcMarkdownStyle` 覆盖。Arc 仍会在“表格+代码块”混合文档上错误叠层，故保留 `MarkdownBodyView` 的**顶级块拆分**（每块独立 ArcMarkdownView），真机/模拟器长中文 Markdown 验证无叠层。
- **扫码修复**：新增应用内 Camera2 扫码 Activity（TextureView 预览 + YUV 帧 ZXing 解码 + 手电筒 + 相册选择），`ScanQrAsync` 优先内置扫码，系统相机仅作回退。真机 TestDevice 实测相机预览/帧循环/手电筒/真实帧解出二维码（`scratch/m6-android/REAL-DEVICE-EVIDENCE.md`）；模拟器 28 PASS、M3 回归 31 PASS。
- **归档会话隐藏**：会话列表、工作区下拉、侧栏均不再展示归档会话与「已归档」区；归档仍可通过左滑执行（恢复需桌面端处理）。
- **插件详情链接**：bundle 新增 `README.md`，插件详情页显示「打开配对二维码页面」链接（`pluginManager/readBundleDetail` 已验证返回该 Markdown）；配对页仍仅本机可访问。
- 发布 APK：`dshremote-1.2.2-15-arm64-release.apk`（34.12MB，sha256 `48cabad7…66275`），已同步 `https://203.0.113.10:9443/__gateway/download`；真机 TestDevice 已安装并验证（工作区下拉、归档隐藏、长中文 Arc Markdown、内置扫码打开/相机预览）。

## 2026-11-10 自动允许审批（v1.2.3，已于 2026-11-14 移除）

> **已移除**：手机端 `InteractionHub` 的「自动允许审批」开关（以及 `settings.json` 的
> `AutoApproveApprovals`）在 v1.8.0 删除。自动审批改由桌面 relay 值守，规则可按工作区/会话选择，
> 手机 App 关闭后仍生效。见文末「2026-11-14 桌面端自动审批」。

- 聊天页底部新增 CheckBox「自动允许审批」（与「插话模式」同一行）：
  - 勾选后，所有新到的审批请求立即以「允许一次」应答，不再弹审批卡片/不再发审批通知；Agent 的问题（question）仍需人工回答。
  - 开关状态持久化到 `settings.json`（`AutoApproveApprovals`），重启 App/重连后保留；全局生效（对当前所有会话）。
  - 关闭后恢复逐条审批。审批会自动应答并显示“已自动允许「工具名」”提示。
- 实现：`InteractionHub.AutoApproveApprovals` 在 `OnApproval` 中直接 `RespondApprovalAsync(AllowedOnce)`；`SettingsViewModel` 持久化；`MainViewModel` 同步并抑制自动模式下的系统通知。
- 验证（模拟器 debug build）：开启开关后触发真实审批，桌面文件被自动写出、turn 正常结束（无需点击「允许一次」）；开关持久化。
- 发布 APK：`dshremote-1.2.3-16-arm64-release.apk`（33.74MB，sha256 `075fb8bf…1b32`），已同步 VPS 下载页。

## 2026-11-10 v1.2.5：崩溃修复 + Markdown/输入/折叠

- **启动闪退（v1.2.3）根因**：增量 Release 打包出的 APK 里 `lib/arm64-v8a/libxamarin-app.so` 程序集存储不完整（1,009,312 vs 正常 1,382,608 字节），启动即 `UnsatisfiedLinkError: n_onCreate`。`scripts/build-release-apk.ps1` 现在发布前强制清理 Android `bin/Release` 与 `obj/Release`，杜绝再次出现；v1.2.5 已按此构建并用 x86_64 Release 在模拟器验证可正常启动。
- **Markdown 重叠/链接**：`MarkdownBodyView` 渲染前将 `[label](url)` 规范为普通文本 `label（url）`，对裸 URL 在 `/ . _ - ? = & #` 后插入零宽空格允许换行；保留按顶级块拆分，消除 Arc 链接内联样式导致的行高/基线错位与叠印。证据：`docs/evidence/task22-a-markdown-*.png`。
- **全屏输入**：新增全局设置「输入时展开全屏」（settings.json 持久化）；聚焦/输入时全屏编辑层，IME 不遮挡，发送后自动收起；修复收起后重聚焦反复弹开。
- **思考/工具折叠**：新增 `ChatTurnViewModel`；每轮 reasoning + 工具调用/结果 + 中间输出收进 Expander，主区只显示最终回答；运行中「正在执行中 · Ns」实时计时，完成「已完成 · Xs」默认折叠；审批/提问卡片保持在折叠外。
- 验证：Desktop/Android 构建 0/0；Core 111/111 + App 4/4；模拟器截图 `task22-b-*`、`task22-c-*`。
- 发布：`dist/apk/dshremote-1.2.5-18-arm64-release.apk`（33.74→34.12MB，sha256 `4f3f6ad7…677d84`），已同步 VPS；旧崩溃包 1.2.3/1.2.4 已从 VPS 移除。



## 2026-11-10 v1.2.6：Markdown 重叠根因修复 + 独立设置页 + 时间线顺序/插话丢失修复

- **Markdown 文字重叠（问题 1，根因确认）**：不是 Arc 的 markdown 组件缺陷，而是 **DSH Remote 自己的模板问题**。App 为了让 `ArcMarkdownView` 在外层会话滚动区里自适应高度，在 `Theme/AppStyles.axaml` 里把它的 `PART_Host` 重新模板化成了 `Panel`。一个 ArcMarkdownView 有时会渲染多个顶级块（例如「**最新 APK**：」段落紧接着 `- …` 列表、中间没有空行），而 Avalonia 的 `Panel` 会把所有子元素都排在 (0,0) 并按最终尺寸布局 → 段落与列表叠印。修复：`PART_Host` 改为与上游 Arc 主题一致的 `StackPanel`（`Spacing=0`）。桌面渲染探针稳定复现/验证（`docs/evidence/task23-a-markdown-overlap-before.png` / `...-after.png`），模拟器实测「验证与下载」段落 + 列表不再重叠（`task23-a-markdown-fixed-emulator.png`）。
- **独立设置页（问题 2）**：新增 `设置` 页（会话列表头部「设置」按钮 / 会话侧栏底部「设置」），全局设置从聊天底部移入该页：
  - 交互：输入时展开全屏（即时生效并持久化到 `settings.json`）；自动审批（v1.8.0 起为桌面端值守规则，见文末）；
  - 连接：连接状态、桌面管理、重新连接、断开连接；
  - 关于：产品名与版本（版本取自入口程序集；发布构建同时传 `-p:Version`，安装后显示实际版本 1.2.6）。
  聊天页底部只保留「插话模式（发送 steer）」这一按次发送的模式开关；Android 返回键与 ‹ 都能返回上一页。
- **运行折叠排到提问之后（问题 3）**：运行中回合的「▶ 正在执行中 · Ns」折叠块曾显示在用户提问气泡上方。修复：durable `user/message` 回执就地升级乐观气泡（位置不变，不再删除后在末尾重加）；`▶/■` 系统横幅标记 `IsTurnBoundary`、不渲染也不作为折叠锚点；折叠改以首个真实过程行锚定（刚发送 prompt 后仍即时显示运行折叠）。历史页合并改为批量投影一次，显著减少加载后的重复全量重建。
- **重启后丢失插话/首条提问（问题 4）**：DSH 的 `session/events` 按「消息」预算分页，长运行回合的首屏页会从回合中间开始；App 又裁剪到最后 60 条事件，follow 快照只补更新的 seq，因此该回合的提问与 steer 气泡（如 13:28 / 13:33）不在窗口内。修复：打开运行中会话时向后补页到本轮 `turn/start`（≤8 页 / 3000 事件、60 消息/页，best effort，空闲会话不受影响）；超大运行回合的折叠不再自动展开（过程行 > 40 保持收起），避免一次布局数百张卡片导致卡顿/ANR。
- 验证：Desktop/Android 构建 0 warning 0 error；Core 测试 111/111、App 测试 8/8（新增 4 个时间线回归测试）；模拟器（emulator-5556）v1.2.6 Debug 实测：设置页打开/返回/持久化、Markdown 段落+列表无叠印、重启后打开运行中会话顺序为 13:25 提问 → 运行折叠（收起）→ 13:28/13:33 插话气泡，且无 ANR。
- 发布 APK：`dist/apk/dshremote-1.2.6-19-arm64-release.apk`（35,781,216 bytes，sha256 `5f6da2d02f383e3c6b26150685bfa99c7fbb5501af2d22fbd232c659f8d5cc5f`），已同步 VPS 下载页。

## 2026-11-10 v1.2.7：设置页偏好（重启后）不持久化修复

- **现象**：设置页的「自动允许审批」「输入时展开全屏」打开后，重启 App 又回到关闭状态（设置页看起来被重置）；配对/连接本身正常。
- **根因**：Android 头把整份设置文档（含两个偏好）作为配对快照加密存进 Keystore（只在连接成功时写入），而偏好 setter 每次变化都会把整份文档写回 `settings.json`。冷启动顺序是 `Load(settings.json)` → `LoadFromSecretJson(旧快照)` → `ReloadPreferences()`；中间一步把连接时的旧偏好写回文件，最后一步读到的已是被覆盖的值，于是设置页永远停在连接时的状态（桌面头没有 SecretStore，只在手机复现）。
- **修复**：配对快照只保存连接字段（`ToSecretJson` 不再写偏好/工作区，`LoadFromSecretJson` 只回放连接字段）；`SettingsViewModel` 新增 `_isRestoring` 写盘保护，`Load`/`LoadFromSecretJson`/`ReloadPreferences` 回放期间不落盘。`settings.json` 是偏好的唯一权威来源。
- **验证**：新增 `DshRemote.App.Tests/SettingsPersistenceTests`（3 个；修复前旧快照把 true 覆盖成 false → FAIL，修复后 App 11/11、Core 111/111）。模拟器（x86_64 API 36）用真实故障路径复现：把携带旧偏好的 Keystore 快照放回设备，修复前重启 `settings.json` 由 true 变回 false；装修复版后同一快照下重启仍为 true、设置页两个开关仍勾选、连接区显示「已从安全存储恢复配对」。证据：`docs/evidence/task24-settings-*.png`。
- 发布 APK：`dist/apk/dshremote-1.2.7-20-arm64-release.apk`（35,781,216 bytes，sha256 `b5d28080e902e164946c92c2d07904feaa8c98cf9f1538cbe2efd136fc6846b0`），已同步 VPS 下载页。

## 2026-11-10 v1.2.8：实时订阅断线重试有界 + 重新连接入口 + 退出会话不再假死

- **现象（真机截图）**：聊天页实时订阅断开后，错误横幅 `实时订阅已断开: a follow subscription for session '…' is already active; one subscription per session is supported` 每 1.5 s 刷一次，状态一直「订阅断开，正在重试…」；左上角 ‹ 退出会话点不动，App 近乎死机。
- **根因 1（无限重试）**：`SessionViewModel.FollowLoopAsync` 用固定 1.5 s 间隔**无上限**重试，且把「同一会话已有活跃 follow 订阅」这个客户端并发保护错误当成普通传输错误；该错误在重试时立即再次抛出，于是形成永不结束的失败循环。
- **根因 2（为什么会出现重复订阅）**：错误横幅上的「重试」按钮会重新跑整个会话加载（`ReloadCoreAsync`），它与首屏加载、返回/重开同一会话之间没有串行化；两个 follow 消费端同时存在时，只有一个能拿到订阅，另一个就会永久报 "already active"。同时 `DshClient.FollowEventsAsync` 在旧订阅槽位尚未释放时**立即**抛错，且清理订阅表在 `await pump` 之后、非原子，让这个竞态窗口变成永久的毒循环。
- **根因 3（退出会话无响应）**：‹ 返回按钮绑定的是共享 `AsyncRelayCommand`，`BackToListAsync` 在切页后仍 `await` 会话 Dispose 和 `LoadSessionsAsync`（无超时、断网时最长可挂 60 s）；命令一直处于 running 状态，`CanExecute=false` 让按钮直接置灰并丢弃点击。
- **修复**：
  1. **断线重试预算 5 次 × 5 s**：`FollowLoopAsync` 连续失败（含重复订阅、流结束、补齐失败）最多重试 5 次；传输层 `Reconnecting` 由 5 s 看门狗计时（pump 阻塞等新连接时也会计数）。成功收到数据即清零预算。5 次仍失败 → 停止重试并**自动退回连接页面**（同时释放死客户端，避免后台继续重连）。
  2. **重新订阅等待旧槽位释放**：`DshClient.FollowEventsAsync` 最多等 3 s 让上一路订阅清理完再注册，超时才报 `session/follow-busy`（`DshRemoteException.Codes.FollowBusy`，可恢复错误）；释放槽位时只移除属于自己的注册，避免误删新订阅。App 删除就地「重试/整页重载」，`OpenSessionAsync` 串行化，不再产生同一会话的双 follow 消费端。
  3. **重新连接按钮**：错误横幅按钮由「重试」改为「重新连接」，点击直接结束当前聊天、清理客户端并跳到连接页面（用户可选快速路径）；5 次重试耗尽也走同一路径。
  4. **退出会话立即响应**：‹ 返回先切页，再后台释放会话/刷新列表，命令立即结束；`SessionViewModel.DisposeAsync` 对 follow 循环的等待上限 3 s；所有后台重试的 UI 更新都加 `_disposed` 守卫，不再向已关闭页面投递状态。
  5. **补齐循环上限**：连续 resync 补齐（消息缺口）最多 5 次，之后同样退回连接页，附带 250 ms 间隔，杜绝 snapshot→gap→snapshot 的热循环。
- **验证**：Core 115/115（新增 4 个 follow 并发/忙等/服务端流错误/流结束回归测试）、App 14/14（新增 3 个重试预算/重新连接回归测试）；Desktop/Android(Debug) 构建 0 warning 0 error；Release 签名 APK 构建通过。
- 发布 APK：`dist/apk/dshremote-1.2.8-21-arm64-release.apk`（35,793,504 bytes，sha256 `62a50aadfbea0a85affde5964c2b277949f7cbbf7057c7ad5d66168e4991e362`，含本次修复；已同步 VPS 下载页并校验 sha256；覆盖安装即可）。

## 2026-11-10 v1.2.9：会话列表改用 ArcSwipeActions（Arc「可点击行」缺陷修复后）

- **背景**：Arc.Avalonia 修复了 `ArcSwipeActions` 的「可点击行」缺陷——按压改为隧道阶段观察、横向锁定后才捕获指针，轻点会到达 `Content` 里的 Button/Tapped 表面，横拖仍由组件接管（Arc `docs/28 §2.7`，T1–T6 headless 交互探针全 PASS）。DSHRemote 此前因这个限制只能自绘滑动（`SessionsView.axaml.cs` 约 190 行手势状态机）。
- **更新引用/用法**：会话行模板改用 `media:ArcSwipeActions`：`Trailing="{Binding SwipeActions}"`（`ArcSwipeAction`，Tone=Danger，图标 archive，动作=`ArchiveCommand`）、`Label` 取会话标题；行内容放进整宽透明的 `Button.session-row`（`OpenCommand`）——轻点打开会话，横拖露出归档。`FullSwipe="False"` 保持「先露出、再点归档」的旧语义，避免误滑即归档；外观对齐原 `row-card`（`ArcSurface` / `ArcBorderSubtle` / 圆角 18 / padding 14）。
- **删除**：自绘手势（`SwipeThreshold`/`OpenThreshold`/`RowSwipeState`/`SetActionPainted`/`FindArchiveAction`）与 `Button.archive-action` 样式。收起态动作由组件 `IsVisible=false` 负责，不再依赖宿主 `Opacity=0` 兜底，红边透色从根上消失。
- **验证**：Core 115/115、App 14/14；Desktop/Android(Debug) 构建 0 warning 0 error。桌面 `--demo`（436×915 PrintWindow）行外观正常，收起态外圈条带危险色像素 0；模拟器 `emulator-5556` 演示数据实测：轻点行进入会话、左滑露出「归档」、点归档后 toast「已归档」且列表保持当前工作区、收起态像素断言四边均 0、crash buffer 空（证据 `docs/evidence/v129-*.png`）。
- 发布 APK：`dist/apk/dshremote-1.2.9-22-arm64-release.apk`（35,793,504 bytes，sha256 `13c89d2d99d238ecebdca5485c6699ef5bbdca9ab8238bbc13394980b518611a`，含本次修复；已同步 VPS 下载页并校验 sha256）。

**下载最新 APK（手机需与电脑同一 Wi-Fi/VPN，或走 VPS 公网）**

- 页面（资源下载中心，按分类 ID 分组：`dsh-remote`/`smartbill`/`relay`/`docs`）：<https://203.0.113.10:9443/__gateway/download>
- 规范直链（v0.4.0 起）：<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.3.1-24-arm64-release.apk>
- 旧直链（v0.4.0 仍兼容）：<https://203.0.113.10:9443/__gateway/download/dshremote-1.3.1-24-arm64-release.apk>
- 安装：覆盖安装即可（同签名）；ColorOS 全新安装如弹「安装增强防护」，按「更多 → 开始深度扫描 → 继续安装」。

## 2026-11-10 relay 0.4.0：中继服务器改为手动填写 IP/端口/API 路由端点 + 持久化

- **不再固定 VPS**：桌面 relay 插件连接的公网中继服务器地址不再写死在 DSH profile patch 中，
  改为用户手动填写 **IP（或域名）**、**端口**、**可选 API 路由端点**（默认 `/__gateway/tunnel`，
  支持反代前缀），持久化到 `%USERPROFILE%\.dsh\remote\relay-config.json`（原子写、0600）。
- **修改方案（三选一，保存后立即重连，无需重启 DSH）**：
  1. 本机管理页 <https://127.0.0.1:8443/__relay/pair> →「中继服务器设置」表单 →「保存并重连」；
  2. `pwsh -File scripts/relay-config.ps1 -Host <IP/域名> -Port <端口> [-Path /__gateway/tunnel]`
     （不带参数=查看；`-Disable`/`-Enable`；`-Json`）；
  3. 直接编辑 JSON 文件，插件监听该文件并自动重载。
  - 底层 API：`GET/POST /__relay/config`（仅 loopback；LAN/公网 404）。health 增
    `gateway.{host,port,path,source,configFile,configError}`。
- **一次性初始化（本机重装/升级插件）**：若 `relay-config.json` 不存在、而旧 profile patch 里还有
  `gateway` 配置（即原来的 VPS 地址），新插件首次加载时自动把旧值写入 JSON
  （`initializedBy=one-time-migration:profile-patch`），`install-relay-bundle.ps1` 随后删除 patch 里的
  gateway 段落，让 JSON 成为唯一可编辑来源。迁移只搬运本机已有配置、不写死任何 VPS 常量，
  属于一次性动作（`-NoSeedConfig` 可跳过）。
- **兼容**：旧 `config.gateway.url` 写法仍作为回退；优先级 `relay-config.json` > patch > 默认。
- **验证（2026-11-10，本机 DSH 0.2.2-rc.1 + VPS 203.0.113.10:9443；证据：[docs/evidence/relay-config](docs/evidence/relay-config/)）**：
  - `relay/test/config.test.mjs` 15/15 + `relay/test/downloads.test.mjs` 4/4（`node --test`，打包脚本自动执行）；假宿主集成探针 12/12（迁移/API/实时应用/文件监听/内容类型门/初始化标记）。
  - 本机重装 bundle 0.4.0：删除 `relay-config.json` 后运行 `install-relay-bundle.ps1`，自动打印
    `pre-install relay server captured ... -> 203.0.113.10:9443/__gateway/tunnel` 并
    `one-time initialization filled relay-config.json from the pre-install setup`；`listBundles` 可见 0.4.0。
  - 插件 reload（`setBundleEnabled` false→true）后配置文件 sha256 不变、`gateway.source=file`、隧道自动恢复 online。
  - CLI 修改/IP 端口在线切换/`-Disable`→v1→`-Enable`→v2 均实测通过；LAN 访问 `/__relay/config`、`/__relay/pair` 均 404。
  - `relay-smoke.ps1 -IncludeWs`（LAN）与 `-Gateway -IncludeWs`（经 VPS 公网）均 PASS。
- 详细说明：[docs/relay-config.md](docs/relay-config.md)。

## 2026-11-11 v1.3.0：跟随 Arc/PuraMarkdown 重构更新引用 + 会话重开历史/文件展示修复

- **引用更新（Arc 与 PuraMarkdown 重构后）**：
  - **Avalonia 12.1.4 → 12.1.5**（App / Desktop / Android 三处直接版本）。Arc 重构后所有包按 12.1.5 编译，App 仍直接钉 12.1.4 会触发 `NU1605` 包降级错误，故统一到 12.1.5。
  - **PuraMarkdown 3 包布局**：Arc.Avalonia.Chat / Agent 改为经由 `PuraMarkdown.Agent` 渲染（Markdown、pura-agent 组件、工具调用、流式都上移到该包），Arc.Avalonia.Markdown 改用 `PuraMarkdown.Core` 解析。App 仍通过本机源码工程引用 `Arc.Avalonia.Meta`（推荐入口，会传递带入 Chat/Agent 及其 `PuraMarkdown.Agent` 依赖），NuGet.config 的本地 feed 注释更新为 `PuraMarkdown.Core/Agent/Extension`。
  - **受限沙箱下的可重复构建**：新增 `app/Directory.Build.targets` 清空 `UsedAvaloniaProducts`，避免 Avalonia 构建统计任务写 `%LOCALAPPDATA%\AvaloniaUI\BuildServices\buildtasks.log` 被文件沙箱拒绝（与 Arc 仓库同一处理）。
- **问题 1/2（重开已完成的会话：中间用户输入、中间回合的最终回答不见了）**：
  - **根因（分页裁剪 + 错误回退游标）**：`session/events` 按「消息」预算分页，而 App 首屏把一页裁剪到 60 条事件后，把**未裁剪的原始页起点**当成下一次「加载更早」的游标。被裁掉的那一段（往往正好含回合提问与回合最终回答，因为它们稀疏地夹在成百上千条工具事件之间）位于原始页起点之后、裁剪后起点之前，下一次请求从原始页起点再往前翻，于是**永久跳过**。复现：真实 1912 事件会话按旧算法只能落盘 660 条，两个用户提问（seq 8 / 1418）与第一回合最终回答（seq 1410）全部丢失 —— 与截图完全一致。
  - **修复**：所有向后游标一律改为「**实际渲染出来的最旧一行**」（裁剪后首行），被裁段由下一次「加载更早」原样取回，重复 seq 由既有归一化去重；首屏 90 条事件、每次加载更早 320 条事件（原 60），减少点击次数。回放对真实会话最终应用 1912/1912 条事件、无重复 seq。
  - **重开体验**：已完成会话首屏会把窗口回溯到**最近一个回合的 turn/start**（有界：最多 3 页 / 1200 事件），所以再次点进会话时最后回合的提问、插话和最终回答一定在；更早回合用「加载更早的历史」逐页补齐且不再丢内容。运行中会话仍按原逻辑向后补到本轮 `turn/start`。
- **问题 3（产出的文件没有展示 / 修改的文件需要展示）**：
  - 每回合折叠块下方新增只读「本轮文件」卡片：`present` / `deliverables/presented` 记为**产出**（含描述），`edit` / `write` / `apply_patch` 等写文件工具记为**修改**（按 `file_path` 汇总，附近似 `+新增/−删除` 行数）。同一文件在 `present` 相对路径与 durable 事件绝对路径下按路径后缀合并为一行；最多显示 12 个，多余折叠为「+N 个文件未显示」。
  - 仍不做远程打开文件（按需求本期不要求）；卡片只展示文件名、目录、产出/修改标记与行数变化。
  - 文件信息是对回合行的**纯投影**（历史回放、加载更早、实时流共用同一逻辑），因此重开历史与实时过程完全一致。
- **验证**：
  - **真实会话回放探针**（`session-92cc9a43` 原始 JSONL，1912 事件）：两个用户提问 seq 8 / 1418 均出现且顺序正确；两个回合最终回答 seq 1410 / 1908 均作为主区最终回答渲染；无重复 seq；回合 1 文件卡片 12 项（2 产出 + 10 修改）、回合 2 文件卡片 7 项（3 产出 + 4 修改，README 的 present 描述与 +18/−10 修改行数合并到同一行）。
  - **单测**：Core 115/115；App 17/17（新增 3 个：已完成多回合重开不丢提问/最终回答、文件卡片产出+修改解析、实时文件卡片增量出现）。
  - **构建**：Desktop Debug、Android Debug（arm64 产物 APK）均 0 error；Release 签名 APK 构建通过。
- 发布 APK：`dist/apk/dshremote-1.3.0-23-arm64-release.apk`（36,420,192 bytes = 34.73 MB，sha256 `e595150da021c75a468dd84bfec1a0b67ba003b21ec64db81b0bd12408b23f42`，apksigner verify 通过，证书 `b5613a45…493d6727` 同签名可覆盖安装；versionCode 23 / versionName 1.3.0）。已用 `scripts/sync-apk.ps1` 上传到 VPS `/opt/dsh-remote/apk/`，远端 sha256 与本地一致；gateway identity 报 `latest=dshremote-1.3.0-23`，下载直链 HTTP 200 / 36,420,192 bytes。

## 2026-11-11 v1.3.1：会话页「长图」（长截图）+ VPS 资源下载中心（网关 0.2.0）

### 问题 1：会话页截图不支持长截图 → 内置「长图」

- **现象**：在 App 的当前会话页用系统截屏时没有「长截图/滚动截屏」，长会话只能一屏屏截。
- **根因**：系统长截图依赖 App 暴露可滚动、可拼接的原生内容（Android 12+ 的
  `ScrollCaptureCallback` / 原生可滚动 View）。DSH Remote 是 Avalonia/Skia **单表面自绘**应用，
  聊天时间线是自绘的文本气泡/工具卡/文件卡（不是 `RecyclerView`/`ScrollView`），系统无法滚动
  它也无法按区域索取位图，因此系统长截图不可用。这不是 OS 设置能修好的，正确修法是 App 自己
  提供「整段会话 → 长图」。
- **修复**：会话页 header 新增 compact「长图」按钮（`ChatView.axaml`）。点击后：
  - 在 UI 线程用 `RenderTargetBitmap` 把 `MessagesScroll` 里的**整段已加载时间线**（用户/助手/
    工具卡/推理/图片/「本轮文件」卡片，按当前渲染）按设备 DPI 分段渲染成 PNG；渲染时临时隐藏
    滚动条与「加载更早」按钮、给内容面板填页面背景色，所以长图**不透明、无滚动条、无进度遮罩**；
  - **尺寸保护**（`LongShotPlanner`，纯逻辑可单测）：整次 ≤24M px、单图 ≤8M px 且单边 ≤12000 px、
    宽度不低于 640 px（保证可读）；超长会话先按比例降采样，仍放不下就自动分段为
    `…-p1.png / -p2.png …`（最多 24 张，绝不静默截断）。段间 `await Task.Yield()`，不阻塞 UI/不 ANR；
  - 结果保存到系统相册 `Pictures/DSHRemote/`（API 33+ 走 MediaStore `IS_PENDING`，无需存储权限；
    同时写一份副本到 App 外部目录便于 adb 取回），底部结果条显示「张数/像素/保存位置」并提供
    **查看**（复用现有全屏图片预览）/ **分享**（FileProvider + 系统 Sharesheet）/ 关闭；
  - 若 `HasOlder`（还有更早历史没加载），结果条明确提示「仅包含已加载内容」，可先点「加载更早的
    历史」再截；捕获中可取消；同一时刻只允许一次捕获。
  - 桌面壳同样可用（Windows 保存到 `图片\DSHRemote` 并打开 Explorer 分享）；语义集中在共享层，
    平台差异只在 `PlatformServices.ImageSaver/ImageSharer` 槽位 + Android `AndroidLongShotOutput`。
- **验证**：
  - 桌面自动化探针：`DshRemote.App.Desktop.exe --longshot-probe=<png>`（可加
    `--longshot-max-edge/--longshot-max-pixels/--longshot-min-width/--longshot-max-images` 压小预算），
    exit 0；默认输出 972×679 单图；小预算强制分 3 张（257+257+165），逐像素比对无缝/无重复、
    alpha 全 255、背景 `(20,20,20,255)`。证据：`docs/evidence/v131-longshot-desktop-probe.png`
    （+split-p1..p3、`.report.txt`）。
  - Android 模拟器（emulator-5556，Debug）：演示会话 → 点「长图」→ 进度 → 结果条「1006×1805 px ·
    4 条时间线 · Pictures/DSHRemote/…」；MediaStore 行 `relative_path=Pictures/DSHRemote/`、
    `width=1006 height=1805` 确认；`adb pull` PNG 内容完整（顶部「已到会话开头」到底部提示，无重复/
    无遮罩）；「查看」「分享」均正常，logcat 无 FATAL。证据：`docs/evidence/v131-longshot-android-*.png`。
  - 单测：Core 115/115、App 35/35（新增 18 条 `LongShotPlanner`/`LongShotNaming`：单张/上限降采样/
    分段不丢内容/段连续无重叠/非法参数/文件命名等全绿）；在真实仓库复核用等价反射 runner 执行同一
    测试程序集（本机 vstest testhost 受沙箱进程句柄限制无法启动，与代码无关）。
  - 完整记录：`docs/evidence/V131-LONGSHOT-VERIFY.md`。
  - 备注（既有基线，非本次引入）：emulator-5556 上 ArcMarkdownView 正文中文显示为方块（tofu），
    所以模拟器长图里 Markdown 正文也是方块；桌面探针/真机渲染正常。

### 问题 2：APK 下载扩展为通用资源中心（VPS 网关 0.2.0）

> 历史记录：网关 v0.4.0（2026-11-13）起资源改为**分类 ID**（`dsh-remote`/`smartbill`/`relay`/`docs`）
> 并新增 `keepVersions` 版本保留策略、下载页取消分类标签，详见文末「2026-11-13 网关 v0.4.0」小节；
> 本段保留 0.2.0 当时的 kind（apk/relay/docs/image/misc）描述作为演进记录。

- **背景**：原来 `/__gateway/download` 只列 `*.apk`，未来还要发布 relay 插件包、文档、图片等资源。
- **现状（已部署）**：`https://203.0.113.10:9443/__gateway/download` 现在是**资源下载中心**：
  - 5 个类别 `apk / relay / docs / image / misc`，各类别独立扩展名白名单；APK 类别仍映射原
    `/opt/dsh-remote/apk`，其余在 `/opt/dsh-remote/resources/<kind>/`；
  - 页面按类别分组展示**所有可下载资源**（文件名/大小/sha256/更新时间/直链；APK 保留「最新版本」
    主卡片 + 历史表；空类别有引导文案），无脚本、严格 CSP、页面/索引不含任何 token；
  - 机器接口：`GET /__gateway/resources[?kind=]`（JSON 索引，含 latest/直链）、
    `GET|HEAD /__gateway/resources/<kind>/<name>`（流式下载、正确 content-type、`x-content-sha256`、
    ETag/304、Range 206/416）；旧 `GET /__gateway/download/<file>.apk` 继续兼容；
  - 上传/删除：`PUT|POST|DELETE /__gateway/resources/<kind>/<name>`，仅 `Authorization: Bearer
    <UPLOAD_TOKEN>`（未配置令牌时端点 404 禁用；令牌由部署脚本首次生成并持久化在 VPS
    `/etc/dsh-remote/gateway.env`，600）；流式落盘 + 200 MiB 上限 + fsync + 原子 rename + 返回 sha256；
  - 脚本：新增 `scripts/upload-resource.ps1`（上传/列表/删除 + 远端/回读 sha256 校验，自动读取
    VPS 令牌）；`scripts/sync-apk.ps1`、`scripts/deploy-gateway.ps1`、identity/health、`/connect`
    与隧道/WS 路由均保持兼容。
- **验证**：`cd vps && npm run check && npm test` → 33 pass / 1 skip（Windows 无符号链接权限）/ 0 fail；
  VPS 侧真 `curl -k` 全量验证（页面/索引/APK 新旧直链 sha 一致/HEAD/Range/ETag/404/无令牌 403/
  上传→下载→删除 roundtrip/journal 零令牌泄露）；部署后 `tunnels=1`，手机 `/api/session/*` 流量正常。
  详见 `服务器文档/部署与验证记录.md`。
- **文档**：新增工作区根目录 [`服务器文档/`](服务器文档/README.md)：`总体设计.md`、`HTTP接口规范.md`、
  `上传下载运维.md`、`部署与验证记录.md`（含资源模型/接口/鉴权/安全边界/迁移与运维流程），并同步
  `vps/README.zh.md` 与 `docs/vps-gateway.md`。
- **新 APK**：`dist/apk/dshremote-1.3.1-24-arm64-release.apk`（36,444,768 bytes = 34.76 MB，
  sha256 `910ef73bcf2124caf7912eceafed296e5aeeeb62c7e91a9f2bc26f17cb930687`；versionName 1.3.1 /
  versionCode 24 / arm64-v8a；apksigner verify 通过，证书 `b5613a45…493d6727` 同签名可覆盖安装）。
  已上传 VPS `/opt/dsh-remote/apk/` 并复验：远端 sha256 一致；gateway identity
  `download.latest=dshremote-1.3.1-24-arm64-release.apk`（36,444,768 B / versionCode 24）、
  `resources.total=13`（apk 11 / relay 1 / docs 1）；下载页最新卡片与直链均指向 1.3.1。

## 2026-11-11 relay 0.5.0：Windows 工作区权限守卫（新会话准入自愈）

- **背景**：会话审计发现工作区 ACL 漂移（根上只有**不可继承**的用户 ACE → 新建目录缺 `WRITE_OWNER`）会让 DSH 自己授权失败（`SetNamedSecurityInfoW failed (Win32 5)`），agent 只能反复申请完全权限跑 `icacls`（实测 4 次修复审批 / 145 分钟等待）。成因与方案见 [docs/acl-experience.md](docs/acl-experience.md)。
- **方案**：relay 新增 `relay/src/acl-guard.mjs`，在宿主进程内（用户令牌、**不触发审批**）注册 `session/created`（预热）+ `tools/pre-execute`（首次工具执行前等待就绪）+ `tools/post-execute`（ACL 失败指纹复查）；只给**被新会话用到的工作区根**补一条可继承的用户完全控制 ACE，capability ACE 与 Low 标签仍由 DSH 自己写；Windows 的继承传播顺带补齐该工作区存量（模式 A，已确认）；每个工作区只做一次，缓存于 `~/.dsh/remote/acl-admission.json`，审计写入 `acl-audit.jsonl`。
- **配置**：`config.aclGuard`（`enabled` / `waitMs` / `probeTimeoutMs` / `repairTimeoutMs` / `cacheTtlHours` / `propagate` / `excludedPrefixes`），说明见 [relay/README.zh.md](relay/README.zh.md)。
- **验证**：relay 单测 **42/42**（含 23 个 acl-guard 用例）；`npm run smoke:acl` 端到端 **PASS**（复现缺陷→修复→子对象继承→缓存/审计）；装入桌面 DSH 后实测修复 `D:\Code\VibeCoding\Arc.Avalonia`（4.8 s）与 `D:\Code\VibeCoding\DSHRemote`（7.1 s），根新增 `(OI)(CI)(F)`、新建目录继承 `(I)(OI)(CI)(F)`，`pairing.relayVersion=0.5.0`、self-check OK、gateway online。
- **下一轮**：手机端状态卡片/一键体检（消费 `guard.status()`）、模式 B（只补根不传播）、失败路径的精确祖先链定位。

## 2026-11-11 relay 0.5.1 + App 1.4.0：手机端「Windows 权限」卡片与一键体检

- **relay 0.5.1**：新增手机可达的 ACL API（令牌鉴权，网关与 LAN 都可用）：
  - `GET /__relay/acl/status` → guard 状态 + 已知工作区（不探测，O(1)）；
  - `POST /__relay/acl/check` → body `{ repair?: bool, root?: string }`，对已知工作区逐个体检，默认修复缺失项；
  - 鉴权：`Authorization: Bearer <配对令牌>`（同时接受网关 remote token 与 LAN launch token，`timingSafeEqual` 比较）；本机 loopback 管理页免令牌；**网关内部 loopback 转发不继承本机信任**；
  - 只接受 relay 已知工作区（guard 见过的 + 会话 cwd），任意路径返回 `unknown-root`。
- **Core**：`IAclGuardClient` + 记录类型（`DshAclGuardStatus`/`DshAclRootStatus`/`DshAclCheckResult`/`DshAclRootCheck`）；`DshClient.Acl.cs` 复用固定 TLS 的 HttpClient，Bearer 鉴权，404 → "relay 版本过低（需要 0.5.0+）"，401/403 → 配对失效/被拒。
- **App 1.4.0**：设置页新增「Windows 权限」卡片：状态摘要（工作区数/正常/待体检/异常）、前 3 个工作区、上次体检时间、`刷新状态` 与 `一键体检并修复` 按钮、结果摘要；连接成功自动加载，断开自动复位。
- **验证**：relay `npm test` **53/53**（新增 `acl-api.test.mjs` 6 个：令牌鉴权、网关 loopback 不继承信任、check 透传、方法与前缀校验、旧版本回退）；loopback 实测 status/check 全 PASS（3 个已知工作区全部 ready、未知路径被拒）；LAN 实测无令牌/错误令牌 401、正确配对令牌 200；Core 新增 `AclClientTests` 5/5（本机 VSTest testhost 受"打开父进程句柄被拒"限制，用 `_scratch/acl-test-runner` 直接调用验证；xunit 用例保留给正常开发机/CI）；Core/App/Desktop 构建 0 warning 0 error。
- **APK**：`dist/apk/dshremote-1.4.1-26-arm64-release.apk`（36,457,056 bytes = 34.77 MB，sha256 `d8f28fb9db75dee826e9e8479b997d40993e955e1b50f0ee2f9a834178e5e602`，versionName 1.4.1 / versionCode 26，同签名 `b5613a45…493d6727`）；已同步 VPS 并校验 sha256：<https://203.0.113.10:9443/__gateway/download/dshremote-1.4.1-26-arm64-release.apk>。
- **修复（1.4.1）**：网关模式下「Windows 权限」卡片报“读取失败: 桌面 relay 版本过低”。根因不是 relay 版本，而是**公网网关按 `dsh-auth` cookie 把请求路由到某台桌面**：ACL 请求只带了 Bearer、没带 cookie，网关直接回 404 `unknown-session`，App 把 404 一律当成“relay 版本过低”。修复：`DshClient.Acl.cs` 同时携带 owner cookie（网关路由）与 Bearer（relay 鉴权），并把 404 文案改为区分“没路由到桌面 / relay 版本过低”。网关链路实测：无 cookie → 404 `unknown-session`；cookie + Bearer → 200（3 个已知工作区全部 ready）。


## 2026-11-11 v1.4.2：重新加载 Arc 链接能力（Markdown 链接可点：复制链接 / 在浏览器中打开）

- **背景**：Arc.Avalonia 本轮修好了 Markdown 内联链接——点击链接弹出上下文菜单（复制链接 / 在浏览器中打开），Chat 内链接的按钮 chrome / 基线与正文对齐。DSH Remote 仍保留着 2026-11-10 的临时 workaround（`MarkdownBodyView.Preprocess` 把 `[label](url)` 压成 `label（url）` 纯文本 + 裸 URL 插零宽空格），会让 Arc 的链接能力在 App 中完全失效，本次移除。
- **重新加载 Arc + 恢复链接**（`app/src/DshRemote.App/Controls/MarkdownBodyView.cs`）：
  - App 本来就通过 `ProjectReference` 引用本机 `Arc.Avalonia` 源码（`Arc.Avalonia.Meta`），重建即加载新 Arc；删除链接压平逻辑后，最终消息里的 Markdown 链接恢复为 Arc 原生 `HyperlinkButton`：accent 下划线、与正文同基线；点击弹 Arc 菜单，**复制链接**（写剪贴板 + 「链接已复制」反馈）与**在浏览器中打开**（`ArcMarkdownPlatform.OpenInBrowserAsync` → `TopLevel.Launcher`，Android 即系统浏览器）都可用。
  - 裸 URL 去掉零宽空格注入（复制/选择不再带 `U+200B`）。Avalonia 12.1.5 对超长无空格文本按字符换行，长 URL 在窄气泡里仍完整显示。
  - 仅 `<https://…>` 尖括号 autolink 仍压平为纯 URL：Arc 内联链接内容用 `TextWrapping.NoWrap`，长 URL 作为可见标签会被裁切（证据 `docs/evidence/v142-link/after-long-label-clip-w320.png`），压平后才能随正文换行、完整阅读。
  - 代码围栏内不改写；`SplitTopLevelBlocks`（表格 + 代码块叠层 workaround）与 `PART_Host` StackPanel 模板不变。演示会话末条消息补了一条裸 URL + 一条 Markdown 链接，供桌面/模拟器截图回归。
- **验证**：
  - 桌面渲染探针 `build/link-probe`（320 / 360 / 720 px 宽）：修复前 0 个 `HyperlinkButton`（链接全被压平），修复后链接正常渲染、无越界，点击 `OpenedPopups=1` 且菜单行 = `复制链接` / `在浏览器中打开`。
  - 桌面 demo 时间线截图（`--longshot-probe`，生产同款渲染路径）：裸 URL 完整换行 + `install.md` 下划线链接。
  - Android 模拟器（emulator-5556，Debug APK）实测：演示会话中链接可点 → 弹出 Arc 菜单 → 「复制链接」显示「链接已复制」→「在浏览器中打开」由 Chrome 打开目标 URL（`mCurrentFocus=com.android.chrome/ChromeTabbedActivity`）。
  - 单测 **163/163**（Core + App，含新增 7 条 `MarkdownPreprocessTests`；本机 vstest testhost 仍受“打开父进程句柄被拒”限制，按既有约定用反射 runner `build/test-runner`，`TEMP` 指向工作区）；Desktop / Android 构建 0 warning 0 error。
  - **Release 配置冒烟**：发布的 arm64 APK 在 x86_64 模拟器上受 ARM 翻译拖慢会在冷启动时 ANR（模拟器 ABI 限制，不是应用缺陷；arm64 包内 libxamarin-app.so = 1,382,904 bytes，与已在真机验证过的 1.3.0 一致、pksigner verify 通过）。为验证 Release + SdkOnly 裁剪后的代码路径，另用同源码构建 RuntimeIdentifier=android-x64 Release APK 装入同一模拟器：冷启动正常、演示会话中链接与菜单正常。
  - 证据与复跑命令：[docs/evidence/V142-LINK-VERIFY.md](docs/evidence/V142-LINK-VERIFY.md)。
- **已知限制**：链接标签本身是超长 URL（`[https://…](…)`）时仍会被裁切；裸 URL 不可点击（Arc 默认 pipeline 不做 autolink），如需可点需上游提供可换行的链接渲染或宿主侧短标签方案；文件路径链接的「在浏览器中打开」按 Arc 语义置灰，只能复制。
- 发布 APK：`dist/apk/dshremote-1.4.2-27-arm64-release.apk`（36,457,056 bytes = 34.77 MB，sha256 `9f2e33e0f5651850b9ababe5d0edbb29676c79520792f2e7e278fb68ae2d4a46`，versionName 1.4.2 / versionCode 27 / arm64-v8a；`apksigner verify` 通过，证书 `b5613a45…493d6727` 同签名可覆盖安装）。已用 `scripts/sync-apk.ps1 -File dist/apk/dshremote-1.4.2-27-arm64-release.apk` 上传到 VPS `/opt/dsh-remote/apk/` 并复验：远端 sha256 一致；gateway identity `download.latest=dshremote-1.4.2-27-arm64-release.apk`（36,457,056 B / version 1.4.2 / versionCode 27）、`resources.total=18`（apk 16）；下载页最新卡片与直链（`/__gateway/download/…` 与 `/__gateway/resources/apk/…`）均 HTTP 200、`content-length` 一致、`x-content-sha256` 与本地相同：<https://203.0.113.10:9443/__gateway/download/dshremote-1.4.2-27-arm64-release.apk>。

## 2026-11-12 v1.4.3：修复「网站链接不渲染」（Arc 链接换行 + 裸 URL autolink 归一化）

- **现象**（真机截图 7482.jpg）：同一条助手消息里 `[label](…)` 链接（文件路径、验收记录）可点，但网站链接——`下载页：<https://…>`、`直链：https://…`——显示为纯文本、点不了。
- **根因（三层，逐层实测）**：
  1. **直接原因**：v1.4.2 的 `MarkdownBodyView.Preprocess` 主动把尖括号 autolink `<https://…>` 压平成纯文本（当时为绕开下面第 3 条的裁切）；截图里的网站链接源码正是 `<https://…>`。
  2. **裸 URL 也不会变链接**：Markdig GFM autolink 只在空白/行首触发，`下载页：https://…`（全角冒号后）不会被识别（探针：空格后的裸 URL 会 autolink，`：` 后不会）。
  3. **深层约束**：Arc 行内链接 = `InlineUIContainer(HyperlinkButton(TextBlock NoWrap))`，长 URL 作为可见标签时不换行、被裁切——这才是 v1.4.2 压平的动机。
- **修复**：
  - **Arc（上游修根因，工作区未提交）**：`ArcMarkdownDefaultInlineRenderer.AddHyperlink` 的链接内容 `TextWrapping.NoWrap → Wrap`；`InlineUIContainer` 把可用行宽传给子控件后，URL 在气泡内换行、完整可见（320 px 实测 301×42 两行、无越界；短标签仍单行）。Arc 回归 `VERIFY: checks=188 failures=0`。
  - **App 侧**（`app/src/DshRemote.App/Controls/MarkdownBodyView.cs`）：删除 `<…>` 压平；新增裸 `http(s)` URL → `<url>` autolink 归一化（跳过围栏/行内代码、已有 autolink、Markdown 链接/图片 destination、HTML 属性；中文/英文尾部标点留在链接外），因此 `下载页：https://…`、括号里的 URL 也都可点。
  - **单测**：`MarkdownPreprocessTests` 更新/新增到 14 条（中文冒号后裸 URL、行首裸 URL、尾部中文标点、destination 不二次包裹、括号内 URL、图片 destination、行内/围栏代码、尖括号 autolink 保持等）。
- **验证**：
  - 桌面渲染探针（320/360/720 px）：`autolinks` 场景 2 个链接全部渲染（320 px：295×24 与 298×42 两行，`overflow=0`）；`paragraphs` 4 个链接全部渲染；长 URL 链接标签由「裁切」变为「换行」；点击链接菜单 `OpenedPopups=1` 且行 = 复制链接 / 在浏览器中打开。
  - 桌面 demo 时间线截图：`下载页：<url>`、`最新 APK：url`、`install.md` 均为 accent 下划线链接，长 URL 自动换行（`--longshot-probe`，生产同款渲染）。
  - Android 模拟器（Debug + Release x64 冒烟）：3 个链接均为 `HyperlinkButton`；点方括号 autolink → 菜单；点「在浏览器中打开」→ 系统 Chrome 收到 `act=android.intent.action.VIEW dat=https://203.0.113.10:9443/...`。
  - 单测 **169/169**（App + Core）；Arc `VERIFY 188/188`；Desktop / Android Debug / Android Release 构建 0 warning 0 error。
  - 证据与复跑命令：[docs/evidence/V143-LINK-AUTOLINK-VERIFY.md](docs/evidence/V143-LINK-AUTOLINK-VERIFY.md)。
- **说明**：v1.4.2 记录的「裸 URL 不可点 / 长 URL 标签会被裁切」两条限制由本版解决；Arc 仓库的 `Wrap` 改动目前只在工作区（DSHRemote 经 `ProjectReference` 直接使用），需要时可单独在 Arc 仓库提交。
- 发布 APK：`dist/apk/dshremote-1.4.3-28-arm64-release.apk`（36,457,056 bytes = 34.77 MB，sha256 `8585b557dff73bb3d6d42f41fe38d35205404f27ef636f3d03d9a3af114ff275`，versionName 1.4.3 / versionCode 28，同签名 `b5613a45…493d6727` 可覆盖安装）；已上传 VPS 并复验（远端 sha256 一致、identity `latest=1.4.3/28`、`resources.total=19`、legacy + resource 直链均 HTTP 200 / `x-content-sha256` 一致）：<https://203.0.113.10:9443/__gateway/download/dshremote-1.4.3-28-arm64-release.apk>。

## 2026-11-12 v1.5.2：六项降流量改造（本地缓存 / 列表瘦身 / gzip / 图片缓存 / 懒加载）

手机经公网网关使用时流量过大。实测（本机 311 会话的真实 DSH）：`session/list` **1.06 MB**（92.1% 是 App 不用的
`projections`，单会话 `agentTeam` 一项 80 KB）；打开一个会话约 **2.8 MB**（`session/events` 首屏 1.19 MB + turnaround
读前 1–3 页 + `session/follow` 快照 22 KB + **follow pump 的快照(80) 1.44 MB，与已加载历史完全重复**）；网关 HTTP/WS
全量 base64 且不压缩。

六项改造：

1. **App 本地缓存**：新增 `SessionCacheStore`（list / workspace / 会话窗口 / 图片；gzip + 原子写 + 容量上限 + LRU 淘汰）。
   冷启动先渲染缓存再增量刷新；打开会话时用缓存 `lastSeq/cursor` 种 follow baseline，桌面只送增量。缓存损坏/回退日志时
   自动失效，绝不阻塞 UI。
2. **follow 快照去重**：`DshClientOptions.FollowMaxMessages` 80 → 1；缺口交给既有 `session/events` 精确回补。
3. **列表瘦身 + 按需拉取**：relay **0.6.0** 新增 `GET /__relay/sessions/summary`（Bearer + cookie 反代 `session/list`，
   裁剪 11 字段、默认隐藏 subagent、旧 relay 自动回退）；`?sessionId=` 详情保留为“以后需要更多列表字段时”的按需扩展点；
   响应带 `schema/fields/counts`，客户端忽略未知字段。
4. **网关 gzip**：gateway **0.3.0** 对手机侧 JSON/text HTTP 响应 gzip（实测 3.6–5.1×），Core 打开 `AutomaticDecompression`；
   旧 App 不发送 `Accept-Encoding` 时行为不变。
5. **图片磁盘缓存**：`sha256(attachmentId)` 内容寻址落盘（64 MB 上限），跨会话/重启复用，不再每次打开重拉。
6. **首屏 read-ahead 懒加载**：打开只拉 1 页；首次「加载更早」才补 turn 边界（保留原“提示词/插话丢失”修复），
   completed/running 的 3/8 页预读不再发生在打开时。

实测收益（真实 DSH，经公网网关）：

| 场景 | 改造前 | 改造后 |
|---|---|---|
| 连接后会话列表 | 1.06 MB JSON（公网 ≈1.4 MB） | **35.6 KB JSON → gzip 7.1 KB**（0.67%） |
| 打开已缓存会话 | 1.19 MB history + 1.44 MB follow 快照 + 读前页 | 磁盘缓存即时渲染 + 增量（无变化时不传快照） |
| 聊天中断线重连 | 每次重传 snapshot(80) ≈1.44 MB | 只补 seq 缺口 |
| 重启后打开会话 | 1–3 MB 重拉 | 先渲染缓存，再拉增量 |

验证：C# 198/198（含缓存契约、follow 去重、紧凑列表回退）、relay `npm test` 66/66、vps `npm test` 45 passed/1 skipped；
公网链路实测 `/__relay/sessions/summary` 未压缩 35,570 B / gzip 7,110 B，`counts={total:313,hiddenSubagents:199,returned:114}`；
VPS identity `version=0.3.0`、`tunnels=1`；本机 profile relay `relayVersion=0.6.0`、self-check OK。
设计与扩展位：[docs/traffic.md](docs/traffic.md)。

发布 APK：`dist/apk/dshremote-1.5.2-31-arm64-release.apk`（36,485,728 bytes = 34.8 MB，versionName 1.5.2 / versionCode 31，
sha256 `424a75d5468d3b630d5bfbb84497d6269cd0dafe1ed58f8b3e8aeb8ba08ac6b4`，apksigner 通过，签名证书 `b5613a45…493d6727`）；
已上传 VPS 并复验（远端 sha256 一致、identity `latest=1.5.2/31`、`tunnels=1`）：
<https://203.0.113.10:9443/__gateway/download/dshremote-1.5.2-31-arm64-release.apk>

## 2026-11-13 v1.6.0：会话模型 + 思考强度选择（手机端可切换）与聊天页整理

- **背景**：手机端此前只能发消息，模型/推理等级只能用桌面默认值；聊天顶栏还占着「长图」大按钮、运行状态徽标和工作区目录行，composer 的占位文字会把输入框撑高、与按钮不等高；Android 软键盘换行会被当成发送并收起键盘。
- **协议（官方 Remote，无需改 relay/网关）**：新增 `session/modelCatalog`（无参，返回 provider 分组/模型/每个模型的 `reasoning.efforts`/defaultEffort/badge/failures）与 `session/selectModel`（`{sessionId,provider,model,reasoningEffort?}`，下一步生效；返回归一化 `selected`，省略 effort 时服务端会物化该模型的 `defaultEffort`）。选择成功会向会话日志写 durable 事件 `model/selection`，并后台保存桌面全局默认模型（与桌面 GUI 行为一致）。
- **Core**：新增可选接口 `IModelClient`（`DshClient.Model.cs`）；catalog 按连接代际缓存（失败也缓存，支持 `forceRefresh`）；新增 `gateway/endpoint-unavailable` 错误码（HTTP 404 不再误报成「无法连接中继」），旧版 DSH 缺端点时结果标记为 `Unsupported`；`SessionSummary`/`SessionSnapshot` 带上 `modelSelection`（next ?? lastUsed），follow 快照用于打开会话时校准。
- **手机端**：
  - 聊天顶栏重做：删除「长图」按钮、`运行中/空闲` 徽标、工作区目录行；长图移入**会话名称下拉菜单**（点标题 → 截取长图）；工作区名称点击仍打开全部会话抽屉。
  - 右上角新增「模型 · 推理等级」chip（两行紧凑显示），点击展开**级联菜单**：全部 Provider → 模型 → 该模型的推理等级（不铺平），选中立即作用于当前会话并刷新 chip；运行中入口直接禁用；旧版 DSH 自动柔和禁用（不弹错）。
  - 状态行不再显示「空闲/运行中」文案（对话本身即可看出运行状态），只保留队列/首屏耗时等瞬时信息。
- **Composer / IME**：删除输入框占位文字，输入框与「附图/发送/停止」按钮等高（36dp，多行时输入框单独增高）；Android 软键盘的换行键改为插入换行并保持键盘（不再被 Enter 发送拦截；桌面仍保持 Enter 发送 / Shift+Enter 换行），全屏输入框同样支持换行。
- **验证**：Core 单测 **173/173**、App 单测 **76/76**、Solution 构建 0 warning 0 error；模拟器（emulator，Debug）实测：新顶栏布局、标题菜单长图（生成 1028×1831 PNG 并保存相册）、Provider→模型→推理等级三级级联并生效（chip 由 `V4.1 Flash·高` 切到 `V4.1 Flash·中`）、IME 换行后 `mInputShown=true` 且输入框出现第二行、composer 等高。证据：`docs/evidence/v160-*.png`。
- **发布 APK**：`dist/apk/dshremote-1.6.0-32-arm64-release.apk`（36,506,208 bytes = 34.82 MB，versionName 1.6.0 / versionCode 32，sha256 `6c05b10dd04651b54bf318a7806cf93f04a935fab3d7b401094cb4d19d2bb6be`，`apksigner` 通过，证书 `b5613a45…493d6727` 同签名可覆盖安装）；已上传 VPS 并复验（远端 sha256 一致、gateway `tunnels=1`）：<https://203.0.113.10:9443/__gateway/download/dshremote-1.6.0-32-arm64-release.apk>

## 2026-11-13 网关 v0.4.0：资源分类 ID + 版本保留策略（keepVersions）

- **背景**：v0.3.0 的资源中心按**文件类型 kind**（apk/relay/docs/image/misc）分类，DSH Remote
  与 SmartBill 两个 App 的 APK 都落在 `apk` 类、下载页混在同一个列表。v0.4.0 改为按
  **资源分类 ID（应用/发布线）**组织，并把“保留几个版本”变成上传参数。
- **分类 ID 注册表**（`vps/resources.mjs` 的 `RESOURCE_CATEGORIES`，目录
  `/opt/dsh-remote/resources/<id>/`）：

| id | title | flavor | 说明 |
|---|---|---|---|
| `dsh-remote` | DSH Remote 客户端 | apk | Release 签名 APK（arm64-v8a） |
| `smartbill` | SmartBill（我的账单）客户端 | apk | universal，debugsign 测试签名 |
| `relay` | 桌面 Relay 插件包 | file | zip / tgz / tar |
| `docs` | 文档资料 | file | pdf / md / txt / zip |

- **新增 App/发布线** = 在 `RESOURCE_CATEGORIES` 加一项（`id`/`title`/`flavor`/`description`）
  并重新部署（与旧版加 kind 的方式一致）；文件名白名单全局统一（原 5 类并集），
  文件名规则不变（`^[A-Za-z0-9][A-Za-z0-9._-]*$`、1–160 字符、不含 `..`/路径分隔符）。
- **上传**：`PUT|POST /__gateway/resources/<id>/<name>?keepVersions=0|1`（Bearer 鉴权不变）：
  - `keepVersions=1`（保留旧版本）：上传后该 ID 目录只保留**最近 3 个版本**
    （按 mtime 倒序，`MAX_RETAINED_VERSIONS=3`）；
  - `keepVersions=0` 或**缺省**（不保留）：只保留刚上传的 1 个版本，其余旧版本删除；
  - 其它值 → 400 `invalid-keep-versions`；响应含 `keepVersions`/`replaced`/`retained`/`pruned`。
- **页面**：`/__gateway/download` **取消顶部分类标签（chips）**，改为按分类 ID 分组卡片
  （eyebrow `title · N 个文件`、最新文件主按钮、版本表；空分类不显示卡片）；无脚本、严格 CSP 不变。
- **JSON 索引**：`GET /__gateway/resources` 改为 `categories[].id` + `counts.byId`，
  支持 `?id=<id>` 过滤（未知 id → 400 `unknown-id` + `allowedIds`）；不再支持 `?kind=`。
- **identity / health**：`identity.resources.categories[]`（id/title/count/latest）、
  `health.resourceCategories[]`；旧字段 `resourceKinds` 保留为同内容别名。
- **脚本**：`upload-resource.ps1` 的 `-Kind` 改为 `-Id`（新增 `-KeepVersions` 开关）；
  `sync-apk.ps1` 改为**走上传 API**（不再 scp）：新增 `-Id`（默认 `dsh-remote`）、
  `-KeepVersions`（默认开启，`-NoKeepVersions` 关闭），`-All` 按 versionCode 升序上传，
  移除 `-RemoteDir`；SmartBill 发布脚本改为 `?id=smartbill` + `?keepVersions=1`。
- **兼容**：旧下载直链 `/__gateway/download/<name>.apk`、旧别名
  `/__gateway/resources/apk|image|misc/<name>` 保持可用；**旧 kind 上传路径不再接受**
  （400 `id-required`）；旧 `APK_DIR` 保留仅作兼容下载回退/回滚兜底。
- **迁移**（Lead 部署时执行）：旧 `dshremote-*.apk` → `resources/dsh-remote/`（按“保留最近
  3 个”归档其余）；SmartBill APK → `resources/smartbill/`；`relay`/`docs` 目录原地成为分类；
  `/opt/dsh-remote/apk` 清空保留。
- **版本**：`vps/package.json` → `0.4.0`（identity.version）；`npm run check`/`npm test` 全绿。
- **部署与验证（2026-11-13 已完成）**：17:45:14 UTC 迁移（分类计数 dsh-remote=3 / smartbill=3 /
  relay=1 / docs=1，16 个旧 APK 归档于 `resources/_archive/dsh-remote/`），17:45:59 UTC 重启
  v0.4.0；identity.version=0.4.0、tunnels=1、total=8、apk=6。线上实测：页面无 chips、
  索引新结构、`?id=` 过滤、旧/新直链 200、旧 kind 上传 400、`keepVersions=1` 保留 3 个（并发
  `1.6.1-33` 上传真实剪枝 1.5.1-30）、`=0` 只留 1 个（本地临时网关实测）、无令牌泄露、
  tunnels 恢复 1；verifier 线上只读 63 PASS / 0 FAIL。详细记录见
  [`服务器文档/部署与验证记录.md`](服务器文档/部署与验证记录.md) §10。
- **命令速查**：

```powershell
pwsh -File scripts/sync-apk.ps1                       # 最新 APK → resources/dsh-remote/，保留最近 3 个版本
pwsh -File scripts/sync-apk.ps1 -NoKeepVersions       # 只保留 1 个版本
pwsh -File scripts/sync-apk.ps1 -Id smartbill -File dist\apk\smartbill.apk
pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-0.4.0.tgz -KeepVersions
pwsh -File scripts/upload-resource.ps1 -List
```

- **下载**：页面 <https://203.0.113.10:9443/__gateway/download>；
  规范直链 `https://203.0.113.10:9443/__gateway/resources/<id>/<name>`（旧
  `https://203.0.113.10:9443/__gateway/download/<name>.apk` 仍可用）。

## 2026-11-13 v1.6.2：运行中卡死诊断 + 插话条固定高度加固

- **背景**：1.6.1 真机反馈「进入会话后插话模式选项未出现时流畅；该选项出现（开始运行）后 APP 卡死并自动关闭」。AOSP 模拟器（Debug/411dp）复现 demo 发送 → 插话行出现 → 流式输出，整条链路正常，判断仍与 OEM 窗口/输入法实现相关，需要设备侧证据。
- **诊断能力（本版新增，后续崩溃可定位）**：
  - `DiagnosticsLog`：滚动日志 + 最近 240 条 breadcrumb（连接、打开会话、加载、`running=true/false`、发送、模型选择器开/关、IME 显示/隐藏、页面 attach/detach）；
  - 捕获 UI 线程 / AppDomain / 未观察 Task 异常，以及 Android `UnhandledExceptionRaiser`；
  - **UI 心跳 watchdog**：UI 线程 1s 心跳，>12s 未跳动且前台 → 写 `UI-HANG` + breadcrumb（区分卡死与崩溃）；
  - 日志位于应用专属外部目录，可免 root 取回：`/sdcard/Android/data/dev.dshremote.app/files/dshremote-diagnostics.log`；设置页新增「诊断」卡片（显示路径 / 复制 / 清空）。
- **加固**：插话条由「整行按 IsRunning 显示/隐藏」改为**固定 30dp 常驻容器 + 内容切换**（空闲显示灰色提示，运行显示原复选框）。running 切换时 composer 不再重排，`CheckBox` 也不再在切换瞬间首次应用模板，降低"运行开始即卡死"的一类风险面。
- **验证**：Solution 构建 0 warning 0 error；Core 173/173、App 76/76；模拟器 Debug 验证诊断日志落盘（`install path=…`、`app: framework initialization completed`）与设置页诊断卡片。
- **取证**：若真机仍卡死，请按 [docs/issue-20261113-model-picker-freeze.md](docs/issue-20261113-model-picker-freeze.md) §7.3 取 `dshremote-diagnostics.log` 反馈（含 `UI-HANG`/`UI-THREAD-UNHANDLED` 与 breadcrumb），可据此做 1.6.3 定向修复。
- **发布 APK**：`dist/apk/dshremote-1.6.2-34-arm64-release.apk`（36,510,304 bytes = 34.82 MB，versionName 1.6.2 / versionCode 34，sha256 `bea4bf20c565cfe74a36de049b95c76f9f2f9a73d476e03a5007ba6a06395a50`，apksigner 通过、同签名可覆盖安装）：<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.2-34-arm64-release.apk>

## 2026-11-13 v1.6.3：真机定位「运行中打开长会话卡死/ANR」并修复

- **真机定位**（无线调试，TestDevice / Android 16）：1.6.2 诊断构建抓到打开运行中长会话时 **UI-HANG ≈13s** 与系统 ANR（`Input dispatching timed out ... Waited 5000ms`）；加计时后确认：`load done` 仅 0.36s，但随后 Messages 从 117 被短时灌到 800+，每个事件都触发一次全量时间线重建（`perf: rebuild timeline ... ms=409`），叠加长 Markdown 首次布局 → UI 线程被占满 → 冻结 → ANR。"插话模式出现"只是 `running=true`/流式开始的表象。
- **修复**：
  1. live 事件合并批处理（120ms 定时器、每批≤128 条，批次内挂起时间线重建，只重建/通知一次）；
  2. `ApplyResyncSnapshot` 补洞记录一次性批量应用；
  3. `ChatTurnViewModel.Update` 对 Process/Final 改为前缀 diff 增量同步，不再每事件清空重建几百张工具卡；
  4. 新增 `MarkdownRenderQueue`：先纯文本、每 40ms 只把一个视图升级为 Arc Markdown，避免打开长会话时一次性构建几十个长 Markdown；
  5. 保留 1.6.2 的插话条固定高度、IME 事件化合并与全部诊断（新增 `perf:` 计时行便于后续定位）。
- **真机验证**：冷安装后打开运行中会话：`load done ms=1248`、无 UI-HANG、无新增 ANR（`lastanr` 仍为修复前时间）、插话模式行正常、长回合自动折叠为「正在执行中」且可正常滚动/返回；事件洪峰被摊到 120ms 批次，单批降至几十毫秒级。
- **发布 APK**：`dist/apk/dshremote-1.6.3-35-arm64-release.apk`（36,514,400 bytes = 34.82 MB，versionName 1.6.3 / versionCode 35，sha256 `97f9c79947e58e55dadbd3101429a3b1f1d864718cfdda46cadcc7c46f42a106`，同签名可覆盖安装）：<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.3-35-arm64-release.apk>
- **详细记录**：[docs/issue-20261113-model-picker-freeze.md](docs/issue-20261113-model-picker-freeze.md) §8。

## 2026-11-13 v1.6.4：去掉底部「运行中可发送插话（steer，不排队）」空闲提示

- 空闲会话的 composer 上方不再显示该提示行（1.6.2 为规避布局抖动加的固定高度提示条已移除）；只在**运行中**显示原来的「插话模式（发送 steer，而非排队）」复选框行。1.6.3 的流式/长会话性能修复不受影响。
- 验证：Solution 构建 0 warning/0 error；Core 173/173、App 76/76；真机（TestDevice / Android 16）空闲会话实测 composer 仅「附图 / 输入框 / 发送 / 停止」，无提示行（`docs/evidence/v164-composer-no-idle-hint.png`）。
- 发布 APK：`dist/apk/dshremote-1.6.4-36-arm64-release.apk`（36,514,400 bytes = 34.82 MB，versionName 1.6.4 / versionCode 36，sha256 `f79df96b060e1a3a968dfb4bf388e3a02434c69d0583c9708a5416721b246b3c`，同签名可覆盖安装）：<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.4-36-arm64-release.apk>


## 2026-11-13 v1.6.5：工作区全量显示 / 新会话草稿保留 / IME 换行不收起键盘

- **背景**（手机端三项体验问题）：
  1. 工作区里所有会话都被归档后，该工作区会从工作区选择器里整组消失，导致**无法再选中它新建会话**；
  2. 「新建会话」里临时输入、尚未发送的内容在切换会话后丢失，且反复点「＋ 新建会话」会堆积多个空白会话；
  3. 手机输入法按回车虽然会换行，但**输入法同时被收起**（桌面 Enter 发送行为不变）。
- **工作区全量显示**：`ApplySessionData` 不再按「有可见会话」过滤工作区，Host 上报的每个工作区都进入**工作区选择器**（计数 0 也保留），空工作区显示「这个工作区还没有会话」空状态与「＋ 新建会话」入口；聊天页会话抽屉只列**有会话**的工作区（避免空工作区留下没有行的孤立分组标题，选择器里仍可选中它新建会话）。
- **新会话输入保留**：
  - 新增 `SessionDraftStore`（内存 + 经 `SessionCacheStore` 落盘 `cache/drafts.<桌面 key>.json.gz`，按桌面命名空间隔离、条数/长度上限、原子写），`SessionViewModel` 的 `Draft` 每次编辑即写入，打开会话时回填；发送后清除；切会话/返回列表/退出时 flush，冷启动也能恢复。
  - 「＋ 新建会话」改为**复用该工作区最新的空白会话**（`blank=true` 且未发送过任何消息）：再次点击回到同一个新会话并带回草稿，不再堆积「(空白会话)」；该会话发出第一条消息后立即被标记为已使用（不等列表刷新），下次点击才创建真正的新会话。
- **IME 换行不收起键盘（根因修复）**：Avalonia 的 Android 输入法按 `TextInputOptions` 生成 `EditorInfo`；此前 `Multiline=false`，Android 侧发布的是 `ImeAction.Done`（无 multi-line 标志），输入法执行「完成」动作后自行收起。现在两个 composer 输入框（行内 + 全屏）声明 `TextInputOptions.Multiline=True`，Android 侧发布 `TYPE_TEXT_FLAG_MULTI_LINE | IME_FLAG_NO_ENTER_ACTION`，回车键成为换行键、键盘保持打开；桌面端 Enter 发送 / Shift+Enter 换行逻辑不变。
- **验证**：
  - 新增单测 `WorkspaceVisibilityTests`（2 个：全量工作区、非当前工作区归档后仍可选）与 `SessionDraftTests`（6 个：按会话存取/清除、落盘回读、跨桌面隔离、切会话恢复、再次「新建会话」复用空白会话并恢复草稿、发送后不再复用该会话）；仓库自带 in-process runner：**Core + App 共 257/257 通过**（vstest testhost 在本沙箱不可用，沿用 `build/test-runner`）。
  - Solution/App/Android Debug 构建 0 warning 0 error；模拟器（API 36，Debug APK）实测三项行为并留证：工作区计数 0 仍列出（选择器可选、会话抽屉不出现空工作区孤立标题）、`draft2` 切走再点「新建会话」仍在、`dumpsys input_method` 为 `inputType=0x20001 imeOptions=0x52000000 mInputShown=true` 且回车后 `PromptBox` 文本为 `abc\n`、消息未发送。证据：`docs/evidence/v165-*.png`、`v165-ime-editorinfo.txt`。
- **发布 APK**：`dist/apk/dshremote-1.6.5-37-arm64-release.apk`（36,493,920 bytes = 34.8 MB，versionName 1.6.5 / versionCode 37，sha256 `f4c48c5110be14488ecb6e669797a46faf76a0861abdb211fe96781ae472bc81`，apksigner 通过，签名证书 `b5613a45…493d6727`，同签名可覆盖安装）。
- **上传 VPS**：已上传资源中心 `dsh-remote` 分类并复验（网关索引远端 sha256 一致、`keepVersions=1` 保留最近 3 个版本：1.6.5 / 1.6.4 / 1.6.3；`gateway v0.4.0`、`tunnels=1`、直链 HEAD 200 且 content-length 一致）：<https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.6.5-37-arm64-release.apk>（下载页 <https://203.0.113.10:9443/__gateway/download>）。

## 2026-11-13 v1.7.0：跟随 Arc/PuraMarkdown 0.2 Markdown 引擎重构重新适配（链接/块布局/分帧渲染）

- **上游变化（必须同步适配）**：Arc.Avalonia 提交 `9422bb5` 把 `ArcMarkdownView` 的解析/渲染/流式/块布局
  委托给 **PuraMarkdown.UI 0.2.0-alpha.1**（ADR-0008 / `docs/31-markdown-puramarkdown-0.2-migration.zh.md`）：
  - 引擎每个顶层块渲染成独立控件并堆叠（`PuraRootHost`），宿主不再需要按空行拆分文档；
  - 行内链接改为 **文本 run**（`ArcMarkdownLinkInline : Span`，accent 下划线、与正文同一行盒），点击由字符
    矩形命中后弹 Arc 菜单（复制链接 / 在浏览器中打开），不再产生 `HyperlinkButton`；
  - Arc 默认 pipeline 内置 **CJK 裸 URL autolink**（`下载页：https://…` 直接成链）；
  - `Markdown` 默认 `Auto`：>4 KB 文档先占位、后台解析后由 `PuraRenderScheduler` 按 ~6 ms/帧分帧挂载；
  - 包矩阵：Avalonia 12.1.5 不变，`PuraMarkdown.Core/UI/Agent` = `0.2.0-alpha.1`（`PuraMarkdown.UI`
    传递 `Avalonia.Controls.ItemsRepeater 12.0.0`），`PuraMarkdown.Extension` 存在但 Arc 不引用。
- **宿主适配**（`app/src/DshRemote.App`）：
  1. **删除两个旧 workaround**：`MarkdownBodyView.Preprocess`（把裸 URL 包成 `<…>`，v1.4.3 引入）与
     `SplitTopLevelBlocks`（按空行拆成多个 `ArcMarkdownView`，v1.2.1 引入）。现在**一条消息 = 一个** 
     `ArcMarkdownView`；表格/代码块/段落由引擎独立布局，松散有序列表不再被拆成多个各自从 1 开始的列表。
  2. `MarkdownBodyView` 只保留宿主策略：流式纯文本预览、60k 字符上限、`MarkdownRenderQueue` 节流构建；
     已有引擎视图只更新 `Markdown`（不再重建），`ShowPlain` 时 `Dispose()` 旧视图（避免分帧任务继续渲染
     已脱离的树），重新 attach 时重新入队，并把 Arc `Warning`（`ARC-MD-*` / `PURA-*`）写入诊断日志。
  3. `MarkdownRenderQueue` 新增 `IsIdle` / `PendingCount`；仍然保留 40 ms/视图的节流（引擎只对 >4 KB 分帧，
     ≤4 KB 仍在 `Markdown` 赋值内同步渲染，一次性构建整屏消息会卡 UI）。
  4. `AppStyles.axaml`：`ArcMarkdownView` 重模板保留（去掉自带 `PART_Scroll`，整个会话只保留外层滚动条）；
     `PART_Host` 仍为 StackPanel（现在只承载一个引擎视图），注释更新为 0.2 语义。
  5. `ChatView`：**分帧渲染高度增长时尾部跟随**（用户本就在底部才跟随；主动上滚 >96 px 后不抢滚动），
     `ScrollToEndSoon` 合帧；长图捕获前等待「队列空闲 + extent 连续 3 次不变（≤10 s）」再截，避免半渲染。
  6. `NuGet.config` 注释更新为 PuraMarkdown 0.2 四包布局；App 增加 `InternalsVisibleTo LinkProbe`。
  7. 单测：删除 14 条 `MarkdownPreprocessTests`，新增 4 条 `MarkdownBodyViewTests`（透传/空/60k 截断/边界）；
     `build/test-runner` 的 App 测试锚点改为 `MarkdownBodyViewTests`。
- **验证**：
  - Solution（App/Desktop/Android Debug）构建 **0 warning 0 error**；反射 runner **247/247**（Core + App）。
  - **桌面 link probe**（真实窗口，生产 `MarkdownBodyView` 路径，320/360/720 px）：5 个链接场景全部为
    文本 run 链接（`HyperlinkButton=0`、全部下划线、0 溢出），中文冒号后裸 URL 与 `<url>` 均为链接；
    长 URL 标签 320 px 下 3 行换行不裁切；表格+代码块+列表 5 块**无重叠**；松散列表 1 block 编号连续
    1./2./3.；9,952 字符长文档分帧完成 92 块、无占位残留；真实指针点击链接 → Arc 菜单
    （`复制链接` / `在浏览器中打开`）。桌面沙箱会话不能写系统剪贴板，剪贴板值以模拟器为准（WARN 记录）。
  - **桌面长图**（`--longshot-probe`）：`pages=1 972x756 rows=4`，链接/中文/气泡背景正常。
  - **Android 模拟器**（API 36 x86_64，Debug APK，demo 会话）：文本 run 链接与长 URL 换行正常；
    点链接 → `链接操作 / 复制链接 / 在浏览器中打开`；点「复制链接」→ 行内「**链接已复制**」；
    点「在浏览器中打开」→ `mCurrentFocus=com.android.chrome/ChromeTabbedActivity`；
    `logcat -b crash` 空、无 FATAL/ANR（`lastanr` = no ANR since boot）。
    （模拟器 AOSP 镜像缺 CJK 字形，中文显示方框为既有环境限制；桌面/真机中文正常。）
  - 证据与详细记录：[docs/evidence/v170-arc-puramarkdown-0.2/V170-ARC-PURAMARKDOWN-0.2-VERIFY.md](docs/evidence/v170-arc-puramarkdown-0.2/V170-ARC-PURAMARKDOWN-0.2-VERIFY.md)
    （含 `link-probe-run.log`、桌面/模拟器截图、长图与 report）。
- **发布 APK**：`dist/apk/dshremote-1.7.0-38-arm64-release.apk`（36,555,360 bytes = 34.86 MB，versionName 1.7.0 / versionCode 38，
  sha256 `63d79b69bd81fc914c1f4ff6b2a3d467f711e54628e147fa07025ea0966109bd`，`apksigner verify` 通过，签名证书
  `b5613a45…493d6727` 同签名可覆盖安装；`libxamarin-app.so` = 1,382,952 bytes，非增量打包截断的故障形态）。
  Release arm64 包在 x86_64 模拟器上受 ARM 翻译限制冷启动停在启动图（既有模拟器 ABI 限制，1.4.2 已记录同现象），
  Release 功能路径以同源码 Debug APK 模拟器验证 + 该包完整性/签名校验为准；真机覆盖安装由用户侧完成。
- **上传 VPS**：已用 `scripts/sync-apk.ps1` 上传到资源中心 `dsh-remote` 分类（`keepVersions=1`）并复验：HTTP 201、
  远端/回读 sha256 与本地一致（`63d79b69…6109bd`），保留 1.7.0 / 1.6.5 / 1.6.4（剪掉 1.6.3），
  gateway v0.4.0 `tunnels=1`，直链 HTTP 200 / 34.86 MiB：
  <https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.7.0-38-arm64-release.apk>
  （下载页 <https://203.0.113.10:9443/__gateway/download>）。
- **OPPO Sign（APK Signature Info）**：`OPPOSign/` 是 OPPO 官方 **签名信息查看/校验**工具（v1.1.0，GUI 只接受
  拖放，不带命令行、不做重新签名）。新增 `scripts/oppo-sign.ps1` + `build/oppo-sign/OppoSignReport.java`：复用该
  jar 内的 `ApkSignatureUtils`/`HashUtils`/`ApkVerifier`，在无窗口/CI 环境重放与 GUI 完全一致的报告（含
  **OPPO Sign** 值）。对 `dshremote-1.7.0-38` 的 release APK（sha256 `63d79b69…6109bd`）：`VERIFIED=true`，
  V2/V3 通过（`V1=false`：minSdk 24，V1 文件存在但不参与校验），证书 DN `CN=DSH Remote, OU=Mobile,
  O=DSHRemote, L=Shanghai, C=CN`，MD5 `A1:A0:98:C4:FA:AF:30:49:74:58:8A:1D:1C:5C:76:F4`，
  SHA-256 `B5:61:3A:45:E4:43:B8:43:13:0E:C4:41:E8:36:38:F3:70:4A:82:18:3B:3E:15:76:D2:4E:0B:8F:49:3D:67:27`，
  **OPPO Sign = `DA6B0A0BF403A21623509D28B97FDAC6`**。报告原文见
  [docs/evidence/v170-arc-puramarkdown-0.2/oppo-sign-apk-signature-info.txt](docs/evidence/v170-arc-puramarkdown-0.2/oppo-sign-apk-signature-info.txt)。
  复跑：`pwsh -File scripts/oppo-sign.ps1 [-Apk <apk>] [-OutFile <path>]`。若需要 OPPO 开放平台「应用签名服务」
  的官方重签，需要 OPPO 开发者账号与平台密钥（超出本工具能力）。
- **历史说明**：v1.2.1 / v1.2.6 / v1.4.2 / v1.4.3 小节中描述的 `SplitTopLevelBlocks`、`Preprocess` 与
  「链接 = HyperlinkButton」均为历史记录，已在 v1.7.0 随上游引擎重构删除/替换。

## 2026-11-14 v1.7.1：工作区运行指示点 / 会话列表顶栏精简

- **变更**：
  1. 会话列表页工作区选择器左侧的**连接状态绿点移除**（连接状态已由上方「已连接」细条表达），改为在
     **工作区下拉列表每行右侧**显示运行中绿点：该工作区有任一可见会话 `Running` 时显示，空闲工作区不显示，
     收起态的当前工作区同样生效（`WorkspaceGroupViewModel.HasRunningSessions`，随列表刷新/重建更新）。
  2. 顶栏移除「已隐藏 N 个子代理会话」文案，并清掉只服务该文案的 ViewModel 属性/字段（子代理过滤逻辑与
     离线缓存字段保留，不影响列表行为）。
- **验证**：App/Desktop/Android Debug 构建 **0 warning 0 error**；App 单测 **76/76**（新增
  `WorkspaceRunningIndicatorTests` 2 条：运行中工作区显示、归档停止后清除；本机 VSTest testhost 仍受
  “打开父进程句柄被拒”限制，用 `_scratch/app-test-runner` 反射 runner 执行）。桌面 demo 460×900 窄屏截图确认：
  工作区名左侧无绿点、运行中的 `DSH Remote` 行右端有绿点、`Arc.Avalonia`/`我的账单 APP`/`其他` 无点、顶栏无子代理数量。
  截图与复跑方法：[docs/evidence/v171-workspace-running-dot/README.md](docs/evidence/v171-workspace-running-dot/README.md)。
- **发布 APK**：`dist/apk/dshremote-1.7.1-39-arm64-release.apk`（36,555,360 bytes = 34.86 MB，versionName 1.7.1 /
  versionCode 39，sha256 `d8792f50df6d95285688b5139ae4fc02e438493d45872f956ff6f3f927ba89e8`，`apksigner verify` 通过，
  签名证书 `b5613a45…493d6727` 同签名可覆盖安装）。
- **上传 VPS**：已用 `scripts/sync-apk.ps1` 上传资源中心 `dsh-remote`（`keepVersions=1`）并复验：HTTP 201、
  远端/回读 sha256 与本地一致（`d8792f50…ba89e8`），保留 1.7.1 / 1.7.0 / 1.6.5（剪掉 1.6.4），gateway v0.4.0
  `tunnels=1`，直链 HTTP 200 / 34.86 MiB：
  <https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.7.1-39-arm64-release.apk>
  （下载页 <https://203.0.113.10:9443/__gateway/download>）。

## 2026-11-14 桌面端自动审批（relay 0.7.1 / App 1.8.3）

手机 App 关闭后审批仍会被自动处理：规则存桌面 relay，宿主进程内的 `approval/request` 监听器
（`prepend`）直接以 `allowed-once` 结算，命中范围时桌面和手机都不再弹审批。

- 设置页新增「自动审批（桌面端值守）」三级勾选树：全部工作区 → 工作区 → 会话；勾选上级默认全选下级，
  取消上级清空下级，部分选中显示半选。完全勾选的工作区覆盖以后新建的会话；部分勾选的工作区只保存勾选的
  会话 id；会话范围覆盖其全部子代理（沿 parentSession 回溯）。
- 只自动「允许一次」，不自动拒绝、不碰提问；规则损坏/匹配异常一律退回人工审批。
- 规则文件 `~/.dsh/remote/auto-approve.json`（原子写、热加载），手机经
  `GET/PUT /__relay/auto-approve`（Bearer 配对令牌，走 VPS 网关时附带 Cookie）读写。
- 旧手机端「自动允许审批」开关及其 `AutoApproveApprovals` 偏好已删除，避免绕过范围。
- **修复（1.8.3）**：程序化级联/回填时 `AutoApproveSessionOption/WorkspaceOption.SetSelected` 只触发了
  `SetSelected` 的 PropertyChanged，未通知 `IsSelected`，导致勾选工作区/全部后子勾选框仍显示未选中。
  已显式传 `nameof(IsSelected)`，并新增 PropertyChanged 回归测试。
- **发布**：`dist/apk/dshremote-1.8.3-43-arm64-release.apk`（36,575,840 bytes，sha256
  `8753eec0d6a46d9f72a5413f4fbfd6e6e14762176347699373c00d3c7a2289b6`），已按标准流程上传：
  <https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.8.3-43-arm64-release.apk>；
  relay bundle `dsh-remote-relay-0.7.1.tgz`（sha256 `38bee10bb74194295bfc1043b809843fbbb6f20ccc7359484c50b673721d3ad5`）已上传：
  <https://203.0.113.10:9443/__gateway/resources/relay/dsh-remote-relay-0.7.1.tgz>。
- 标准发布命令：`pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.8.3 -VersionCode 42`
  （构建 → 签名 → 上传 → sha256 回读校验；上传失败自动重试一次）；流程见 [docs/release.md](docs/release.md)。
- 详见 [docs/auto-approve.md](docs/auto-approve.md)。

## 2026-11-14 会话内容复制（整条消息 / 选中文本）

- **现象**：手机聊天页里的文本无法复制（用户反馈"选中后无法复制"）。根因有两层：Avalonia `SelectableTextBlock` 只把复制接在键盘 `Ctrl+C` 上（无长按/右键菜单、无选择手柄），Android 系统复制浮层也不作用于 Avalonia 自绘文本；纯触摸下 `ScrollViewer` 的滚动手势还会与选择手势互斥，常常连选区都产生不了。
- **归属结论（详见 [docs/session-copy.md](docs/session-copy.md)）**：复制交互由 **DSHRemote** 实现，Arc 不改。Arc/PuraMarkdown 已经提供"可选中"（`EnableTextSelection` → `SelectableTextBlock`）、`GetPlainText`/`CopyAllAsync` 与代码块复制按钮；"哪条消息、复制正文还是工具结果、选中后在哪里弹复制条"属于宿主产品语义。跨块选择是 PuraMarkdown 每块独立控件的架构限制，本次用"整条复制"绕过。
- **实现**：
  - `ChatMessageViewModel` 新增 `CopyText` / `CopyLabel` / `HasCopyableText` / `ShowFooter`：用户/助手/系统行复制全文，工具行复制结果（结果未到时复制参数），压缩卡复制摘要；流式行复制已收到的全文。
  - 每条消息尾部新增紧凑「复制」按钮（Ghost）；成功/失败用底部 toast 提示，不打断阅读。
  - 任一 `SelectableTextBlock`（Arc Markdown 正文、推理、工具参数/结果）选中后，transcript 右下角出现「已选择 N 字 · 复制 / 取消」浮动条；复制后清除选区并提示。
  - 剪贴板统一走 `TopLevel.Clipboard`，异常只写诊断日志。
- **验证**：Desktop/Android 构建 0 warning 0 error；App 单测 **101/101**（新增 `ChatMessageCopyTests` 6 条）；桌面 demo + UI Automation 实测 4 个消息行的复制内容与 Windows 剪贴板一致，拖动选中 18 字 → 浮动条 → 复制成功并显示「已复制 18 字」；Android 模拟器（API 36 x86_64）实测消息尾部「复制」→ toast + 诊断日志 `copy: 328 chars`，键盘剪贴板建议显示已复制正文。纯触摸拖选不产生选区（Avalonia 触摸滚动与选择手势互斥）已记录为已知限制，手机端以每行「复制」为准。证据：[docs/evidence/v184-session-copy/](docs/evidence/v184-session-copy/README.md)。
- **本地签名 Release（未上传）**：`dist/apk/dshremote-1.8.4-44-arm64-release.apk`（36,579,936 bytes = 34.89 MB，versionName 1.8.4 / versionCode 44，sha256 `39dc73aa95c092921b4ee82c4491874fcbca7c54bc1e4aa074ced23bde338a37`，`apksigner` 通过，签名证书 `b5613a45…493d6727` 同签名可覆盖安装）。构建时 VPS 网关不可达（`https://203.0.113.10:9443/__gateway/identity` 超时），因此未执行标准上传；**该版本内容已随 v1.8.5-45 一起发布**，无需再单独补 1.8.4。

## 2026-11-15 v1.8.5：会话页移除「工作区名」切换按钮（连同会话抽屉）

- **改动**：聊天页头部左侧那个「工作区名」按钮删除。它唯一的作用是拉开会话侧边栏抽屉（`ArcDrawer`）直接切会话；而切会话本来就是「‹ 返回列表 → 选会话」，两步之内就能到，这个按钮只是把会话标题挤进了 `MaxWidth=112` 的窄格子里。
- **连带删除（不留死 UI）**：`SidebarViewModel`（4 个命令全部只服务抽屉）、`SessionViewModel.Sidebar` / `WorkspaceTitle`、`MainViewModel.Sidebar` / `ResolveWorkspaceTitle` / `CurrentSessionId` 同步、`SessionListViewModel.IsCurrent`（唯一消费者是抽屉行高亮）、`AppStyles.axaml` 的 3 条 `Button.sidebar-row*` 样式、`ChatView.axaml` 的 `xmlns:overlays` 引用；表头列由 `Auto,Auto,*,Auto` 收敛为 `Auto,*,Auto`。净减约 280 行。
- **副作用（正向）**：会话标题不再被截断，拿回完整宽度；返回按钮、标题长截图菜单、模型 chip 的绑定与行为均未改动。
- **验证**：Desktop / Smoke / replay-probe 构建 `0 warning 0 error`；单测 `DshRemote.Core.Tests` 187 + `DshRemote.App.Tests` 101 = **288/288**；桌面 demo 前后对比截图 `_work/ui-verify/shots/v2xx-before-chat-header.png` / `v2xx-after-chat-header.png`（头部由「‹ 工作区名｜被截断的标题」变为「‹ 完整标题」）。改动前的 10 个文件备份在 `_work/backup-20261115/`。
- **发布**：`dist/apk/dshremote-1.8.5-45-arm64-release.apk`（36,575,840 bytes = 34.88 MB，versionName 1.8.5 / versionCode 45，sha256 `b2a56bde1404fc3983fcb61ae38e3802904b698531e11dded5b3c2a5ef76490e`），已按标准流程上传并回读校验通过：
  <https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-1.8.5-45-arm64-release.apk>（下载页 <https://203.0.113.10:9443/__gateway/download>）。
  资源中心现保留 **1.8.5-45 / 1.8.3-43 / 1.8.2-42**，按 `keepVersions=3` 清理了 1.8.1-41。
- 标准发布命令：`pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.8.5 -VersionCode 45`。

