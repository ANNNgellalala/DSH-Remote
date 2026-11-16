# 本机配置

`config/dshremote.settings.json` 保存**只属于这台机器**的部署参数，已被 `.gitignore`
排除，不会提交到仓库。

首次使用：

```powershell
Copy-Item config/dshremote.settings.example.json config/dshremote.settings.json
# 然后按实际情况修改 sshHost / sshPort / publicHost / lanIp / 工具路径等
```

| 字段 | 用途 |
|---|---|
| `deploy.sshHost` / `sshPort` / `sshUser` / `keyFile` | `deploy-gateway.ps1`、`upload-resource.ps1`、`sync-apk.ps1`、`vps-tunnel.ps1` 连接 VPS 使用 |
| `deploy.publicHost` / `publicPort` | 网关对外地址（TLS 证书 SAN、下载页、上传校验） |
| `deploy.extraSan` | 额外写进网关证书 SAN 的 DNS 名（可留空数组） |
| `network.lanIp` | 生成 relay 证书时使用的 LAN IPv4；留空则自动探测默认路由网卡 |
| `tools.openSslPath` | `gen-tls.ps1` 使用的 `openssl.exe`；留空则在 `PATH` 中查找 |
| `tools.androidSdkDirectory` / `javaSdkDirectory` | 可选的 Android SDK / JDK 覆盖值；留空则走环境变量与脚本默认探测 |

所有脚本都支持用同名参数覆盖配置，例如：

```powershell
pwsh -File scripts/deploy-gateway.ps1 -SshHost 198.51.100.20 -SshPort 22022
pwsh -File scripts/gen-tls.ps1 -LanIp 192.0.2.10 -OpenSslPath C:\OpenSSL\bin\openssl.exe
```

> 没有 `dshremote.settings.json` 时脚本会回退到 `dshremote.settings.example.json`
> 里的占位值（`203.0.113.10` 等），只适合只读检查；真正部署前必须创建本机配置。
