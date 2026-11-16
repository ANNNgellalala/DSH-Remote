<#
.SYNOPSIS
  Open the local QR pairing page for DSH Remote.

.DESCRIPTION
  The relay serves a loopback-only pairing page at
  https://127.0.0.1:<port>/__relay/pair that renders the current pairingUri
  (host/port/token/fp) as a QR code. Scanning it with the phone's system camera
  opens the dshremote:// deep link, which launches the app (intent-filter
  already implemented on the Android head).

  This script verifies that the relay is actually answering (and optionally
  installs it first with -AutoInstall), prints a short pairing summary (token
  masked), then opens the page in the default browser.

  The page is refused to non-loopback clients by the relay, carries
  Content-Security-Policy + no-store, and the token is never written to a log.

.PARAMETER Port
  Relay port. Default: the port from pairing.json, else 8443.

.PARAMETER NoBrowser
  Print the URL instead of opening a browser.

.PARAMETER AutoInstall
  If the relay is not answering, run scripts/dev-install.ps1 once, then retry.

.PARAMETER BindHost
  LAN/VPN address used only when -AutoInstall needs to install/repair the relay.

.PARAMETER DshHome / Profile
  Override DSH_HOME / profile (pairing path and install target).

.PARAMETER TimeoutSeconds
  How long to wait for relay health (default 25).

.EXAMPLE
  pwsh -File scripts/show-pairing.ps1

.EXAMPLE
  pwsh -File scripts/show-pairing.ps1 -NoBrowser -Port 8443
#>
[CmdletBinding()]
param(
  [int]$Port = 0,
  [switch]$NoBrowser,
  [switch]$AutoInstall,
  [string]$BindHost,
  [string]$DshHome,
  [string]$Profile = 'desktop',
  [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'relay-common.ps1')

$paths = Get-RelayProfilePaths -DshHome $DshHome -Profile $Profile
$pairing = $null
if (Test-Path -LiteralPath $paths.PairingFile) {
  try { $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json } catch {}
}
if ($Port -le 0) {
  if ($pairing -and $pairing.port) { $Port = [int]$pairing.port } else { $Port = 8443 }
}

function Mask-Secret {
  param([string]$Value)
  if (-not $Value) { return '<none>' }
  if ($Value.Length -le 8) { return '****' }
  return $Value.Substring(0, 4) + '…' + $Value.Substring($Value.Length - 4)
}

Write-Host "DSH home : $($paths.DshHome)  (profile: $Profile)"
Write-Host "Relay    : https://127.0.0.1:$Port"

$health = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort $Port
if (-not $health -and $AutoInstall) {
  Write-Host 'Relay is not answering; installing/repairing it with scripts/dev-install.ps1 ...' -ForegroundColor Yellow
  $installArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'dev-install.ps1'), '-Port', "$Port", '-Profile', $Profile)
  if ($BindHost) { $installArgs += @('-BindHost', $BindHost) }
  if ($DshHome) { $installArgs += @('-DshHome', $DshHome) }
  $pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
  if (-not $pwshCommand) { $pwshCommand = Get-Command pwsh -ErrorAction SilentlyContinue }
  $pwshExe = if ($pwshCommand) { $pwshCommand.Source } else { 'pwsh' }
  & $pwshExe @installArgs
  $health = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort $Port
}
if (-not $health) {
  Write-Host ''
  Write-Error ("Relay is not answering on https://127.0.0.1:$Port/__relay/health.`n" +
    "Start or repair it first: pwsh -File scripts/dev-install.ps1`n" +
    "Or let this script do it: pwsh -File scripts/show-pairing.ps1 -AutoInstall") -ErrorAction Stop
  exit 1
}

Write-Host "Relay    : healthy (relay $($health.relayVersion), DSH $($health.dshVersion), self-check $($health.protocolProbe.status))" -ForegroundColor Green
if ($pairing) {
  Write-Host "Pairing  : active=$($pairing.active)  host:$($pairing.host):$($pairing.port)  fp=$($pairing.fp.Substring(0, 16))…"
  Write-Host "           updatedAt=$($pairing.updatedAt)  token=$(Mask-Secret $pairing.token)"
  if ($pairing.active -ne $true) {
    $reason = if ($pairing.error -and $pairing.error.message) { $pairing.error.message } else { 'pairing is inactive' }
    Write-Warning "Pairing is not active: $reason"
    Write-Host '           The page will show the failure instead of a QR code; fix the self-check first.'
  }
} else {
  Write-Warning "pairing.json not found ($($paths.PairingFile)); the page may report pairing-not-ready."
}

$url = "https://127.0.0.1:$Port/__relay/pair"
Write-Host ''
Write-Host 'QR pairing page:' -ForegroundColor Cyan
Write-Host "  $url"
Write-Host ''
Write-Host '用法：'
Write-Host '  1) 手机与电脑同一 Wi-Fi / VPN；'
Write-Host '  2) 用手机「系统相机 / 扫码」扫描页面上的二维码；'
Write-Host '  3) 系统会以 dshremote://pair?... 深链拉起 DSH Remote App（App 端已注册 intent-filter）。'
Write-Host '  4) 若 App 提示认证失败：刷新本页重新扫码；仍失败先跑 scripts/relay-smoke.ps1 -IncludeWs。'
Write-Host ''
Write-Host '注意：证书是自签的，浏览器会提示不安全 → 选择「高级 → 继续访问 127.0.0.1」即可；'
Write-Host '      该页面只对 loopback 开放，非本机访问返回 404；二维码/URI 含 token，等同密码，勿外传。'

if ($NoBrowser) {
  Write-Host ''
  Write-Host '(-NoBrowser) 请手动在浏览器打开上面的 URL。'
  exit 0
}

Write-Host ''
Write-Host 'Opening the default browser ...'
Start-Process $url
exit 0

