# 本地运行与启动规范

> 适用：DSHRemote（Android App + `dsh-remote-relay` DSH 宿主插件 + VPS 网关）。
> 本文只讲**怎么在开发机上把它跑起来**；配对/安全/协议的细节见 [`install.md`](install.md)、
> [`protocol-v1.md`](protocol-v1.md)、[`remote-v2.md`](remote-v2.md)。

## 1. 一句话

这是三层结构，**没有"一个 exe 双击就跑"的入口**：

| 层 | 形态 | 标准启动 |
|---|---|---|
| relay（必需） | DSH 宿主插件（Cordis，Node ESM） | `pwsh -File scripts/dev-install.ps1` |
| App（手机） | Android APK（`net10.0-android`） | `pwsh -File scripts/build-apk.ps1 -Install` |
| 桌面壳（可选，调试用） | Avalonia `WinExe`（`net10.0`） | `dotnet run --project app/src/DshRemote.App.Desktop` |

## 2. relay：必须由 DSH 宿主加载

`relay/src/index.mjs` 是 **Cordis 插件**，靠宿主注入的 `ctx.webServer` / `ctx.connection` 拿端口与
`launchToken`。它**不是独立服务器**：

```powershell
node relay/src/index.mjs   # ❌ 不要这样跑：没有 ctx，起不来也不会有代理
```

正确流程（仓库根目录，需要桌面 DSH 正在运行）：

| 步骤 | 命令 | 说明 |
|---|---|---|
| 1. 证书（一次性） | `pwsh -File scripts/gen-tls.ps1` | 自签，SAN 含本机 LAN IP，输出 SPKI 指纹 |
| 2. 装进桌面 profile | `pwsh -File scripts/dev-install.ps1` | junction 指向工作树 + 受管 patch 块 + HMR 热载 + 等健康；改 relay 源码后重跑即可 |
| 3. 验链路 | `pwsh -File scripts/relay-smoke.ps1 -IncludeWs` | `/connect` 302 + `session/list` 200 + WSS 101 + `$events` ready + 自检能力位 |
| 4. 扫码配对 | `pwsh -File scripts/show-pairing.ps1` | 打开 `https://127.0.0.1:8443/__relay/pair`（仅 loopback），手机扫码 → `dshremote://` 深链 |
| 5. 改网关地址 | `pwsh -File scripts/relay-config.ps1 [-Host <ip/域名>] [-Port 9443] [-Disable]` | 保存后立即重连，不需要重启 DSH |

> 分发/换机场景用 `scripts/install-relay.ps1`（zip → `$DSH_HOME/plugins/dsh-remote-relay`）或
> `scripts/install-relay-bundle.ps1`（插件市场 bundle），开发期一律用 `dev-install.ps1`。

## 3. App（Android）

前置：.NET 10 SDK + Android workload、JDK 21、Android SDK（默认 `D:\SmartBillAndroidSdk`，
可用 `DSHREMOTE_ANDROID_SDK` 覆盖）、`adb devices` 能看到设备。

| 场景 | 命令 |
|---|---|
| 构建 Debug APK | `pwsh -File scripts/build-apk.ps1` |
| 构建并安装到设备 | `pwsh -File scripts/build-apk.ps1 -Install` |
| Release 签名 APK | `pwsh -File scripts/build-release-apk.ps1 -StorePass $pass -DisplayVersion 1.0.1 -VersionCode 11` |
| 手机自取 APK | `pwsh -File scripts/show-download.ps1` → `https://<host>:8443/__relay/download` |
| 桌面壳（调试 UI） | `dotnet run --project app/src/DshRemote.App.Desktop` |

真机安装提示：ColorOS/OPlus 对**全新安装**会弹「安装增强防护」，adb 安装报 `Failure [-99]`；
手机上按「更多 → 开始深度扫描 → 继续安装」即可。同签名升级无此问题。

## 4. 测试与端到端

| 场景 | 命令 | 说明 |
|---|---|---|
| App 单测（标准） | `dotnet test app/tests/DshRemote.Core.Tests` | 在当前终端直接可用时首选 |
| App 单测（受限终端兜底） | `pwsh -File scripts/run-tests.ps1` | 见下方说明 |
| relay 单测 | `cd relay; npm test` | 另含 `npm run check`（语法检查）、`npm run smoke:acl` |
| 全链路 E2E | `pwsh -File scripts/e2e.ps1 -Serial <serial>` | relay smoke → 单测 → 协议 smoke → APK 构建/安装 → 启动截图；日志在 `_scratch/e2e/<时间戳>/` |

`scripts/run-tests.ps1` 存在的原因：`dotnet test`（VSTest）会起 testhost 并打开父进程句柄做存活探测，
在**受限 / 被父进程托管的终端**里会以 `Win32Exception 5 (拒绝访问)` 直接失败（一个测试都没跑）。
脚本改为"构建 + 用 `scripts/test-fallback` 的进程内 runner 执行测试程序集"，计数与 `dotnet test` 一致；
加 `-PreferDotnetTest` 会先试正常路径、失败再回退。

## 5. 为什么这里仍要提"别双击 exe"

- **桌面壳** `app\src\DshRemote.App.Desktop\bin\Debug\net10.0\DshRemote.App.Desktop.exe` 是 apphost，
  双击会触发附件管理器「打开文件 - 安全警告」或 SmartScreen，用 `dotnet run` 即可。
- **手机 APK** 走的是 Android 安装器（`install-apk`/adb），与 Windows 壳层无关；
  但通过 relay 下载页拿到的 APK 属于"从网络获取"，手机侧会各自弹安装确认，属正常流程。

## 6. 排障对照

| 现象 | 真实原因 | 处理 |
|---|---|---|
| `dev-install` 后 health 不通 | HMR 没触发 / 端口占用 / `engines.dsh` 不匹配 | 重跑脚本；`EADDRINUSE` 换 `-Port`；升级 relay 包 |
| relay 起来了但 `pairing.active:false` | 自检失败（DSH 升级导致端点/参数形状变化） | 看 `protocolProbe.missing/errors`，或回滚 DSH |
| `/connect` 401 | DSH 重启换了 token | 手机重新读 pairing（插件会自动重写） |
| `/connect` 403 | 中间又加了一层反代 | 直连 relay，不要套代理 |
| `dotnet test` 报 `Win32Exception 5` | 受限终端 / 完整性级别过低 | `pwsh -File scripts/run-tests.ps1` |
| 全新安装报 `Failure [-99]` | ColorOS 安装增强防护 | 手机上按提示放行后重装 |
| `zipalign` 打不开 zip | Android SDK 路径含中文 | 换纯 ASCII 路径（`DSHREMOTE_ANDROID_SDK`） |

## 7. 相关文档

- [`install.md`](install.md) —— 新机器从零安装（证书 → relay → 配对 → APK → 真机）
- [`upgrade.md`](upgrade.md) / [`release.md`](release.md) —— 升级 / 发版流程
- [`vps-gateway.md`](vps-gateway.md) / [`relay-config.md`](relay-config.md) —— 公网网关与中继配置
- [`../README.md`](../README.md) —— 里程碑、验收矩阵、脚本清单
- 跨项目统一规范：[`VibeCoding/README.md`](../../README.md)
