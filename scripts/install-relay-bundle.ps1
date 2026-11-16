<#
.SYNOPSIS
  Install the dsh-remote-relay **bundle** into the running desktop DSH (M6).

.DESCRIPTION
  DSH's plugin page only lists bundles, so the relay now ships as a DSH bundle
  (`package.json.dsh.bundle.patch` -> cordis.patch.yml inserting row id
  `remote-relay`). This script migrates an existing dev install
  (junction + hand-written insert) to the bundle form:

    1. read pairing.json for the loopback DSH port + launchToken
    2. exchange the launchToken for an API cookie on 127.0.0.1:<port>
    3. remove the managed dev insert block from the profile patch (backup kept)
    4. remove the dev junction if it still points at the repo relay/
    5. build the bundle artifact (scripts/package-relay-plugin.ps1)
    6. call the host RPC pluginManager/installBundle with the absolute bundle dir,
       then waitForInstall/listBundles until it appears
    7. optionally write the managed config override (name/gateway/...)
    8. wait for relay health + self-check; on any failure restore the old patch
       (and remove the bundle) unless -NoRollback

  The desktop profile is managed by Electron, so this never edits the profile
  package.json by hand and never runs `dsh plugin` directly: only the host's
  own pluginManager RPC is used.

.PARAMETER BundleDir
  Use an already staged bundle directory instead of building one.

.PARAMETER NoBuild
  Do not rebuild; use -BundleDir or the newest dist/dsh-remote-relay-* directory.

.PARAMETER Name
  Desktop display name for the managed config override.

.PARAMETER GatewayHost / GatewayPort / GatewayPath / GatewayFingerprint
  Optional relay-server settings written through the loopback config API after
  install (persisted to <DSH_HOME>/remote/relay-config.json + applied live).
  If omitted, the plugin keeps / migrates the existing configuration.

.PARAMETER GatewayEnable / GatewayDisable
  Force the public gateway transport on or off (default: keep current value).

.PARAMETER NoSeedConfig
  Skip the one-time migration. By default, when relay-config.json does not exist
  yet, the plugin seeds it from the legacy profile patch (so an existing VPS
  address keeps working without retyping) and this script then removes the
  gateway block from the patch so the JSON document is authoritative.

.PARAMETER ForceReinstall
  Remove and reinstall the bundle even when the version is unchanged (development
  reloads). Normally only a version change triggers the remove+install path.

.PARAMETER Profile / DshHome
  DSH profile (default desktop) / DSH_HOME override.

.PARAMETER NoRollback
  Do not restore the previous dev patch when installation fails.

.PARAMETER TimeoutSeconds
  How long to wait for install + health (default 180).
#>
[CmdletBinding()]
param(
  [string]$BundleDir,
  [switch]$NoBuild,
  [string]$Name,
  [switch]$GatewayEnabled,
  [string]$GatewayUrl,
  [string]$GatewayName,
  [string]$GatewaySecret,
  [string]$GatewayToken,
  [string]$GatewayFingerprint,
  [string]$GatewayHost,
  [int]$GatewayPort,
  [string]$GatewayPath,
  [switch]$GatewayEnable,
  [switch]$GatewayDisable,
  [switch]$NoSeedConfig,
  [switch]$ForceReinstall,
  [string]$Profile = 'desktop',
  [string]$DshHome,
  [switch]$NoRollback,
  [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
# Native output (curl.exe) is decoded with the console encoding when captured;
# under a redirected stdout that is the ANSI/OEM codepage on many hosts, which
# turns the host RPC's UTF-8 JSON (localized plugin titles) into invalid text.
# Decode as UTF-8 so ConvertFrom-Json always sees the original bytes.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'relay-common.ps1')

$paths = Get-RelayProfilePaths -DshHome $DshHome -Profile $Profile
$patchFile = $paths.PatchFile
$linkDir = Join-Path $paths.NodeModules 'dsh-remote-relay'
$certDir = Join-Path $env:USERPROFILE '.dsh\remote\certs'
$certFile = Join-Path $certDir 'relay-cert.pem'
$keyFile = Join-Path $certDir 'relay-key.pem'
$repoCert = Join-Path $repoRoot 'relay\certs\relay-cert.pem'
$repoKey = Join-Path $repoRoot 'relay\certs\relay-key.pem'

Write-Host "DSH home : $($paths.DshHome)  (profile: $Profile)"
Write-Host "Patch    : $patchFile"
$configFile = Get-RelayConfigPath -DshHome $paths.DshHome
Write-Host "Config   : $configFile"

function Get-LoopbackHostInfo {
  if (-not (Test-Path -LiteralPath $paths.PairingFile)) { throw "pairing.json not found: $($paths.PairingFile) (is the relay running?)" }
  $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json
  $upstreamPort = [int]$pairing.upstreamPort
  if ($upstreamPort -lt 1) { throw 'pairing.json has no upstreamPort; cannot reach the loopback DSH' }
  $token = $null
  if ($pairing.localPairingUri) {
    try { $token = ([uri]$pairing.localPairingUri).Query.TrimStart('?').Split('&') | ForEach-Object { if ($_ -like 'token=*') { [uri]::UnescapeDataString($_.Substring(6)) } } | Select-Object -First 1 } catch {}
  }
  if (-not $token -and $pairing.schemaVersion -ne 2) { $token = [string]$pairing.token }
  if (-not $token) { throw 'cannot determine the DSH launchToken from pairing.json (localPairingUri)' }
  [pscustomobject]@{ Pairing = $pairing; UpstreamPort = $upstreamPort; LaunchToken = $token }
}

function Get-ApiCookieJar {
  param([int]$UpstreamPort, [string]$LaunchToken)
  $jar = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-bundle-cookies-" + [guid]::NewGuid().ToString('N') + '.txt')
  $code = & curl.exe -s -o NUL -c $jar -w '%{http_code}' "http://127.0.0.1:$UpstreamPort/connect?token=$([uri]::EscapeDataString($LaunchToken))&interactions=none"
  if ("$code" -ne '302') { throw "loopback /connect failed (HTTP $code); is the desktop DSH still running?" }
  return $jar
}

function Invoke-HostRpc {
  param([int]$UpstreamPort, [string]$CookieJar, [string]$Method, [hashtable]$Arguments = @{}, [int]$TimeoutSec = 90)
  $body = @{
    type    = 'client-request'
    rpcId   = [guid]::NewGuid().ToString()
    method  = $Method
    payload = @{ args = $Arguments }
  } | ConvertTo-Json -Depth 30 -Compress
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-rpc-" + [guid]::NewGuid().ToString('N') + '.json')
  $resp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-rpc-resp-" + [guid]::NewGuid().ToString('N') + '.json')
  Set-Content -LiteralPath $tmp -Value $body -Encoding utf8NoBOM
  try {
    # Write the response to a file and decode it explicitly as UTF-8 instead of
    # capturing curl's stdout: the payload contains localized (non-ASCII) plugin
    # titles, and console-codepage decoding can corrupt the JSON.
    & curl.exe -s -m $TimeoutSec -b $CookieJar -H 'content-type: application/json' --data-binary "@$tmp" -o $resp "http://127.0.0.1:$UpstreamPort/api/$Method"
    $raw = if (Test-Path -LiteralPath $resp) { [System.IO.File]::ReadAllText($resp, [System.Text.Encoding]::UTF8) } else { '' }
  } finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $resp -Force -ErrorAction SilentlyContinue
  }
  if ([string]::IsNullOrWhiteSpace($raw)) { throw "RPC $Method returned no body" }
  $json = $raw | ConvertFrom-Json
  if (-not $json.result) { throw "RPC $Method returned an unexpected envelope: $($raw.Substring(0, [Math]::Min(300, $raw.Length)))" }
  if ($json.result.ok -ne $true) {
    $err = $json.result.error
    throw "RPC $Method failed: $($err.code) — $($err.message)"
  }
  return $json.result.value
}

<#
  Persisted relay-server config (relay v0.4.0+). Read through the loopback API and
  written through it too, so the plugin owns validation/persistence.
#>
function Get-LoopbackRelayConfig {
  param([int]$RelayPort = 8443, [int]$TimeoutSeconds = 5)
  $resp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-cfg-" + [guid]::NewGuid().ToString('N') + '.json')
  try {
    & curl.exe -k -s -m $TimeoutSeconds -o $resp "https://127.0.0.1:$RelayPort/__relay/config" 2>$null
    $text = if (Test-Path -LiteralPath $resp) { [System.IO.File]::ReadAllText($resp, [System.Text.Encoding]::UTF8).Trim() } else { '' }
  } finally {
    Remove-Item -LiteralPath $resp -Force -ErrorAction SilentlyContinue
  }
  if (-not $text) { return $null }
  try { return ($text | ConvertFrom-Json) } catch { return $null }
}

function Save-LoopbackRelayConfig {
  param([hashtable]$Gateway, [string]$InitializedBy, [int]$RelayPort = 8443)
  $payload = @{ gateway = $Gateway }
  if ($InitializedBy) { $payload.initializedBy = $InitializedBy }
  $payload = $payload | ConvertTo-Json -Depth 8 -Compress
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-config-" + [guid]::NewGuid().ToString('N') + '.json')
  $resp = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-config-resp-" + [guid]::NewGuid().ToString('N') + '.json')
  Set-Content -LiteralPath $tmp -Value $payload -Encoding utf8NoBOM
  try {
    & curl.exe -k -s -m 60 -X POST -H 'content-type: application/json' --data-binary "@$tmp" -o $resp "https://127.0.0.1:$RelayPort/__relay/config" 2>$null
    $text = if (Test-Path -LiteralPath $resp) { [System.IO.File]::ReadAllText($resp, [System.Text.Encoding]::UTF8).Trim() } else { '' }
  } finally {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $resp -Force -ErrorAction SilentlyContinue
  }
  if (-not $text) { throw 'relay config API returned no body (is the new plugin version loaded?)' }
  $result = $text | ConvertFrom-Json
  if (-not $result.ok) { throw "relay config API rejected the settings: $($result.error)" }
  return $result
}

<# True when the managed relay block in the profile patch still carries a gateway section. #>
function Test-RelayPatchGatewayBlock {
  param([string]$PatchFile)
  if (-not (Test-Path -LiteralPath $PatchFile)) { return $false }
  $inBlock = $false
  foreach ($line in ([System.IO.File]::ReadAllText($PatchFile) -split "`r?`n")) {
    if (-not $inBlock -and $line -match $script:RelayBlockStartPattern) { $inBlock = $true; continue }
    if ($inBlock -and $line -match $script:RelayBlockEndPattern) { return $false }
    if ($inBlock -and $line -match '^\s+gateway:\s*$') { return $true }
  }
  return $false
}

<# Parse a legacy gateway URL into host/port/path parts for the config API. #>
function ConvertFrom-GatewayUrl {
  param([string]$Url)
  $uri = [uri]$Url
  $secure = $uri.Scheme -ne 'ws'
  [pscustomobject]@{
    Host   = $uri.Host
    Port   = if ($uri.IsDefaultPort) { if ($secure) { 443 } else { 80 } } else { $uri.Port }
    Path   = if ($uri.AbsolutePath) { $uri.AbsolutePath } else { '/__gateway/tunnel' }
    Secure = $secure
  }
}

<#
  Read the relay-server settings out of the managed profile patch block before
  it is removed, so the one-time migration can fill relay-config.json with the
  machine's existing (e.g. VPS) address instead of starting empty.
#>
function Get-RelayPatchGatewaySettings {
  param([string]$PatchFile)
  if (-not (Test-Path -LiteralPath $PatchFile)) { return $null }
  $inBlock = $false
  $inGateway = $false
  $values = @{}
  foreach ($line in ([System.IO.File]::ReadAllText($PatchFile) -split "`r?`n")) {
    if (-not $inBlock -and $line -match $script:RelayBlockStartPattern) { $inBlock = $true; continue }
    if ($inBlock -and $line -match $script:RelayBlockEndPattern) { break }
    if (-not $inBlock) { continue }
    if ($line -match '^\s{2,}gateway:\s*$') { $inGateway = $true; continue }
    if (-not $inGateway) { continue }
    if ($line -match '^\s{1,3}[-\w]+:') { $inGateway = $false; continue }
    if ($line -match '^\s+(enabled|url|host|port|path|secure|name|fingerprint|secret|token|identityFile):\s*(.*)$') {
      $values[$Matches[1]] = $Matches[2].Trim().Trim("'").Trim('"')
    }
  }
  if (-not $values.ContainsKey('enabled') -or $values['enabled'] -notmatch '^(?i:true)$') { return $null }
  $gateway = @{
    enabled     = $true
    fingerprint = if ($values['fingerprint']) { $values['fingerprint'] } else { $null }
    name        = if ($values['name']) { $values['name'] } else { 'DSH Remote Gateway' }
    secret      = if ($values['secret']) { $values['secret'] } else { $null }
  }
  if ($values['url']) {
    $parts = ConvertFrom-GatewayUrl -Url $values['url']
    $gateway.host = $parts.Host
    $gateway.port = $parts.Port
    $gateway.path = $parts.Path
    $gateway.secure = $parts.Secure
  } else {
    $gateway.host = [string]$values['host']
    $gateway.port = if ($values['port']) { [int]$values['port'] } else { 9443 }
    $gateway.path = if ($values['path']) { $values['path'] } else { '/__gateway/tunnel' }
    $gateway.secure = if ($values['secure'] -match '^(?i:false)$') { $false } else { $true }
  }
  if (-not $gateway.host) { return $null }
  return $gateway
}

# ---- 0) TLS material for the bundle defaults --------------------------------
if (-not (Test-Path -LiteralPath $certFile) -or -not (Test-Path -LiteralPath $keyFile)) {
  if ((Test-Path -LiteralPath $repoCert) -and (Test-Path -LiteralPath $repoKey)) {
    New-Item -ItemType Directory -Path $certDir -Force | Out-Null
    Copy-Item -LiteralPath $repoCert -Destination $certFile -Force
    Copy-Item -LiteralPath $repoKey -Destination $keyFile -Force
    Write-Host "TLS      : copied relay/certs -> $certDir (fingerprint preserved)" -ForegroundColor Green
  } else {
    throw "TLS material missing: $certDir. Run pwsh -File scripts/gen-tls.ps1 first."
  }
}

# ---- 1) stage the bundle ----------------------------------------------------
if (-not $BundleDir) {
  if ($NoBuild) {
    $candidate = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') -Directory -Filter 'dsh-remote-relay-*' -ErrorAction SilentlyContinue |
      Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw 'No dist/dsh-remote-relay-* bundle found; run without -NoBuild' }
    $BundleDir = $candidate.FullName
  } else {
    & (Join-Path $PSScriptRoot 'package-relay-plugin.ps1') -Force
    if ($LASTEXITCODE -ne 0) { throw 'package-relay-plugin.ps1 failed' }
    $candidate = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') -Directory -Filter 'dsh-remote-relay-*' |
      Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $BundleDir = $candidate.FullName
  }
}
$BundleDir = (Resolve-Path -LiteralPath $BundleDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $BundleDir 'cordis.patch.yml'))) {
  throw "Not a DSH bundle (cordis.patch.yml missing): $BundleDir"
}
Write-Host "Bundle   : $BundleDir"

# ---- 2) capture state for rollback -----------------------------------------
$loopback = Get-LoopbackHostInfo
$cookieJar = Get-ApiCookieJar -UpstreamPort $loopback.UpstreamPort -LaunchToken $loopback.LaunchToken
Write-Host "DSH RPC  : 127.0.0.1:$($loopback.UpstreamPort) (launchToken $($loopback.LaunchToken.Substring(0, 4))…)"
$beforePatch = if (Test-Path -LiteralPath $patchFile) { [System.IO.File]::ReadAllText($patchFile) } else { $null }
$bundleManifest = Get-Content -LiteralPath (Join-Path $BundleDir 'package.json') -Raw | ConvertFrom-Json
$bundleName = $bundleManifest.name
$bundleVersion = $bundleManifest.version

# Capture the currently configured relay server BEFORE the managed patch block is
# removed: this install then performs the one-time initialization (the plugin
# persists it into relay-config.json through its own config API).
$preHealth = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort 8443
$previousName = if ($preHealth -and $preHealth.name) { [string]$preHealth.name } else { $null }
$legacyGateway = Get-RelayPatchGatewaySettings -PatchFile $patchFile
if (-not $legacyGateway) {
  if ($preHealth -and $preHealth.gateway -and $preHealth.gateway.url) {
    $parts = ConvertFrom-GatewayUrl -Url $preHealth.gateway.url
    $legacyGateway = @{
      enabled     = $true
      host        = $parts.Host
      port        = $parts.Port
      path        = $parts.Path
      secure      = $parts.Secure
      fingerprint = $preHealth.gateway.fp
      name        = $preHealth.gateway.name
    }
  }
}
if ($legacyGateway) {
  Write-Host "Config   : pre-install relay server captured for one-time init -> $($legacyGateway.host):$($legacyGateway.port)$($legacyGateway.path)"
}

# ---- 3) migrate: drop the hand-written dev insert + junction ----------------
if (Remove-RelayPatchBlock -PatchFile $patchFile) {
  Write-Host 'Patch    : removed managed dev insert block (backup created)' -ForegroundColor Green
} else {
  Write-Host 'Patch    : no managed dev insert block present'
}
if (Test-Path -LiteralPath $linkDir) {
  $item = Get-Item -LiteralPath $linkDir -Force
  if ($item.LinkType -eq 'Junction' -or $item.LinkType -eq 'SymbolicLink') {
    $target = @($item.LinkTarget)[0]
    $repoRelay = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'relay')).Path
    if ($target -and ([System.IO.Path]::GetFullPath($target).TrimEnd('\') -ieq $repoRelay.TrimEnd('\'))) {
      Remove-Item -LiteralPath $linkDir -Force
      Write-Host 'Junction : removed dev junction (points at repo relay)' -ForegroundColor Green
    } else {
      Write-Host "Junction : left as-is ($target) — installBundle will manage the package"
    }
  }
}

# ---- 3b) replace a previously installed bundle of another version ----------
# pluginManager/installBundle reports `already-installed` when the package name is
# already in the profile, so an upgrade must remove the old bundle first.
try {
  $existingBundles = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/listBundles' -Arguments @{} -TimeoutSec 30
  $existingBundle = @($existingBundles) | Where-Object { $_.name -eq $bundleName } | Select-Object -First 1
  if ($existingBundle -and ($ForceReinstall -or [string]$existingBundle.version -ne [string]$bundleVersion)) {
    Write-Host "RPC      : removing previously installed $bundleName $($existingBundle.version) before installing $bundleVersion"
    $null = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/removeBundle' -Arguments @{ name = $bundleName } -TimeoutSec 120
    $removeDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $removeDeadline) {
      Start-Sleep -Milliseconds 1200
      try {
        $bundlesNow = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/listBundles' -Arguments @{} -TimeoutSec 30
        if (-not (@($bundlesNow) | Where-Object { $_.name -eq $bundleName })) { break }
      } catch {}
    }
    if (@($bundlesNow) | Where-Object { $_.name -eq $bundleName }) {
      # A lingering entry is not fatal: installBundle may treat it as an update.
      Write-Warning "old bundle $bundleName still listed after removeBundle; continuing with install"
    } else {
      Write-Host "RPC      : old bundle removed" -ForegroundColor Green
    }
  }
} catch {
  Write-Warning "pre-install bundle cleanup failed: $($_.Exception.Message)"
}

# ---- 4) install via the host pluginManager RPC -----------------------------
$requestId = 'install-' + [guid]::NewGuid().ToString('N')
$installed = $false
try {
  $installResult = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/installBundle' -Arguments @{
    spec    = $BundleDir
    options = @{ requestId = $requestId; enabled = $true }
  } -TimeoutSec 180
  Write-Host "RPC      : installBundle accepted (requestId=$requestId)"
  if ($installResult) { $installResult | ConvertTo-Json -Depth 6 -Compress | ForEach-Object { Write-Host "           $($_.Substring(0, [Math]::Min(400, $_.Length)))" } }

  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $bundleVisible = $false
  $installState = $null
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1500
    try {
      $installState = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/waitForInstall' -Arguments @{ requestId = $requestId } -TimeoutSec 30
    } catch {
      $installState = $null
    }
    try {
      $bundles = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/listBundles' -Arguments @{} -TimeoutSec 30
      $bundleVisible = @($bundles) | Where-Object { $_.name -eq $bundleName -and [string]$_.version -eq [string]$bundleVersion } | Select-Object -First 1
    } catch {}
    if ($bundleVisible) { break }
    if ($installState -and ($installState.state -eq 'failed' -or $installState.step -eq 'failed')) {
      throw "installBundle reported failure: $($installState | ConvertTo-Json -Depth 6 -Compress)"
    }
  }
  if (-not $bundleVisible) {
    throw "bundle '$bundleName' did not appear in pluginManager/listBundles within ${TimeoutSeconds}s"
  }
  $installed = $true
  Write-Host "RPC      : bundle visible in listBundles ($($bundleVisible.name) $($bundleVisible.version))" -ForegroundColor Green

  # ---- 5) desktop-name override (the gateway address lives in the persisted
  #         relay-config.json, never in a hand-written patch block)
  $desktopName = if ($Name) { $Name } elseif ($previousName) { $previousName } else { $null }
  if ($desktopName) {
    $override = Set-RelayConfigOverride -PatchFile $patchFile -Name $desktopName
    Write-Host "Config   : kept desktop name '$desktopName' (backup $(Split-Path -Leaf ([string]$override.Backup)))"
  }

  # ---- 6) wait for the bundle plugin to load and self-check ----------------
  $healthDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $health = $null
  while ((Get-Date) -lt $healthDeadline) {
    Start-Sleep -Milliseconds 1500
    $health = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort 8443
    if (-not $health) { continue }
    if (-not $health.protocolProbe) { continue }
    if ($health.protocolProbe.status -eq 'pending') { continue }
    # A relay from the previous version may still answer until the profile
    # recompose finishes; only accept the freshly installed release.
    if ($bundleVersion -and $health.relayVersion -ne $bundleVersion) { continue }
    if ($ForceReinstall -and $preHealth -and [string]$health.startedAt -eq [string]$preHealth.startedAt) { continue }
    break
  }
  if (-not $health) { throw 'relay health did not answer after the bundle install' }
  if ($health.protocolProbe.ok -ne $true) { throw "relay self-check failed after install: $(($health.protocolProbe.errors) -join '; ')" }
  Write-Host "Health   : relay $($health.relayVersion) on DSH $($health.dshVersion), self-check OK, config-source=$($health.gateway.source)" -ForegroundColor Green

  # ---- 6b) one-time initialization of the persistent config -----------------
  # If relay-config.json does not exist yet and this machine already had a relay
  # server configured (normally the fixed VPS from the old plugin), persist it
  # once through the config API so the user does not have to retype anything.
  # This is a one-time migration, not a hardcoded default: values come from this
  # machine's own prior configuration and are only used when the file is missing.
  $persisted = Get-RelayPersistedConfig -ConfigFile $configFile
  if ($NoSeedConfig) {
    Write-Host 'Config   : -NoSeedConfig set; not seeding relay-config.json'
  } elseif (-not $persisted -and $legacyGateway) {
    $seeded = Save-LoopbackRelayConfig -Gateway $legacyGateway -InitializedBy 'one-time-init:installer-seed' -RelayPort 8443
    $persisted = Get-RelayPersistedConfig -ConfigFile $configFile
    if (-not $persisted) { throw "one-time initialization did not create $configFile" }
    Write-Host "Config   : one-time initialization filled relay-config.json from the pre-install setup -> $($seeded.gateway.url)" -ForegroundColor Green
    $health = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort 8443
    if (-not $health) { throw 'relay health did not answer after the one-time initialization' }
  } elseif ($persisted) {
    Write-Host "Config   : persisted relay-config.json kept (server $($persisted.gateway.host):$($persisted.gateway.port))"
  }

  # ---- 7) one-time migration cleanup ---------------------------------------
  # The new plugin seeds relay-config.json from the legacy profile patch when the
  # document is missing (so an existing VPS address keeps working untouched).
  # Once that happened, drop the gateway block from the patch: the JSON document
  # is now the single, user-editable source of truth.
  $persisted = Get-RelayPersistedConfig -ConfigFile $configFile
  if ($NoSeedConfig) {
    Write-Host 'Config   : -NoSeedConfig set; leaving the profile patch and config file untouched'
  } elseif ($persisted -and $persisted.gateway) {
    if (Test-RelayPatchGatewayBlock -PatchFile $patchFile) {
      $desktopName = if ($Name) { $Name } elseif ($health.name) { [string]$health.name } else { $null }
      $null = Set-RelayConfigOverride -PatchFile $patchFile -Name $desktopName
      Write-Host 'Config   : one-time migration done — gateway moved to relay-config.json; removed it from the profile patch' -ForegroundColor Green
      $reloadDeadline = (Get-Date).AddSeconds([Math]::Min($TimeoutSeconds, 90))
      while ((Get-Date) -lt $reloadDeadline) {
        Start-Sleep -Milliseconds 1200
        $reloaded = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort 8443
        if (-not $reloaded) { continue }
        if (-not $reloaded.gateway) { continue }
        if ($reloaded.gateway.source -eq 'file') { $health = $reloaded; break }
      }
      if (-not $health.gateway -or $health.gateway.source -ne 'file') {
        throw 'relay did not switch to the persisted relay-config.json after the one-time migration'
      }
      Write-Host "Config   : relay now reads $configFile (source=$($health.gateway.source))" -ForegroundColor Green
    } else {
      Write-Host 'Config   : persisted relay-config.json already authoritative (no legacy gateway block in the patch)'
    }
  } else {
    Write-Host 'Config   : no persisted relay-config.json yet (configure the relay server via /__relay/pair or scripts/relay-config.ps1)'
  }

  # ---- 8) optional relay-server settings from the command line ------------
  $wantsGatewayUpdate = $PSBoundParameters.ContainsKey('GatewayHost') -or
    $PSBoundParameters.ContainsKey('GatewayPort') -or
    $PSBoundParameters.ContainsKey('GatewayPath') -or
    $PSBoundParameters.ContainsKey('GatewayFingerprint') -or
    $GatewayEnable -or $GatewayDisable -or $GatewayUrl
  if ($wantsGatewayUpdate) {
    $current = Get-LoopbackRelayConfig -RelayPort 8443
    if (-not $current) { throw 'relay config API is not answering; cannot apply the gateway settings' }
    $gw = $current.gateway
    $fromUrl = if ($GatewayUrl) { ConvertFrom-GatewayUrl -Url $GatewayUrl } else { $null }
    $next = @{
      enabled     = if ($GatewayEnable) { $true } elseif ($GatewayDisable) { $false } elseif ($null -ne $gw.enabled) { [bool]$gw.enabled } else { $true }
      host        = if ($PSBoundParameters.ContainsKey('GatewayHost')) { $GatewayHost } elseif ($fromUrl) { $fromUrl.Host } else { [string]$gw.host }
      port        = if ($PSBoundParameters.ContainsKey('GatewayPort')) { $GatewayPort } elseif ($fromUrl) { $fromUrl.Port } else { [int]$gw.port }
      path        = if ($PSBoundParameters.ContainsKey('GatewayPath')) { $GatewayPath } elseif ($fromUrl) { $fromUrl.Path } else { [string]$gw.path }
      secure      = if ($fromUrl) { [bool]$fromUrl.Secure } elseif ($null -ne $gw.secure) { [bool]$gw.secure } else { $true }
      fingerprint = if ($PSBoundParameters.ContainsKey('GatewayFingerprint')) { $GatewayFingerprint } else { $gw.fingerprint }
      name        = if ($GatewayName) { $GatewayName } elseif ($gw.name) { [string]$gw.name } else { 'DSH Remote Gateway' }
    }
    if ($GatewaySecret) { $next.secret = $GatewaySecret }
    $state = Save-LoopbackRelayConfig -Gateway $next -RelayPort 8443
    Write-Host "Config   : relay server updated -> $($state.gateway.url) (enabled=$($state.gateway.enabled))" -ForegroundColor Green
    $health = Get-RelayHealth -TargetHost '127.0.0.1' -TargetPort 8443
  }

  if (Test-Path -LiteralPath $paths.PairingFile) {
    $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json
    Write-Host "Pairing  : schemaVersion=$($pairing.schemaVersion) mode=$(if ($pairing.mode) { $pairing.mode } else { 'local' }) active=$($pairing.active) host=$($pairing.host):$($pairing.port)"
  }
  if ($health -and $health.gateway) {
    Write-Host "Gateway  : enabled=$($health.gateway.enabled) online=$($health.gateway.online) host=$($health.gateway.host):$($health.gateway.port)$($health.gateway.path) source=$($health.gateway.source)"
  }
  Write-Host ''
  Write-Host 'Bundle install: DONE (dev patch removed -> bundle installed)' -ForegroundColor Green
  exit 0
} catch {
  Write-Host ''
  Write-Warning "Bundle install failed: $($_.Exception.Message)"
  if ($NoRollback) {
    Write-Warning 'NoRollback set; leaving the profile as is.'
    exit 1
  }
  Write-Host 'Rolling back the patch and removing the bundle ...' -ForegroundColor Yellow
  try {
    if ($installed) {
      $null = Invoke-HostRpc -UpstreamPort $loopback.UpstreamPort -CookieJar $cookieJar -Method 'pluginManager/removeBundle' -Arguments @{ name = $bundleName } -TimeoutSec 120
    }
  } catch { Write-Warning "removeBundle failed: $($_.Exception.Message)" }
  try {
    if ($null -ne $beforePatch) { Write-RelayPatchFile -Path $patchFile -Text $beforePatch }
    else { Remove-RelayPatchBlock -PatchFile $patchFile -NoBackup | Out-Null }
    Write-Host 'Restored the previous profile patch.' -ForegroundColor Yellow
    # Re-create the dev junction/insert so the previous dev install keeps working.
    & (Join-Path $PSScriptRoot 'dev-install.ps1') -Profile $Profile -SkipWait | Out-Null
    Write-Host 'Re-ran dev-install.ps1 to restore the dev install (hot reload may need a tick).' -ForegroundColor Yellow
  } catch { Write-Warning "restore patch failed: $($_.Exception.Message)" }
  exit 1
} finally {
  Remove-Item -LiteralPath $cookieJar -Force -ErrorAction SilentlyContinue
}

