<#
.SYNOPSIS
  Package the dsh-remote-relay host plugin into a distributable zip.

.DESCRIPTION
  Produces dist/dsh-remote-relay-<version>.zip containing:
    dsh-remote-relay/package.json
    dsh-remote-relay/README.zh.md
    dsh-remote-relay/src/*.mjs
    dsh-remote-relay/INSTALL.md   (generated install/upgrade notes)

  TLS material (relay/certs/) is deliberately NOT packaged: the private key is
  machine-local and every install generates or copies its own certificate.
  A .sha256 sidecar and a printed summary make the artifact verifiable.

  The package is consumed by scripts/install-relay.ps1 -FromZip <zip>.

.PARAMETER OutputDir
  Output directory (default: <repo>/dist).

.PARAMETER Version
  Override the version recorded in the file name (default: relay/package.json).

.PARAMETER Force
  Overwrite an existing artifact.

.PARAMETER SkipChecks
  Skip `node --check` syntax validation of the packaged modules.
#>
[CmdletBinding()]
param(
  [string]$OutputDir,
  [string]$Version,
  [switch]$Force,
  [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$relayDir = Join-Path $repoRoot 'relay'
$manifestPath = Join-Path $relayDir 'package.json'
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }

if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Relay manifest not found: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $manifest.version }
if (-not $Version) { throw 'Cannot determine relay version (pass -Version).' }
if ($manifest.name -ne 'dsh-remote-relay') { throw "Unexpected package name: $($manifest.name)" }

$srcDir = Join-Path $relayDir 'src'
$modules = @(Get-ChildItem -LiteralPath $srcDir -Filter '*.mjs' -File | Sort-Object Name)
if ($modules.Count -eq 0) { throw "No plugin modules found under $srcDir" }
$entry = Join-Path $srcDir 'index.mjs'
if (-not (Test-Path -LiteralPath $entry)) { throw "Plugin entry not found: $entry" }

if (-not $SkipChecks) {
  $node = Get-Command node.exe -ErrorAction SilentlyContinue
  if (-not $node) {
    Write-Warning 'node.exe not found on PATH; skipping module syntax checks.'
  } else {
    foreach ($module in $modules) {
      & $node.Source --check $module.FullName
      if ($LASTEXITCODE -ne 0) { throw "node --check failed: $($module.Name)" }
    }
    Write-Host "Syntax checks passed for $($modules.Count) module(s)." -ForegroundColor Green
  }
}

if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$zipPath = Join-Path $OutputDir "dsh-remote-relay-$Version.zip"
$shaPath = "$zipPath.sha256"
if (((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $shaPath)) -and -not $Force) {
  throw "Artifact already exists: $zipPath (pass -Force to overwrite)"
}

$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-pkg-" + [guid]::NewGuid().ToString('N'))
$stagePlugin = Join-Path $stageRoot 'dsh-remote-relay'
New-Item -ItemType Directory -Path (Join-Path $stagePlugin 'src') -Force | Out-Null

try {
  Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stagePlugin 'package.json')
  $bundlePatch = Join-Path $relayDir 'cordis.patch.yml'
  if (Test-Path -LiteralPath $bundlePatch) { Copy-Item -LiteralPath $bundlePatch -Destination (Join-Path $stagePlugin 'cordis.patch.yml') }
  $readme = Join-Path $relayDir 'README.zh.md'
  if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination (Join-Path $stagePlugin 'README.zh.md') }
  foreach ($module in $modules) {
    Copy-Item -LiteralPath $module.FullName -Destination (Join-Path $stagePlugin "src\$($module.Name)")
  }
  $assetsDir = Join-Path $relayDir 'assets'
  if (Test-Path -LiteralPath $assetsDir) {
    Copy-Item -LiteralPath $assetsDir -Destination (Join-Path $stagePlugin 'assets') -Recurse
  }

  $installNotes = @"
# dsh-remote-relay $Version — 安装 / 升级说明

本包是 DSH 宿主侧薄插件（Cordis，Node ESM，零第三方依赖）。它把桌面 DSH 进程内
loopback 的官方 Web/Remote 服务以 TLS 暴露到一个局域网/VPN 地址，并输出手机配对信息。

## 前置

- Windows 上的 DSH 桌面版正在运行（本包按 DSH >= 0.2.2-rc.1 自检，启动时探测
  \$connect / session/list / session/events / session/follow / \$events）。
- 本仓库的 `scripts/` 目录（安装脚本、证书生成、smoke）。

## 安装（推荐）

```powershell
pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-$Version.zip -BindHost <本机 LAN IP>
```

安装脚本会：

1. 复制本包到 ``%DSH_HOME%\plugins\dsh-remote-relay``（默认 ``$env:USERPROFILE\.dsh``）；
2. 若没有证书，用 `scripts/gen-tls.ps1` 在该目录下生成自签 TLS 材料；
3. 在 ``%DSH_HOME%\profiles\desktop\node_modules`` 建 junction；
4. 在 profile 的 `cordis.patch.yml` 写入受管 patch 块（先备份）；
5. 触发 HMR 热加载并等待 ``/__relay/health`` 返回 ``protocolProbe.ok=true``。

## 验证

```powershell
pwsh -File scripts/relay-smoke.ps1 -IncludeWs
```

## 升级 / 卸载

```powershell
pwsh -File scripts/install-relay.ps1 -FromZip <new zip> -BindHost <ip>   # 升级
pwsh -File scripts/uninstall-relay.ps1                                    # 卸载（保留插件目录）
pwsh -File scripts/uninstall-relay.ps1 -RemovePluginDir                    # 卸载并删除目录
```

## 注意

- 本包**不包含**私钥/证书；每台机器用 `scripts/gen-tls.ps1` 生成，`pairing.json` 的 `fp`
  是证书 SPKI SHA-256 pin。
- ``%USERPROFILE%\.dsh\remote\pairing.json`` 含 launch token（等价于密码），勿外传。
- 桌面 DSH 重启后 `token`/`port` 会变化，插件会自动重写 pairing；手机需重新加载 pairing 文件。
"@
  Set-Content -LiteralPath (Join-Path $stagePlugin 'INSTALL.md') -Value $installNotes -Encoding utf8NoBOM

  if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
  Compress-Archive -Path $stagePlugin -DestinationPath $zipPath -CompressionLevel Optimal
  $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath $shaPath -Value "$hash  dsh-remote-relay-$Version.zip`n" -Encoding ascii
} finally {
  Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$zip = Get-Item -LiteralPath $zipPath
Write-Host ''
Write-Host 'Relay package:' -ForegroundColor Green
Write-Host "  zip    : $($zip.FullName)"
Write-Host "  version: $Version"
Write-Host "  size   : $([math]::Round($zip.Length / 1KB, 1)) KB"
Write-Host "  sha256 : $(Get-Content -LiteralPath $shaPath -Raw | ForEach-Object { ($_ -split '\s+')[0] })"
Write-Host "  install: pwsh -File scripts/install-relay.ps1 -FromZip `"$($zip.FullName)`" -BindHost <LAN IP>"



