<#
.SYNOPSIS
  Install/refresh the dsh-remote-relay host plugin from THIS repository into a
  DSH desktop profile (development flow; hot reloads the running desktop DSH).

.DESCRIPTION
  1. Ensures TLS material exists (runs scripts/gen-tls.ps1 when missing).
  2. Creates a junction
       <DSH_HOME>\profiles\<profile>\node_modules\dsh-remote-relay -> <repo>\relay
  3. Adds (or refreshes) one managed insert entry in
       <DSH_HOME>\profiles\<profile>\cordis.patch.yml
     after backing the file up to cordis.patch.yml.bak-<timestamp>.
  4. Bumps a reload-tick comment so the profile patch HMR reload picks it up
     (no process restart needed), then waits until the TLS relay answers.

  Idempotent: re-running only rewrites the managed block and creates a fresh
  backup. For distribution installs (zip -> %DSH_HOME%\plugins\...), use
  scripts/install-relay.ps1 instead.

.PARAMETER BindHost
  LAN/VPN IPv4 the relay binds. Defaults to the default-route interface address.

.PARAMETER Port
  Relay TLS port (default 8443).

.PARAMETER Token
  Optional explicit launch token; omit to let the plugin discover it from
  ctx.connection.authenticatedUrl().

.PARAMETER Profile
  DSH profile to install into (default: desktop).

.PARAMETER DshHome
  Override DSH_HOME (default: $env:DSH_HOME or ~/.dsh). Useful for isolated tests.

.PARAMETER SkipWait
  Do not poll the relay health endpoint after installing.

.PARAMETER RequireReload
  Fail unless the plugin instance is actually restarted (health startedAt
  changes). Use after editing relay/ sources: a plain rerun with unchanged
  config does not restart the plugin, so this switch asks for the stronger
  check.

.PARAMETER TimeoutSeconds
  How long to wait for the hot-reloaded plugin to answer (default 45).
#>
[CmdletBinding()]
param(
  [string]$BindHost,
  [int]$Port = 8443,
  [string]$PublicHost,
  [int]$PublicPort = 0,
  [string]$Token,
  [string]$Name,
  [switch]$GatewayEnabled,
  [string]$GatewayUrl,
  [string]$GatewayName,
  [string]$GatewaySecret,
  [string]$GatewayToken,
  [string]$GatewayFingerprint,
  [string]$Profile = 'desktop',
  [string]$DshHome,
  [switch]$SkipWait,
  [switch]$RequireReload,
  [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$relayDir = Join-Path $repoRoot 'relay'
# Shared TLS material location (also the bundle default): ~/.dsh/remote/certs.
$certDir = Join-Path $env:USERPROFILE '.dsh\remote\certs'
$certFile = Join-Path $certDir 'relay-cert.pem'
$keyFile = Join-Path $certDir 'relay-key.pem'
$repoCertFile = Join-Path $relayDir 'certs\relay-cert.pem'
$repoKeyFile = Join-Path $relayDir 'certs\relay-key.pem'

. (Join-Path $PSScriptRoot 'relay-common.ps1')

$paths = Get-RelayProfilePaths -DshHome $DshHome -Profile $Profile
$patchFile = $paths.PatchFile
$linkDir = Join-Path $paths.NodeModules 'dsh-remote-relay'

if (-not $BindHost) { $BindHost = Resolve-LanIp }
if ($BindHost -in @('0.0.0.0', '::', '*')) { throw "Refusing wildcard bind host '$BindHost'; pass a concrete LAN/VPN IPv4." }

Write-Host "Repo         : $repoRoot"
Write-Host "DSH home     : $($paths.DshHome)  (profile: $Profile)"
Write-Host "Plugin source: $relayDir"
Write-Host "Relay        : https://${BindHost}:${Port}"

if (-not (Test-Path -LiteralPath $patchFile)) { throw "Profile patch file not found: $patchFile" }
if (-not (Test-Path -LiteralPath $paths.NodeModules)) { throw "Profile node_modules not found: $($paths.NodeModules)" }
if (-not (Test-Path -LiteralPath (Join-Path $relayDir 'package.json'))) { throw "Plugin manifest not found: $relayDir\package.json" }

# ---- 0) TLS material --------------------------------------------------------
if (-not (Test-Path -LiteralPath $certFile) -or -not (Test-Path -LiteralPath $keyFile)) {
  if ((Test-Path -LiteralPath $repoCertFile) -and (Test-Path -LiteralPath $repoKeyFile)) {
    New-Item -ItemType Directory -Path $certDir -Force | Out-Null
    Copy-Item -LiteralPath $repoCertFile -Destination $certFile -Force
    Copy-Item -LiteralPath $repoKeyFile -Destination $keyFile -Force
    Write-Host "TLS material copied from relay/certs -> $certDir (fingerprint preserved)" -ForegroundColor Green
  } else {
    Write-Host 'TLS material missing; generating with scripts/gen-tls.ps1 ...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'gen-tls.ps1') -LanIp $BindHost
    if ($LASTEXITCODE -ne 0) { throw 'gen-tls.ps1 failed.' }
  }
}
if (-not (Test-Path -LiteralPath $certFile)) { throw "Certificate not found after generation: $certFile" }

# ---- 1) snapshot current instance (reload detection) ------------------------
$before = Get-RelayHealth -TargetHost $BindHost -TargetPort $Port
$beforeStartedAt = if ($before) { [string]$before.startedAt } else { $null }
if ($beforeStartedAt) {
  Write-Host "Current relay instance startedAt=$beforeStartedAt (waiting for a reloaded instance)"
} else {
  Write-Host 'No relay instance answering yet (first install / reload in progress)'
}

# ---- 2) junction ------------------------------------------------------------
$junction = Set-RelayJunction -LinkPath $linkDir -TargetDir $relayDir
if ($junction.Changed) { Write-Host "Created/replaced junction: $linkDir -> $($junction.Target)" }
else { Write-Host "Junction already correct: $linkDir -> $($junction.Target)" }

# ---- 3) profile patch entry -------------------------------------------------
$patchResult = Set-RelayPatchBlock -PatchFile $patchFile -BindHost $BindHost -Port $Port -CertFile $certFile -KeyFile $keyFile -Token $Token -Name $Name -PublicHost $PublicHost -PublicPort $PublicPort -GatewayEnabled:$GatewayEnabled -GatewayUrl $GatewayUrl -GatewayName $GatewayName -GatewaySecret $GatewaySecret -GatewayToken $GatewayToken -GatewayFingerprint $GatewayFingerprint
Write-Host "Backed up patch file: $($patchResult.Backup)"
Write-Host "Patch entry written to: $patchFile"
Write-Host '--- managed block ---'
(Get-Content -LiteralPath $patchFile | Select-Object -Last 15) | ForEach-Object { Write-Host $_ }
Write-Host '---------------------'
if (($GatewayEnabled -or $GatewayUrl) -and (Test-Path -LiteralPath (Get-RelayConfigPath -DshHome $paths.DshHome))) {
  Write-Warning ('relay-config.json exists and wins over this patch gateway block; ' +
    'change the relay server with: pwsh -File scripts/relay-config.ps1 -Host <IP> -Port <port>')
}

# ---- 4) wait for hot reload -------------------------------------------------
if ($SkipWait) {
  Write-Host 'SkipWait set; not polling the relay.' -ForegroundColor Yellow
  exit 0
}

$processes = @(Get-DesktopHostProcesses)
if ($processes.Count -eq 0) {
  Write-Warning 'No running DeepSeek Harness desktop process detected; the plugin will load on next start.'
  exit 0
}
$pids = @($processes | ForEach-Object { $_.ProcessId })
Write-Host "Desktop host processes: $($pids.Count) (main pid candidates: $(($pids | Select-Object -First 3) -join ', '))"

$wait = Wait-RelayHealth -TargetHost $BindHost -TargetPort $Port -TimeoutSeconds $TimeoutSeconds -PreviousStartedAt $beforeStartedAt
if (-not $wait.Healthy) {
  Write-Warning "Relay did not answer at https://${BindHost}:${Port}/__relay/health within ${TimeoutSeconds}s."
  Write-Host 'Check the desktop log for [remote-relay] lines. Common causes: HMR not triggered, cert path wrong, port in use, engines.dsh mismatch.'
  exit 1
}
$health = $wait.Health
Write-Host "Relay is healthy: https://${BindHost}:${Port}/__relay/health (startedAt=$($health.startedAt))" -ForegroundColor Green
if ($wait.Reloaded) {
  Write-Host 'Hot reload detected: a fresh plugin instance is serving.' -ForegroundColor Green
} else {
  Write-Host 'Note: plugin instance was already running with identical config/module files; it was not restarted.' -ForegroundColor Yellow
  Write-Host '      (After editing relay/ sources, rerun with -RequireReload to assert a restart.)'
  if ($RequireReload) {
    Write-Warning 'RequireReload was set but the plugin instance did not restart.'
    exit 1
  }
}

# The listener answers before the startup self-check finishes; wait for the
# probe verdict so this script does not report a false "self-check failed".
$probeDeadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $probeDeadline -and $health.protocolProbe -and $health.protocolProbe.status -eq 'pending') {
  Start-Sleep -Milliseconds 500
  $refreshed = Get-RelayHealth -TargetHost $BindHost -TargetPort $Port
  if ($refreshed) { $health = $refreshed }
}

if ($health.protocolProbe -and $health.protocolProbe.ok -eq $true) {
  Write-Host 'Self-check: all required Remote capabilities are available.' -ForegroundColor Green
} else {
  Write-Warning 'Self-check did not pass; pairing will be written with active=false. See health protocolProbe / pairing.error.'
}

if (Test-Path -LiteralPath $paths.PairingFile) {
  $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json
  $pairingIsGateway = ($pairing.PSObject.Properties.Name -contains 'mode') -and $pairing.mode -eq 'gateway'
  if ($pairingIsGateway) {
    # v2: top-level address is the gateway; the local LAN listener is in local*.
    $expectedPairHost = if ($pairing.localHost) { [string]$pairing.localHost } else { $BindHost }
    $expectedPairPort = if ($pairing.localPort) { [int]$pairing.localPort } else { $Port }
    $expectedFp = if ($pairing.localFp) { [string]$pairing.localFp } else { $health.fp }
    Write-Host "Pairing file: $($paths.PairingFile) (v2 gateway mode)"
    if ($pairing.host -ne $pairing.gateway.url -and -not $pairing.gateway.url) {
      Write-Warning 'pairing.gateway.url is missing in a gateway-mode pairing'
    }
    if ([string]$pairing.localHost -ne $BindHost -or [int]$pairing.localPort -ne $Port -or $expectedFp -ne $health.fp) {
      Write-Warning "pairing local endpoint ($($pairing.localHost):$($pairing.localPort), fp=$($expectedFp.Substring(0,12))…) does not match the configured LAN relay ($BindHost`:$Port, fp=$($health.fp.Substring(0,12))…)"
    } else {
      Write-Host "Pairing LAN endpoint matches configuration: $($pairing.localHost):$($pairing.localPort) fp=$($expectedFp.Substring(0,16))… active=$($pairing.active)" -ForegroundColor Green
    }
    $gw = $pairing.gateway
    Write-Host "  gateway  : url=$($gw.url) online=$($gw.online) usable=$($gw.usable) fp=$([string]$gw.fp)"
  } else {
    $expectedPairHost = if ($PublicHost) { $PublicHost } else { $BindHost }
    $expectedPairPort = if ($PublicPort -gt 0) { $PublicPort } else { $Port }
    Write-Host "Pairing file: $($paths.PairingFile)"
    if ($pairing.host -ne $expectedPairHost -or [int]$pairing.port -ne $expectedPairPort -or $pairing.fp -ne $health.fp) {
      Write-Warning "pairing.json ($($pairing.host):$($pairing.port), fp=$($pairing.fp.Substring(0,12))…) does not match the configured relay ($expectedPairHost`:$expectedPairPort, fp=$($health.fp.Substring(0,12))…)"
    } else {
      Write-Host "Pairing matches configuration: $($pairing.host):$($pairing.port) fp=$($pairing.fp.Substring(0,16))… active=$($pairing.active)"
    }
  }
  Write-Host "  pairingUri: $($pairing.pairingUri)"
  if ($pairing.localPairingUri) { Write-Host "  localUri  : $($pairing.localPairingUri)" }
  Write-Host "  updatedAt : $($pairing.updatedAt)"
} else {
  Write-Warning "Pairing file not found yet: $($paths.PairingFile)"
}
Write-Host ''
Write-Host 'Next: pwsh -File scripts/relay-smoke.ps1 -IncludeWs'
