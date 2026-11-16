# 外网访问：把 DSH Remote 通过 VPS 暴露到公网

用一台 VPS 做**公网入口**，桌面侧保持 relay 不变，通过一条常驻 SSH 反向隧道把公网端口转发到桌面 relay。手机在外面（蜂窝网络/其他 Wi-Fi）即可继续操控桌面 DSH。

## 为什么 relay 不直接部署在 VPS

DSH 的 `launchToken` 是**桌面进程级随机值**，官方 Web 服务只监听 `127.0.0.1:<随机端口>`。relay 必须运行在桌面 DSH 进程内才能拿到 token 并反代 loopback 服务。因此 VPS 上只放「公网入口 + 反向隧道」，手机流量路径：

```
手机 ──TLS──▶ VPS:9443 ──sshd 反向隧道──▶ 桌面 127.0.0.1:8443 (dsh-remote-relay) ──▶ DSH 127.0.0.1:<随机>
```

TLS 在桌面 relay 终止，手机仍用 **SPKI 证书固定**；VPS 只转发 TCP，看不到明文。

## 当前部署（2026-11-09）

| 项 | 值 |
|---|---|
| VPS | `203.0.113.10`（ExampleVPS，Ubuntu 26.04） |
| SSH | `root@203.0.113.10:22222`，密钥 `%USERPROFILE%\.ssh\id_ed25519` |
| 公网入口 | `https://203.0.113.10:9443`（UFW 已放行 9443/tcp） |
| 隧道 | 桌面 → VPS `-R 0.0.0.0:9443:127.0.0.1:8443`，由 `scripts/vps-tunnel.ps1` 托管 |
| 配对地址 | pairing.json/二维码里的 host/port 已切换为 `203.0.113.10:9443`，同时保留 `localHost/localPort/localPairingUri`（LAN 直连可用） |
| APK 下载 | `https://203.0.113.10:9443/__relay/download`（公网可用，自签证书提示选「高级 → 继续前往」） |

VPS 上**没有新服务**，只使用现有 sshd（`GatewayPorts clientspecified` 已在 `/etc/ssh/sshd_config.d/00-hardening.conf` 中启用）。

## 部署步骤（可复现）

1. **桌面 relay 声明公网配对地址**（只改配对文档，不改监听地址）：
   ```powershell
   pwsh -File scripts/dev-install.ps1 -PublicHost 203.0.113.10 -PublicPort 9443 -RequireReload
   # 输出 pairingUri: dshremote://pair?...host=203.0.113.10&port=9443&...
   # 同时 pairing.json 带 localPairingUri（192.0.2.10:8443）
   ```
   `install-relay.ps1` 同样支持 `-PublicHost/-PublicPort`（分发给别的机器时写入插件 config）。

2. **VPS 放行端口**：
   ```bash
   ssh -i C:\Users\user\.ssh\id_ed25519 -p 22222 root@203.0.113.10
   ufw allow 9443/tcp && ufw reload
   ```

3. **桌面启动隧道（带自动重连 + 开机自启）**：
   ```powershell
   pwsh -File scripts/vps-tunnel.ps1 -Action start
   pwsh -File scripts/vps-tunnel.ps1 -Action status
   pwsh -File scripts/vps-tunnel.ps1 -Action install   # 注册登录启动（无管理员权限时写 Startup 文件夹）
   ```
   参数默认即当前 VPS；可用 `-SshHost/-SshPort/-KeyFile/-PublicPort/-LocalPort` 覆盖。日志：`%USERPROFILE%\.dsh\remote\vps-tunnel.log`。

4. **手机重新配对**（一次）：把新的 `%USERPROFILE%\.dsh\remote\pairing.json` 推到手机
   （`/sdcard/Android/data/dev.dshremote.app/files/pairing.json`）后 App 内「从文件加载配对」→ 连接；
   或直接打开 `dshremote://pair?...host=203.0.113.10&port=9443...` 深链。App 顶部应显示 `203.0.113.10:9443 · TLS 指纹已固定`。

## 运维命令

```powershell
pwsh -File scripts/vps-tunnel.ps1 -Action status    # 本机 supervisor/ssh 状态 + 公网 health
pwsh -File scripts/vps-tunnel.ps1 -Action restart   # 改完网络/SSH 后重启隧道
pwsh -File scripts/vps-tunnel.ps1 -Action stop
pwsh -File scripts/vps-tunnel.ps1 -Action uninstall # 删除登录自启并停止
```

健康检查（任意能上网的机器）：
`curl -k https://203.0.113.10:9443/__relay/health` → `{"ok":true,...,"publicHost":"203.0.113.10"}`。

## 安全说明

- 公网只开 relay 的 TLS 端口（9443）；认证仍是 `launchToken` + 手机端 SPKI pin。
- **token 随桌面 DSH 重启变化**：重启后 pairing.json 自动重写，手机需重新加载配对（旧 token 立即失效）。
- pairing URI/二维码含 token，等同密码；不要外发。
- 桌面关机/睡眠时隧道断开，外网不可用；手机在 LAN 内可继续直连 `localPairingUri`。
- 如需进一步加固可在 UFW 限制来源 IP（手机蜂窝 IP 不固定，默认未限制）。

## 已验证

- VPS `curl https://203.0.113.10:9443/__relay/health` → ok，`publicHost=203.0.113.10`，5 项协议能力全 true。
- 公网下载 APK：35,469,848 bytes，sha256 与磁盘一致（约 1 MB/s）。
- 真机 TestDevice 通过公网配对连接成功：`203.0.113.10:9443 · TLS 指纹已固定`，会话列表 18 个会话正常加载；图片发送、工作区分组、左滑归档、问答卡均在 v1.1.0 真机验证。
- 隧道 kill 后 5s 内自动重连（supervisor 生效）。
