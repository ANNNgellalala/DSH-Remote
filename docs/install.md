# 从零安装：证书 → relay → 配对 → APK → 真机

目标：在一台**新机器**上，按本文 15 分钟内让 Android 手机连上桌面 DSH。

本文分两半：

- **A. 桌面侧**（Windows + 运行中的 DSH 桌面版）
- **B. 手机侧**（Android，侧载 APK）

> 术语：`relay` = `dsh-remote-relay` 宿主插件；`pairing.json` = 手机连接所需的配对文件（含 token，等价密码）。

---

## 0. 前置检查

| 需要 | 检查命令 | 期望 |
|---|---|---|
| Windows PowerShell 7 | `$PSVersionTable.PSVersion` | 7.x |
| OpenSSL | `openssl version` 或本机 conda 路径存在 | 3.x |
| 桌面 DSH 正在运行 | `Get-Process 'DeepSeek Harness'` | 至少一个进程 |
| DSH 版本 | 由 relay 自检上报（见 §4） | `>= 0.2.2-rc.1` |
| .NET 10 SDK（构建 APK） | `dotnet --version` | 10.x |
| JDK 21 | `Test-Path 'C:\Program Files\Java\jdk-21'` | True（或设 `JAVA_HOME`） |
| Android SDK（含平台/构建工具） | `Test-Path 'D:\SmartBillAndroidSdk'` | True（或设 `DSHREMOTE_ANDROID_SDK`） |
| 手机 USB/无线 adb | `adb devices` | 一台 `device` 状态 |

`DSH_HOME` 默认为 `%USERPROFILE%\.dsh`；本机 `relay/`、`scripts/` 位于仓库根目录（下称 `<repo>`）。

```powershell
cd <repo>
```

---

## A. 桌面侧

### 1. 生成自签 TLS 证书（一次性，约 10 秒）

```powershell
pwsh -File scripts/gen-tls.ps1
```

输出示例：

```
LAN IP   : 192.0.2.10
TLS material ready:
  cert    : <repo>\relay\certs\relay-cert.pem
  key     : <repo>\relay\certs\relay-key.pem
  SAN     : localhost, DESKTOP-XXXX, 192.0.2.10, 127.0.0.1
  SPKI-SHA256: 725ed5…0d430
```

- 证书 5 年有效，SAN 覆盖 `localhost`、计算机名、LAN IPv4、`127.0.0.1`。
- **手机不信任系统 CA**：它 pin 的是 SPKI SHA-256（手机里 `fp` 字段）。只换证书、不换私钥时 `fp` 不变。
- 多网卡机器用 `-LanIp <VPN 地址>` 指定要暴露的地址（建议 WireGuard/Tailscale 地址）。
- 证书/私钥在 `relay/certs/`，已被 `.gitignore` 忽略，**不要**提交。

### 2. 安装 relay 插件

两种方式，二选一：

**方式 A（本仓库开发流，推荐在同一台开发机）**

```powershell
pwsh -File scripts/dev-install.ps1
```

它会：建 `node_modules/dsh-remote-relay` junction → 写入受管 patch 块 → 追加 reload tick 触发 HMR → 等 `/__relay/health`。整个过程**不重启桌面 DSH**。

**方式 B（分发流：从 zip 安装到 `$DSH_HOME\plugins`）**

```powershell
pwsh -File scripts/package-relay.ps1        # 产出 dist/dsh-remote-relay-<ver>.zip
pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-0.2.0.zip -BindHost 192.0.2.10
```

`install-relay.ps1` 会：复制插件到 `%DSH_HOME%\plugins\dsh-remote-relay` → 复用/生成证书 → 建 junction → 写受管 patch 块（先备份）→ 热加载并等待自检通过。支持 `-WhatIf`（只演练）、`-NoReload`（只落盘）。

两者都是幂等的：重复执行只更新受管块/备份，不会重复插入。

### 2b. 以 DSH bundle 安装（M6，插件页可见，推荐）

DSH「设置 → 插件」只列 bundle（`package.json.dsh.bundle.patch`），纯 patch insert 不会出现。relay 已 bundle 化：

```powershell
pwsh -File scripts/package-relay-plugin.ps1     # 构建 dist/dsh-remote-relay-<ver>/ + .tgz
pwsh -File scripts/install-relay-bundle.ps1     # 迁移 dev insert -> 宿主 pluginManager/installBundle
# 需要指定公网中继服务器时（写入 relay-config.json 并即时生效）：
pwsh -File scripts/relay-config.ps1 -Host <gateway-host> -Port 9443 `
  [-Path /__gateway/tunnel] [-Fingerprint '<gateway SPKI sha256>']
```

脚本会：读 pairing 取 loopback 端口+launchToken → 换 API cookie → 删除 profile 受管 dev insert（备份）与 dev junction →
用宿主 `pluginManager/installBundle`（绝对 bundle 目录）安装 → `waitForInstall`/`listBundles` 校验 →
一次性迁移（无 `relay-config.json` 时把本机旧配置写入该文件）→ 等 health/自检；失败自动回滚 patch 并重跑 `dev-install.ps1`。
公网中继服务器配置保存在 `~/.dsh/remote/relay-config.json`（详见 [relay-config.md](relay-config.md)），不再写进 profile patch。
**不要手写 profile package.json / 直接跑 CLI 安装**（桌面 profile 由 Electron 管理）。

TLS 默认路径改为 `~/.dsh/remote/certs/relay-cert.pem`/`relay-key.pem`（bundle 不含证书）；`scripts/gen-tls.ps1`
现在默认输出到该目录，`tls.certFile/keyFile` 配置优先。验证：

```powershell
pwsh -File scripts/relay-smoke.ps1 -IncludeWs
# 宿主 RPC（可选）：pluginManager/listBundles 应含 dsh-remote-relay（installed=true）
```

### 3. 验证 relay 链路

```powershell
pwsh -File scripts/relay-smoke.ps1 -IncludeWs
```

期望（全 PASS，退出码 0）：

```
PASS  pairing active=true relayVersion=0.2.0 dshVersion=0.2.2-rc.1
      $connect=True session/list=True session/events=True session/follow=True $events=True
PASS  health 200 (… relayVersion=0.2.0, dshVersion=0.2.2-rc.1 …)
PASS  protocol probe ok: $connect=True session/list=True … $events=True
PASS  connect 302, Set-Cookie name=dsh-auth-…
PASS  session/list 200, ok=true, rpcId=…, sessions=…
PASS  WebSocket upgrade 101 (HTTP/1.1 101 Switching Protocols)
PASS  $events stream ready: {"type":"item","value":{"type":"ready",…}}
SMOKE: PASS
```

### 4. 查看配对文件

```powershell
Get-Content "$env:USERPROFILE\.dsh\remote\pairing.json"
```

关键字段：

| 字段 | 含义 |
|---|---|
| `host` / `port` | 手机应连接的地址（LAN/VPN IP + 8443） |
| `token` | `/connect?token=` 用；**等价密码**，勿外传 |
| `fp` | 证书 SPKI SHA-256（小写 hex），TLS pinning 用 |
| `pairingUri` | `dshremote://pair?...`，等价信息的单行形式（可贴给 App） |
| `active` | `true` 才可用于配对；`false` 表示启动自检失败（见 `error`） |
| `capabilities` | 五个必需端点的探测结果 |
| `updatedAt` | 最近一次写入时间；桌面 DSH 重启后会刷新 |

健康端点（无秘密，可随时查）：

```powershell
curl.exe -k https://192.0.2.10:8443/__relay/health
```

> 防火墙：Windows 首次监听时若弹出提示，选「允许专用/公用网络」。本机以太网为「公用网络」且 `DeepSeek Harness.exe` 已有 Public 入站 Allow 规则；若手机连不上而本机 smoke 通过，优先检查防火墙入站 8443。

---

## B. 手机侧

### 5. 构建并安装 APK

**Debug（开发自用）**

```powershell
pwsh -File scripts/build-apk.ps1 -Install
```

**Release（签名、可长期侧载）**

```powershell
pwsh -File scripts/gen-signing-key.ps1            # 一次性；记下密码
$env:DSHREMOTE_KEYSTORE_PASS = '<password>'
pwsh -File scripts/build-release-apk.ps1 -DisplayVersion 0.2.0 -VersionCode 2
adb install -r -t .\dist\apk\dshremote-0.2.0-2-arm64-release.apk
```

Release 构建形态：`arm64-v8a`、APK（非 AAB）、`SdkOnly` 裁剪、本地 keystore 签名；脚本会打印体积、SHA-256 与 apksigner 验证结果。

### 5b. 手机直接从服务器下载 APK（无需 adb，推荐给不在电脑旁的用户）

relay 自带一个 **LAN 可访问** 的 APK 下载页（只暴露白名单里的 `*.apk`，无需 token）：

```powershell
# 在电脑上查看可分享的下载地址 + 最新版本/大小/sha256
pwsh -File scripts/show-download.ps1
# 输出示例：https://192.0.2.10:8443/__relay/download
```

手机操作（与电脑同一 Wi-Fi / VPN）：

1. 浏览器打开 `https://<host>:8443/__relay/download`；
2. 自签证书提示「不安全」→ **高级 → 继续前往**（App 内部另有 SPKI pin，属预期行为）；
3. 点「下载最新版 APK」→ 通知栏/下载列表点安装；
4. 若被 ColorOS「安装增强防护」拦截：**更多 → 开始深度扫描 → 继续安装**；部分 ROM 需为浏览器开启「允许安装未知应用」；
5. 安装后回到电脑运行 `pwsh -File scripts/show-pairing.ps1`，手机系统相机扫码配对（配对页仅电脑本机可访问）。

说明：

- 页面每次请求都会重新扫描下载目录（默认 `<repo>/dist/apk`，可用 relay `config.downloadDir` 或环境变量 `DSH_REMOTE_APK_DIR` 覆盖），新构建的 APK 刷新即可看到，无需重启 relay。
- 最新版按钮/直链：`/__relay/download/<文件名>.apk`；下载响应带 `X-Content-SHA256`，可用页面显示的 sha256 核对。
- 严格白名单：只允许下载扫描到的 APK 文件名，`..`/子目录/非 `.apk`/`relay/certs/*` 一律 404；没有目录列表。
- 当前 Release APK 仅 `arm64-v8a`；模拟器/非 arm64 设备请用 Debug 包（`scripts/build-apk.ps1`）。
- 若 relay 未运行，页面不存在：先 `pwsh -File scripts/dev-install.ps1`。

### 5c. 公网网关模式（手机不在同一局域网，M6）

桌面 relay 可主动连公网网关（`vps/`，TLS :9443），手机用 v2 配对经网关访问；LAN 直连同时保留。

```powershell
# 1) 桌面配置中继服务器（v0.4.0+：写入 ~/.dsh/remote/relay-config.json，保存后立即重连）
pwsh -File scripts/relay-config.ps1 -Host <gateway-host> -Port 9443 `
  -Path /__gateway/tunnel -Fingerprint '<gateway SPKI sha256>'
# 也可以打开 https://127.0.0.1:8443/__relay/pair →「中继服务器设置」表单，或直接编辑 JSON。
# 旧的 dev 安装仍可用 dev-install.ps1 -GatewayEnabled -GatewayUrl ...（仅作回退，JSON 优先）。

# 2) 确认连接器在线 + 网关隧道
curl.exe -k https://127.0.0.1:8443/__relay/health          # gateway.online=true, gateway.source=file
curl.exe -k https://<gateway-host>:9443/__gateway/identity  # tunnels=1, fp

# 3) 经网关验证（等价手机路径）
pwsh -File scripts/relay-smoke.ps1 -Gateway -IncludeWs

# 4) 打开本机管理页：中继服务器设置 / v2 二维码 / 桌面名 / 服务器地址 / Token 掩码 + 「生成新 Token」
pwsh -File scripts/show-pairing.ps1
```

- 手机扫码/粘贴 v2 URI（`dshremote://pair?v=2&host=<gateway>&port=9443&token=<remoteToken>&fp=<gateway fp>&mode=gateway`）；
  `fp` 是**网关证书** SPKI（不是桌面自签证书）。
- 远程 Token 持久化在 `~/.dsh/remote/identity.json`；「生成新 Token」后旧 token 在网关上立即 404，手机需重新配对。
- 网关不可用时 relay 回退 v1（LAN 仍可用），`health.gateway.error` 给出原因；详见 [docs/remote-v2.md](remote-v2.md)。

### 6. 扫码配对（推荐）

relay 提供一个**仅本机可访问**的配对页，把 `pairingUri` 渲染成二维码；手机用系统相机扫码即可通过 `dshremote://` 深链拉起 App：

```powershell
pwsh -File scripts/show-pairing.ps1            # 检查 relay，然后打开默认浏览器
pwsh -File scripts/show-pairing.ps1 -NoBrowser # 只打印 URL（端口可用 -Port 指定）
```

- 地址：`https://127.0.0.1:8443/__relay/pair`。自签证书 → 浏览器提示不安全时选「高级 → 继续访问」。
- 页面内容：二维码（= `pairingUri`，含 token/fp）、`host:port`、SPKI 指纹、relay/DSH 版本、更新时间、过期提示。
- 手机：系统相机/扫码对准二维码 → `dshremote://pair?...` → App 打开并解析 host/port/token/fp。
- 安全：页面只对 loopback 开放（非本机访问 **404**），带 `Content-Security-Policy` 与 `no-store`；token 只出现在本机页面，不写日志。
- `pairing.json` 为 `active:false` 时页面显示失败原因而不是二维码（先修自检，见 §3）。
- 若桌面 DSH 重启（token 变化），刷新页面或重跑 `show-pairing.ps1`。

### 6b. 把配对推到手机（备用/自动化）

```powershell
$dir = '/sdcard/Android/data/dev.dshremote.app/files'
adb shell mkdir -p $dir
adb push "$env:USERPROFILE\.dsh\remote\pairing.json" "$dir/pairing.json"
```

### 7. 首次连接

1. 手机与电脑同一 Wi-Fi（或用 VPN 地址）。
2. 扫码配对（§6）或打开 App →「从文件加载配对」→ 选择 `pairing.json`（或粘贴 `pairingUri`）。
3. 点「连接」→ 应看到会话列表（运行中的会话带 ●）。
4. 打开会话 → 历史 + 实时输出；发消息；点「停止」验证写操作。

### 8. 可选：一键验证

```powershell
pwsh -File scripts/e2e.ps1                 # 全链路（会构建 Release APK、安装、启动、截图）
pwsh -File scripts/e2e.ps1 -SkipAndroid    # 只跑 relay + 单测 + 协议 Smoke
```

---

## 验收清单

- [ ] `relay-smoke.ps1 -IncludeWs` 全 PASS
- [ ] `https://<host>:8443/__relay/download` 能打开并列出最新 APK，页面 sha256 与磁盘/`show-download.ps1 -Json` 一致
- [ ] `pairing.json` 的 `active:true`、`capabilities` 五项全 true
- [ ] health 的 `dshVersion` 与桌面 DSH 实际版本一致
- [ ] APK 安装成功、启动不崩（`e2e.ps1` 会截图到 `_scratch/e2e/<ts>/app-launch.png`）
- [ ] `show-pairing.ps1` 打开的 loopback 页面显示二维码（非本机访问 `/__relay/pair` 返回 404）
- [ ] 手机扫码能拉起 App（或手动加载 pairing 后连接）
- [ ] 手机能看到会话列表并打开历史
- [ ] 发消息后桌面会话继续执行，停止按钮生效
- [ ] 桌面 DSH GUI 仍正常（relay 不改桌面绑定）

## 常见问题

| 现象 | 处理 |
|---|---|
| `gen-tls.ps1` 报 openssl 找不到 | 传 `-OpenSslPath`，或安装 OpenSSL 并加入 PATH |
| `dev-install` 后迟迟不健康 | 看桌面日志是否有 `[remote-relay]`；重跑一次触发 HMR tick；确认 8443 未被占用（`Get-NetTCPConnection -LocalPort 8443`） |
| health 的 `protocolProbe.status=failed` | 按 `missing`/`errors` 定位：DSH 升级导致端点/参数变化 → 升级 relay/Core 或回滚 DSH |
| pairing `active:false` | 不要配对；修好自检后重跑 `dev-install.ps1`/`install-relay.ps1` |
| 手机 401 | pairing 过期（DSH 重启换了 token）→ 重新 push pairing |
| 手机 403 | 没有直连 relay（中间又套了代理/改了 Host） |
| 手机 TLS 报 pin 不匹配 | 私钥换了（重新 gen-tls 且未保留旧证书）→ 重新配对 |
| Release APK 启动崩溃 | `build-release-apk.ps1 -LinkMode None` 重试；仍崩溃则为 Avalonia 裁剪兼容问题，记录到发布说明 |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | 换了签名 key：`adb uninstall dev.dshremote.app` 后重装（`e2e.ps1` 自动处理一次） |
| `adb install` 报 `Failure [-99]` / logcat 有 `PC install attack detected` | 厂商（ColorOS/OPPO/一加等）反诈安装守护拦截 adb 安装；APK 本身有效（`apksigner verify` 通过）。改用下载页在手机上安装，或关闭厂商安装守护后重试 |
| 手机打不开 `/__relay/download` | 手机与电脑不同网段/未连 VPN；Windows 防火墙未放行 8443；或 relay 未运行（先看 `/__relay/health`） |
| 手机下载后安装被拦截 | ColorOS「安装增强防护」：更多 → 开始深度扫描 → 继续安装；为浏览器开启「允许安装未知应用」；核对下载页 sha256 |
| APK 实际 ~33.7MB（>30MB 目标） | 当前形态：Avalonia 12 + .NET 10 + arm64-only + `SdkOnly`；如需更小需评估裁剪/AOT，但未验证 `Full` 裁剪下 Avalonia 反射稳定性 |
| 构建报 Android SDK/JDK 找不到 | `-AndroidSdkDirectory` / `-JavaSdkDirectory` 或环境变量 `DSHREMOTE_ANDROID_SDK` / `JAVA_HOME` |
| APK > 40MB | 检查是否误用多 ABI / `AndroidLinkMode=None`；记录原因到发布说明 |



