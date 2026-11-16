# 标准发布流程（APK / relay）

本仓库的发布物只有两类，**两者都必须上传到 VPS 资源中心并经回读 sha256 校验**；
只在本地构建、未上传的产物只能用于调试/模拟器。

## 0. 前置检查

```powershell
# 单测（受限环境用 run-tests.ps1；正常环境可直接 dotnet test）
pwsh -File scripts/run-tests.ps1

# relay 单测 + 语法
cd relay; npm test; npm run check; cd ..

# 协议自检
pwsh -File scripts/relay-smoke.ps1 -IncludeWs
```

## 1. 发布 App（APK）

```powershell
pwsh -File scripts/release-apk.ps1 -DisplayVersion <x.y.z> -VersionCode <n>
```

该脚本等价于：

1. `build-release-apk.ps1`：publish 签名 APK（arm64、SdkOnly）→ `dist/apk/`；
2. `sync-apk.ps1`：上传 VPS 资源中心 `dsh-remote`（默认保留最近 3 个版本）；
3. 校验网关返回的 sha256 + 重新 HTTPS 下载回读 sha256（必须与本地一致）。

上传失败（例如网关隧道正好重连）会自动重试一次；第二次仍失败才判定发布失败。

产物：

```
dist/apk/dshremote-<x.y.z>-<n>-arm64-release.apk
https://203.0.113.10:9443/__gateway/resources/dsh-remote/dshremote-<x.y.z>-<n>-arm64-release.apk
```

- 仅本地调试：`-NoPublish`（会打印 WARNING，禁止当发布包）。
- 上传后用 `scripts/upload-resource.ps1 -List -Id dsh-remote` 复核。
- 同一 `versionCode` 不能覆盖安装；每次发布必须递增。

## 2. 发布 relay（版本变化时）

```powershell
pwsh -File scripts/package-relay-plugin.ps1
pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-<ver>.tgz -KeepVersions
```

本机安装/升级：

```powershell
pwsh -File scripts/install-relay-bundle.ps1 -ForceReinstall
```

安装后确认 `health.relayVersion=<ver>` 与 `health.autoApprove` 存在，再跑一次
`relay-smoke.ps1 -IncludeWs`。

## 3. 发布记录

在 README 对应版本小节记录：

- 版本号 / versionCode / APK sha256 / 大小；
- relay 版本与 health 自检结果；
- 单测与构建结果；
- VPS 资源中心保留的版本列表与直链。

## 4. 禁止事项

- 不把 `dist/apk` 里的旧包手工挑一个上传而不递增 versionCode；
- 不上传未签名的 Debug APK 到 `dsh-remote`；
- 不跳过 `sync-apk.ps1` 的回读 sha256 校验；
- 不把 `app/keys/`、`local-password.txt` 或上传令牌写入仓库。
