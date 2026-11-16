<#
.SYNOPSIS
  Show or change the persisted dsh-remote-relay server configuration.

.DESCRIPTION
  The relay plugin stores the public relay server (gateway) address in
  `<DSH_HOME>\remote\relay-config.json` and exposes a loopback-only API:

    GET  https://127.0.0.1:<RelayPort>/__relay/config   (show)
    POST https://127.0.0.1:<RelayPort>/__relay/config   (save + live apply)

  This script is the CLI "修改方案": it reads the current values, merges the
  parameters you pass, and POSTs the result to the running relay. The relay
  validates the input, persists the JSON document (mode 0600) and reconnects the
  gateway tunnel immediately - no DSH restart / YAML edit required.

  Equivalent UI: https://127.0.0.1:<RelayPort>/__relay/pair -> 中继服务器设置.

.PARAMETER ServerHost
  Relay server IP address or DNS name, e.g. 203.0.113.10. Aliases: -Host, -IP.

.PARAMETER Port
  Relay server TLS port (1-65535), e.g. 9443.

.PARAMETER Path
  Optional API route endpoint used to register the tunnel. Default
  /__gateway/tunnel; a reverse-proxy prefix such as /dsh/__gateway/tunnel works.

.PARAMETER Fingerprint
  Optional gateway certificate SPKI SHA-256 (hex, ":" separators allowed).
  Leave empty to keep the current value; pass -ClearFingerprint to trust on
  first use (TOFU).

.PARAMETER Enable / Disable
  Enable or disable the public gateway transport (LAN direct stays available).

.PARAMETER Json
  Print the raw API JSON instead of a table.

.PARAMETER RelayPort
  Loopback TLS listener port of the relay (default 8443).

.EXAMPLE
  pwsh -File scripts/relay-config.ps1
  Show the effective configuration.

.EXAMPLE
  pwsh -File scripts/relay-config.ps1 -Host 203.0.113.10 -Port 9443
  Point the desktop at a self-hosted relay server and reconnect immediately.

.EXAMPLE
  pwsh -File scripts/relay-config.ps1 -Host relay.example.com -Port 443 -Path /dsh/__gateway/tunnel
  Same, behind a reverse proxy with a path prefix.

.EXAMPLE
  pwsh -File scripts/relay-config.ps1 -Disable
  Turn the public gateway off (phone must use the LAN pairing URI).
#>
[CmdletBinding(DefaultParameterSetName = 'Show')]
param(
  [Parameter(ParameterSetName = 'Set')]
  [Alias('Host', 'IP', 'Address')]
  [string]$ServerHost,
  [Parameter(ParameterSetName = 'Set')]
  [int]$Port,
  [Parameter(ParameterSetName = 'Set')]
  [string]$Path,
  [Parameter(ParameterSetName = 'Set')]
  [string]$Fingerprint,
  [Parameter(ParameterSetName = 'Set')]
  [switch]$ClearFingerprint,
  [Parameter(ParameterSetName = 'Set')]
  [switch]$Enable,
  [Parameter(ParameterSetName = 'Set')]
  [switch]$Disable,
  [switch]$Json,
  [int]$RelayPort = 8443
)

$ErrorActionPreference = 'Stop'

function Get-RelayConfig {
  $raw = & curl.exe -k -s -m 10 "https://127.0.0.1:$RelayPort/__relay/config" 2>$null
  $text = ($raw -join "`n").Trim()
  if (-not $text) {
    throw "relay config API did not answer on https://127.0.0.1:$RelayPort/__relay/config. Is the desktop DSH running with the relay plugin?"
  }
  try { return ($text | ConvertFrom-Json) } catch { throw "relay config API returned invalid JSON: $text" }
}

function Set-RelayConfig {
  param([hashtable]$Gateway)
  $payload = @{ gateway = $Gateway } | ConvertTo-Json -Depth 8 -Compress
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-config-" + [guid]::NewGuid().ToString('N') + '.json')
  Set-Content -LiteralPath $tmp -Value $payload -Encoding utf8NoBOM
  try {
    $output = & curl.exe -k -s -m 30 -X POST -H 'content-type: application/json' --data-binary "@$tmp" "https://127.0.0.1:$RelayPort/__relay/config" 2>$null
  } finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  }
  $text = ($output -join "`n").Trim()
  if (-not $text) { throw 'relay config API returned no body while saving' }
  $result = $text | ConvertFrom-Json
  if (-not $result.ok) { throw "relay rejected the configuration: $($result.error)" }
  return $result
}

function Show-RelayConfig {
  param($Config)
  if ($Json) { $Config | ConvertTo-Json -Depth 10; return }
  $gw = $Config.gateway
  $rows = [ordered]@{
    '配置文件'    = $Config.file
    '配置来源'    = $Config.source
    '已初始化'    = if ($Config.initializedBy) { "$($Config.initializedBy) @ $($Config.initializedAt)" } else { '-' }
    '最后保存'    = if ($Config.updatedAt) { $Config.updatedAt } else { '-' }
    '启用公网中继' = if ($gw.enabled) { 'true' } else { 'false' }
    '服务器地址'  = if ($gw.host) { "$($gw.host):$($gw.port)" } else { '-' }
    'API 路由端点' = if ($gw.path) { $gw.path } else { '-' }
    '传输协议'    = if ($gw.secure -eq $false) { 'ws' } else { 'wss' }
    '证书指纹'    = if ($gw.fingerprint) { $gw.fingerprint } else { '未固定（首次信任）' }
    '隧道状态'    = if (-not $gw.enabled) { '未启用' } elseif ($gw.online) { '在线' } else { '离线' }
    '最近错误'    = if ($gw.error) { $gw.error } else { '-' }
  }
  $rows.GetEnumerator() | ForEach-Object { '{0,-12} {1}' -f $_.Key, $_.Value }
  Write-Host ''
  Write-Host '修改方式：' -ForegroundColor DarkGray
  Write-Host '  pwsh -File scripts/relay-config.ps1 -Host <IP/域名> -Port <端口> [-Path /__gateway/tunnel]' -ForegroundColor DarkGray
  Write-Host '  或打开 https://127.0.0.1:8443/__relay/pair 使用「中继服务器设置」表单。' -ForegroundColor DarkGray
}

$hasChange = $PSBoundParameters.ContainsKey('ServerHost') -or $PSBoundParameters.ContainsKey('Port') -or
  $PSBoundParameters.ContainsKey('Path') -or $PSBoundParameters.ContainsKey('Fingerprint') -or
  $ClearFingerprint -or $Enable -or $Disable

if (-not $hasChange) {
  Show-RelayConfig (Get-RelayConfig)
  exit 0
}

$current = Get-RelayConfig
$gw = $current.gateway
$next = @{
  enabled     = if ($Enable) { $true } elseif ($Disable) { $false } elseif ($null -ne $gw.enabled) { [bool]$gw.enabled } else { $true }
  host        = if ($PSBoundParameters.ContainsKey('ServerHost')) { $ServerHost } else { [string]$gw.host }
  port        = if ($PSBoundParameters.ContainsKey('Port')) { $Port } else { [int]$gw.port }
  path        = if ($PSBoundParameters.ContainsKey('Path')) { $Path } else { [string]$gw.path }
  secure      = if ($null -ne $gw.secure) { [bool]$gw.secure } else { $true }
  fingerprint = if ($ClearFingerprint) { $null } elseif ($PSBoundParameters.ContainsKey('Fingerprint')) { $Fingerprint } else { $gw.fingerprint }
  name        = if ($gw.name) { [string]$gw.name } else { 'DSH Remote Gateway' }
}

# A first-time setup with -Host implies enabling the gateway.
if (-not $Enable -and -not $Disable -and $PSBoundParameters.ContainsKey('ServerHost') -and -not $next.enabled) {
  $next.enabled = $true
}

$result = Set-RelayConfig -Gateway $next
Show-RelayConfig $result
Write-Host ''
Write-Host "已保存并即时应用（$($result.file)）；手机重新加载配对信息即可使用新地址。" -ForegroundColor Green
exit 0
