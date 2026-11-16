<#
.SYNOPSIS
  Show the LAN URL where the phone can download the latest release APK.

.DESCRIPTION
  Reads the pairing document for the relay host/port, checks the running relay,
  then prints the public download page URL plus the latest APK's version, size
  and sha256 (from /__relay/health, which rescans the APK directory live).

  The page itself is served by the relay at /__relay/download and only exposes
  whitelisted `*.apk` files from the configured directory (default
  <repo>/dist/apk). Use -Json for automation, -Open to open the page locally.

.PARAMETER Host
  Override the LAN/VPN host shown in the URL (default: pairing.json host).

.PARAMETER Port
  Override the relay port (default: pairing.json port, else 8443).

.PARAMETER Json
  Print one JSON object instead of the human-readable summary.

.PARAMETER Open
  Open the download page in the default browser on this machine.

.PARAMETER DshHome / Profile
  Override DSH_HOME / profile used to locate pairing.json.

.EXAMPLE
  pwsh -File scripts/show-download.ps1

.EXAMPLE
  pwsh -File scripts/show-download.ps1 -Json
#>
[CmdletBinding()]
param(
  [Alias('Host')]
  [string]$BindHost,
  [int]$Port = 0,
  [switch]$Json,
  [switch]$Open,
  [string]$DshHome,
  [string]$Profile = 'desktop'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'relay-common.ps1')

$paths = Get-RelayProfilePaths -DshHome $DshHome -Profile $Profile
$pairing = $null
if (Test-Path -LiteralPath $paths.PairingFile) {
  try { $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json } catch {}
}

if (-not $BindHost) {
  if ($pairing -and $pairing.host) { $BindHost = [string]$pairing.host } else { throw 'No pairing.json host found; pass -Host <LAN IP>.' }
}
if ($Port -le 0) {
  if ($pairing -and $pairing.port) { $Port = [int]$pairing.port } else { $Port = 8443 }
}

# Health is authoritative and rescans the download directory; fall back to the
# pairing snapshot only if the relay is not answering.
$health = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort $Port
$download = if ($health -and $health.download) { $health.download } elseif ($pairing -and $pairing.download) { $pairing.download } else { $null }

$url = "https://${BindHost}:${Port}/__relay/download"
$latest = if ($download) { $download.latest } else { $null }
$sizeMiB = if ($latest -and $latest.sizeBytes) { [math]::Round([double]$latest.sizeBytes / 1MB, 2) } else { $null }

$result = [pscustomobject]@{
  url          = $url
  host         = $BindHost
  port         = $Port
  pagePath     = '/__relay/download'
  relayHealthy = [bool]$health
  relayVersion = if ($health) { $health.relayVersion } elseif ($pairing) { $pairing.relayVersion } else { $null }
  dshVersion   = if ($health) { $health.dshVersion } elseif ($pairing) { $pairing.dshVersion } else { $null }
  dir          = if ($download) { $download.dir } else { $null }
  count        = if ($download) { $download.count } else { 0 }
  latest       = $latest
  sizeMiB      = $sizeMiB
}

if ($Json) {
  $result | ConvertTo-Json -Depth 6
  exit 0
}

Write-Host 'DSH Remote — APK download' -ForegroundColor Cyan
Write-Host "  URL      : $url"
if ($health) {
  Write-Host "  Relay    : healthy (relay $($health.relayVersion), DSH $($health.dshVersion), self-check $($health.protocolProbe.status))" -ForegroundColor Green
} else {
  Write-Warning "Relay is not answering on 127.0.0.1:$Port; showing the last pairing snapshot. Run scripts/dev-install.ps1 if needed."
}
if ($download) {
  Write-Host "  APK dir  : $($download.dir)  ($($download.count) file(s))"
} else {
  Write-Warning 'No download info available yet (relay may need a reload).'
}
if ($latest) {
  Write-Host "  Latest   : v$($latest.version) ($($latest.versionCode))  $($latest.file)"
  Write-Host "             $sizeMiB MiB  sha256=$($latest.sha256)"
  Write-Host "             updated $($latest.updatedAt)"
  Write-Host "  Direct   : $url/$($latest.file)"
} else {
  Write-Warning 'No APK found in the download directory yet.'
  Write-Host '             Build one: pwsh -File scripts/build-release-apk.ps1'
}
Write-Host ''
Write-Host '手机下载步骤：'
Write-Host '  1) 手机与电脑同一 Wi-Fi / VPN，浏览器打开上面的 URL；'
Write-Host '  2) 自签证书提示不安全 → 「高级 → 继续前往」；'
Write-Host '  3) 点「下载最新版 APK」→ 下载完成后点通知栏安装；'
Write-Host '     若被「安装增强防护」拦截：更多 → 开始深度扫描 → 继续安装；'
Write-Host '  4) 安装后在电脑跑 scripts/show-pairing.ps1 扫码配对（配对页仅本机可访问）。'

if ($Open) {
  Write-Host ''
  Write-Host 'Opening the download page in the default browser ...'
  Start-Process $url
}
exit 0

