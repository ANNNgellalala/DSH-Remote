<#
.SYNOPSIS
  Build the dsh-remote-relay DSH bundle artifact (directory + .tgz).

.DESCRIPTION
  DSH's plugin page lists bundles only, so the relay ships as a bundle:
  `package.json` carries `dsh.bundle.patch = ./cordis.patch.yml` and the bundle
  inserts the `remote-relay` row. This script stages exactly the files a bundle
  install needs (no certs, no keys):

    dist/dsh-remote-relay-<version>/   package.json, cordis.patch.yml, src/,
                                       assets/, README.zh.md, INSTALL.md
    dist/dsh-remote-relay-<version>.tgz (tar -czf, for manual/npm-style installs)

  TLS material stays outside the package (default: ~/.dsh/remote/certs).

.PARAMETER OutputDir
  Output directory (default <repo>/dist).

.PARAMETER Version
  Override the version in the artifact name (default relay/package.json).

.PARAMETER Force
  Overwrite existing artifacts.

.PARAMETER NoTgz
  Skip the .tgz archive.

.PARAMETER SkipTests
  Do not run relay/test/config.test.mjs before packaging.
#>
[CmdletBinding()]
param(
  [string]$OutputDir,
  [string]$Version,
  [switch]$Force,
  [switch]$NoTgz,
  [switch]$SkipTests
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
if (-not $manifest.dsh -or -not $manifest.dsh.bundle -or -not $manifest.dsh.bundle.patch) {
  throw "relay/package.json is missing dsh.bundle.patch; cannot build a DSH bundle."
}
$patchRel = [string]$manifest.dsh.bundle.patch
$patchFile = Join-Path $relayDir ($patchRel -replace '^\./', '')
if (-not (Test-Path -LiteralPath $patchFile)) { throw "Bundle patch file not found: $patchFile" }

$bundleName = "$($manifest.name)-$Version"
$bundleDir = Join-Path $OutputDir $bundleName
$tgzPath = Join-Path $OutputDir "$bundleName.tgz"
if (((Test-Path -LiteralPath $bundleDir) -or (Test-Path -LiteralPath $tgzPath)) -and -not $Force) {
  throw "Artifact already exists: $bundleDir (pass -Force to overwrite)"
}

$node = Get-Command node.exe -ErrorAction SilentlyContinue
if ($node) {
  foreach ($module in (Get-ChildItem -LiteralPath (Join-Path $relayDir 'src') -Filter '*.mjs' -File)) {
    & $node.Source --check $module.FullName
    if ($LASTEXITCODE -ne 0) { throw "node --check failed: $($module.Name)" }
  }
  Write-Host "Syntax checks passed." -ForegroundColor Green
  if (-not $SkipTests) {
    $testFiles = @(
      (Join-Path $relayDir 'test\config.test.mjs'),
      (Join-Path $relayDir 'test\downloads.test.mjs'),
      (Join-Path $relayDir 'test\download-page.test.mjs'),
      (Join-Path $relayDir 'test\acl-guard.test.mjs'),
      (Join-Path $relayDir 'test\acl-api.test.mjs'),
      (Join-Path $relayDir 'test\auto-approve.test.mjs')
    ) | Where-Object { Test-Path -LiteralPath $_ }
    if ($testFiles.Count -gt 0) {
      & $node.Source --test --test-isolation=none @testFiles
      if ($LASTEXITCODE -ne 0) { throw 'relay unit tests failed (run: cd relay; npm test)' }
      Write-Host "Unit tests passed." -ForegroundColor Green
    }
  }
}

if (Test-Path -LiteralPath $bundleDir) { Remove-Item -LiteralPath $bundleDir -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $bundleDir 'src') -Force | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $bundleDir 'package.json')
Copy-Item -LiteralPath $patchFile -Destination (Join-Path $bundleDir 'cordis.patch.yml')
$readme = Join-Path $relayDir 'README.zh.md'
if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination (Join-Path $bundleDir 'README.zh.md') }
$bundleReadme = Join-Path $relayDir 'README.md'
if (Test-Path -LiteralPath $bundleReadme) { Copy-Item -LiteralPath $bundleReadme -Destination (Join-Path $bundleDir 'README.md') }
foreach ($module in (Get-ChildItem -LiteralPath (Join-Path $relayDir 'src') -Filter '*.mjs' -File)) {
  Copy-Item -LiteralPath $module.FullName -Destination (Join-Path $bundleDir "src\$($module.Name)")
}
$assetsDir = Join-Path $relayDir 'assets'
if (Test-Path -LiteralPath $assetsDir) { Copy-Item -LiteralPath $assetsDir -Destination (Join-Path $bundleDir 'assets') -Recurse }
$localeDir = Join-Path $relayDir 'locale'
if (Test-Path -LiteralPath $localeDir) { Copy-Item -LiteralPath $localeDir -Destination (Join-Path $bundleDir 'locale') -Recurse }
$iconFile = Join-Path $relayDir 'icon.png'
if (Test-Path -LiteralPath $iconFile) { Copy-Item -LiteralPath $iconFile -Destination (Join-Path $bundleDir 'icon.png') }

$installNotes = @'
# dsh-remote-relay __VERSION__ — DSH bundle

本目录是一个 DSH **bundle**（`package.json` 的 `dsh.bundle.patch` 指向
`cordis.patch.yml`），因此会出现在 DSH「设置 → 插件」页。

## 安装（推荐：宿主 pluginManager/installBundle）

```powershell
pwsh -File scripts/install-relay-bundle.ps1
```

安装脚本会：移除桌面 profile 里旧的 dev insert（备份）→ 通过宿主
``pluginManager/installBundle`` 安装本目录 → 等待安装完成 → 校验
``listBundles`` 与 relay 健康/自检。失败会自动恢复旧 patch。

## 手动安装（DSH GUI / CLI）

- DSH GUI：设置 → 插件 → 安装本地 bundle，选择本目录（绝对路径）。
- 或调用宿主 RPC：``pluginManager/installBundle``，``spec`` 为本目录绝对路径。

## 证书

bundle 不含证书/私钥。默认读取 ``~/.dsh/remote/certs/relay-cert.pem`` /
``relay-key.pem``（可用 relay config ``tls.certFile/keyFile`` 覆盖）。
没有时先运行 ``pwsh -File scripts/gen-tls.ps1``（默认输出到该目录）。

## 中继服务器配置（v0.4.0+）

公网中继服务器（网关）地址不再写死在 DSH profile patch 里，而是由插件持久化保存到：

```
%USERPROFILE%\.dsh\remote\relay-config.json
```

字段：`gateway.enabled`、`gateway.host`（IP/域名）、`gateway.port`、可选的
`gateway.path`（API 路由端点，默认 `/__gateway/tunnel`）、可选的
`gateway.fingerprint`（网关证书 SPKI SHA-256，留空 = 首次信任）。

三种修改方式（保存后立即重连，无需重启 DSH）：

1. **管理页（推荐）**：`https://127.0.0.1:8443/__relay/pair` →「中继服务器设置」
   填 IP/域名 + 端口 + 可选 API 路由端点 →「保存并重连」。
2. **CLI**：`pwsh -File scripts/relay-config.ps1 -Host 203.0.113.10 -Port 9443 [-Path /__gateway/tunnel]`
   （不带参数 = 查看当前配置；`-Disable` 关闭公网中继）。
3. 直接编辑上面的 JSON 文件：保存后插件通过文件监听自动重载。

一次性初始化：插件首次加载时若 `relay-config.json` 不存在、而旧的 profile patch 里还有
`gateway` 配置，会把旧值迁移进 JSON 文件（`initializedBy=one-time-migration:profile-patch`），
`install-relay-bundle.ps1` 随后删除 patch 里的 gateway 段落，让 JSON 成为唯一可编辑来源
（`-NoSeedConfig` 可跳过）。旧 patch 配置仍作为回退保留。
'@
$installNotes = $installNotes.Replace('__VERSION__', $Version)
Set-Content -LiteralPath (Join-Path $bundleDir 'INSTALL.md') -Value $installNotes -Encoding utf8NoBOM

if (-not $NoTgz) {
  if (Test-Path -LiteralPath $tgzPath) { Remove-Item -LiteralPath $tgzPath -Force }
  Push-Location $OutputDir
  try {
    & tar -czf $tgzPath $bundleName
    if ($LASTEXITCODE -ne 0) { throw "tar -czf failed with exit code $LASTEXITCODE" }
  } finally { Pop-Location }
}

$dirSize = [math]::Round(((Get-ChildItem -LiteralPath $bundleDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB), 1)
Write-Host ''
Write-Host 'DSH bundle artifact:' -ForegroundColor Green
Write-Host "  dir : $bundleDir ($dirSize KB)"
if (-not $NoTgz) { Write-Host "  tgz : $tgzPath ($([math]::Round((Get-Item -LiteralPath $tgzPath).Length / 1KB, 1)) KB)" }
Write-Host "  patch: $patchRel (insert id=remote-relay)"
Write-Host "  install: pwsh -File scripts/install-relay-bundle.ps1"

