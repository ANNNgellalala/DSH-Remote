<#
.SYNOPSIS
  End-to-end smoke test of the DSH Remote TLS relay.

.DESCRIPTION
  Reads %USERPROFILE%\.dsh\remote\pairing.json, then drives the real remote
  protocol through the relay exactly like the phone client will:

    1. GET https://<host>:<port>/__relay/health           -> 200 (TLS hop up)
    2. GET https://<host>:<port>/connect?token=..&interactions=none
                                                           -> 302 + Set-Cookie
    3. POST https://<host>:<port>/api/session/list         -> 200, result.ok=true

  The certificate is self-signed: -k skips the system trust store, while
  --pinnedpubkey enforces the SPKI SHA-256 from pairing.json (the same pin the
  phone uses), so a fingerprint mismatch fails the test.

.PARAMETER PairingFile
  Pairing document to test (default ~/.dsh/remote/pairing.json).

.PARAMETER Loopback
  Use 127.0.0.1:<loopbackPort> instead of the LAN address (local E2E).

.PARAMETER RpcId
  Correlation id for the RPC envelope (default: random).

.PARAMETER IncludeWs
  Also run the WebSocket upgrade check (_scratch/relay/ws-check.mjs): expects
  101 on /api/remote.mux plus the $events ready frame. Requires node on PATH.
#>
[CmdletBinding()]
param(
  [string]$PairingFile = (Join-Path $env:USERPROFILE '.dsh\remote\pairing.json'),
  [switch]$Loopback,
  [switch]$Gateway,
  [switch]$IncludeWs,
  [string]$RpcId = ('smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
)

$ErrorActionPreference = 'Stop'
if (-not $env:DSH_RELAY_SMOKE_ACTIVE) { $env:DSH_RELAY_SMOKE_ACTIVE = '1' }

function Write-Step { param([string]$Text) Write-Host "== $Text" }
function Write-Pass { param([string]$Text) Write-Host "PASS  $Text" -ForegroundColor Green }
function Write-Fail { param([string]$Text) Write-Host "FAIL  $Text" -ForegroundColor Red }
function Mask-Secret {
  param([string]$Value)
  if (-not $Value) { return '<empty>' }
  if ($Value.Length -le 8) { return '****' }
  return $Value.Substring(0, 4) + '…' + $Value.Substring($Value.Length - 4)
}

if (-not (Test-Path -LiteralPath $PairingFile)) {
  Write-Fail "pairing file not found: $PairingFile (run scripts/dev-install.ps1 first)"
  exit 2
}
$pairing = Get-Content -LiteralPath $PairingFile -Raw | ConvertFrom-Json

# Default: LAN TLS (v1 fields localHost/localPort/localFp/localPairingUri when the
# pairing is v2 gateway mode). -Gateway targets the public gateway instead.
function Get-UriToken {
  param([string]$Uri)
  if (-not $Uri) { return $null }
  try { return [string]([uri]$Uri).Query.TrimStart('?').Split('&') | ForEach-Object { if ($_ -like 'token=*') { return [uri]::UnescapeDataString($_.Substring(6)) } } } catch { return $null }
}

$gatewayMode = $pairing.PSObject.Properties.Name -contains 'mode' -and $pairing.mode -eq 'gateway'
if ($Gateway) {
  if (-not $gatewayMode) {
    Write-Fail "pairing is not in gateway mode (schemaVersion=$($pairing.schemaVersion)); run dev-install with gateway config first"
    exit 2
  }
  $targetHost = [string]$pairing.host
  $targetPort = [int]$pairing.port
  $token = [string]$pairing.token
  $fp = [string]$pairing.fp
} else {
  $targetHost = if ($Loopback) { '127.0.0.1' } elseif ($pairing.localHost) { [string]$pairing.localHost } else { [string]$pairing.host }
  $targetPort = if ($pairing.localPort) { [int]$pairing.localPort } else { [int]$pairing.port }
  if ($Loopback -and $pairing.loopbackPort) { $targetPort = [int]$pairing.loopbackPort }
  $localTokenFromUri = if ($pairing.PSObject.Properties.Name -contains 'localPairingUri') {
    try { ([uri]$pairing.localPairingUri).Query.TrimStart('?').Split('&') | ForEach-Object { if ($_ -like 'token=*') { [uri]::UnescapeDataString($_.Substring(6)) } } | Select-Object -First 1 } catch { $null }
  } else { $null }
  $token = if ($localTokenFromUri) { [string]$localTokenFromUri } else { [string]$pairing.token }
  $fp = if ($pairing.localFp) { [string]$pairing.localFp } elseif ($gatewayMode) { $null } else { [string]$pairing.fp }
}
if (-not $targetHost -or -not $targetPort -or -not $token -or -not $fp) {
  Write-Fail "pairing file is missing host/port/token/fp (mode=$(if ($Gateway) { 'gateway' } else { 'lan' })): $PairingFile"
  exit 2
}

$base = "https://${targetHost}:${targetPort}"
$escapedToken = [uri]::EscapeDataString($token)

# curl --pinnedpubkey wants the SPKI SHA-256 digest as base64 after `sha256//`.
$fpBytes = New-Object byte[] 32
for ($i = 0; $i -lt 32; $i++) { $fpBytes[$i] = [Convert]::ToByte($fp.Substring($i * 2, 2), 16) }
$pinnedBase64 = [Convert]::ToBase64String($fpBytes)
$pinArgs = @('--pinnedpubkey', "sha256//$pinnedBase64")

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ('dsh-relay-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
$cookieJar = Join-Path $tmpDir 'cookies.txt'
$headersFile = Join-Path $tmpDir 'connect.headers'
$bodyFile = Join-Path $tmpDir 'rpc.json'
$responseFile = Join-Path $tmpDir 'rpc.response.json'
$failed = 0

try {
  Write-Host "pairing : $PairingFile"
  Write-Host "relay   : $base  (host=$targetHost port=$targetPort$(if ($Gateway) { ', gateway mode' } elseif ($Loopback) { ', loopback mode' } else { ', LAN mode' }))"
  Write-Host "token   : $(Mask-Secret $token)"
  Write-Host "fp      : $fp"
  Write-Host ''

  # ---- 0) pairing self-check verdict ---------------------------------------
  Write-Step 'pairing self-check (active + capabilities)'
  $pairingActive = $pairing.PSObject.Properties.Name -contains 'active' -and $pairing.active -eq $true
  if ($pairingActive) {
    $caps = if ($pairing.PSObject.Properties.Name -contains 'capabilities' -and $pairing.capabilities) {
      ($pairing.capabilities.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
    } else { '(no capabilities field: relay < 0.2.0)' }
    Write-Pass "pairing active=true relayVersion=$($pairing.relayVersion) dshVersion=$($pairing.dshVersion)"
    Write-Host "      $caps"
  } else {
    $errCode = if ($pairing.PSObject.Properties.Name -contains 'error' -and $pairing.error) { $pairing.error.code } else { 'unknown' }
    $errMessage = if ($pairing.PSObject.Properties.Name -contains 'error' -and $pairing.error) { $pairing.error.message } else { 'pairing.active is not true' }
    Write-Fail "pairing is inactive ($errCode): $errMessage"
    if ($pairing.PSObject.Properties.Name -contains 'probe' -and $pairing.probe -and $pairing.probe.errors) {
      foreach ($probeError in @($pairing.probe.errors)) { Write-Host "      probe: $probeError" }
    }
    Write-Host '      (fix compatibility first: check relay health protocolProbe, then rerun scripts/dev-install.ps1)'
    $failed++
  }

  # ---- 1) health + certificate pin ------------------------------------------
  Write-Step 'TLS health + SPKI pin + relay self-check report'
  $healthCurlArgs = @()
  if ($Gateway) {
    # The gateway only routes /__relay/* for a known dsh-auth cookie, so obtain
    # one with a throwaway /connect first (the main connect check runs later).
    $healthJar = Join-Path $tmpDir 'health-cookies.txt'
    & curl.exe -k -sS @pinArgs -o NUL -c $healthJar "$base/connect?token=$escapedToken&interactions=none" 2>&1 | Out-Null
    $healthCurlArgs = @('-b', $healthJar)
  }
  $healthBody = & curl.exe -k -sS @pinArgs @healthCurlArgs -o - -w "`nHTTP_STATUS:%{http_code}" "$base/__relay/health" 2>&1
  $healthText = ($healthBody -join "`n")
  $healthStatus = if ($healthText -match 'HTTP_STATUS:(\d{3})') { [int]$Matches[1] } else { 0 }
  $healthJsonText = ($healthText -replace "`nHTTP_STATUS:\d{3}$", '').Trim()
  if ($healthStatus -eq 200) {
    $health = $healthJsonText | ConvertFrom-Json
    Write-Pass "health 200 (upstreamPort=$($health.upstreamPort), relayVersion=$($health.relayVersion), dshVersion=$($health.dshVersion), fp=$($health.fp.Substring(0,16))…)"
    if ($health.PSObject.Properties.Name -notcontains 'protocolProbe' -or -not $health.protocolProbe) {
      Write-Fail 'health has no protocolProbe (relay < 0.2.0 was not reloaded)'
      $failed++
    } elseif ($health.protocolProbe.ok -ne $true) {
      Write-Fail "health protocolProbe.ok is not true: missing=$(@($health.protocolProbe.missing) -join ',')"
      foreach ($probeError in @($health.protocolProbe.errors)) { Write-Host "      probe: $probeError" }
      $failed++
    } else {
      $probeCaps = ($health.protocolProbe.capabilities.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
      Write-Pass "protocol probe ok: $probeCaps"
    }

    if ($health.PSObject.Properties.Name -notcontains 'autoApprove' -or -not $health.autoApprove) {
      Write-Fail 'health has no autoApprove block (relay < 0.7.0 was not reloaded)'
      $failed++
    } else {
      Write-Pass "auto-approve: enabled=$($health.autoApprove.enabled) all=$($health.autoApprove.all) workspaces=$($health.autoApprove.workspaces) sessions=$($health.autoApprove.sessions) revision=$($health.autoApprove.revision)"
    }
  } else {
    Write-Fail "health request failed (status=$healthStatus) body=$healthJsonText"
    $failed++
  }

  # ---- 2) /connect -> 302 + Set-Cookie --------------------------------------
  Write-Step 'GET /connect?token=…&interactions=none (expect 302 + Set-Cookie)'
  $connectStatus = & curl.exe -k -sS @pinArgs -o NUL -D $headersFile -c $cookieJar -w '%{http_code}' "$base/connect?token=$escapedToken&interactions=none" 2>&1
  $connectHeaders = if (Test-Path -LiteralPath $headersFile) { Get-Content -LiteralPath $headersFile } else { @() }
  $setCookie = $connectHeaders | Where-Object { $_ -match '^set-cookie:' } | Select-Object -First 1
  $location = $connectHeaders | Where-Object { $_ -match '^location:' } | Select-Object -First 1
  if ("$connectStatus" -eq '302' -and $setCookie) {
    $cookieName = (($setCookie -replace '^set-cookie:\s*', '') -split '=', 2)[0]
    Write-Pass "connect 302, Set-Cookie name=$cookieName, $($location -replace '\s+', ' ')"
  } else {
    Write-Fail "connect expected 302 + Set-Cookie, got status=$connectStatus setCookie=$($setCookie -ne $null)"
    $connectHeaders | ForEach-Object { Write-Host "      $_" }
    $failed++
  }
  if (-not ($location -and $location -match '/api/remote\.mux')) {
    Write-Fail 'connect Location does not point at ./api/remote.mux'
    $failed++
  }

  # ---- 3) authenticated unary RPC ------------------------------------------
  Write-Step 'POST /api/session/list (expect 200 + result.ok=true)'
  $envelope = @{
    type   = 'client-request'
    rpcId  = $RpcId
    method = 'session/list'
    payload = @{ args = @{ _request = @{} } }
  } | ConvertTo-Json -Depth 6 -Compress
  Set-Content -LiteralPath $bodyFile -Value $envelope -Encoding utf8NoBOM
  $rpcStatus = & curl.exe -k -sS @pinArgs -b $cookieJar -H 'content-type: application/json' --data-binary "@$bodyFile" -o $responseFile -w '%{http_code}' "$base/api/session/list" 2>&1
  $rpcRaw = if (Test-Path -LiteralPath $responseFile) { Get-Content -LiteralPath $responseFile -Raw } else { '' }
  $rpc = $null
  try { $rpc = $rpcRaw | ConvertFrom-Json } catch {}
  if ("$rpcStatus" -eq '200' -and $rpc -and $rpc.result -and $rpc.result.ok -eq $true) {
    $items = @($rpc.result.value.items)
    Write-Pass "session/list 200, ok=true, rpcId=$($rpc.rpcId), sessions=$($items.Count)"
    $first = $items | Select-Object -First 1
    if ($first) {
      Write-Host "      first session: $($first.sessionId) running=$($first.running) cwd=$($first.cwd)"
    }
  } else {
    Write-Fail "session/list expected 200 + result.ok=true, got status=$rpcStatus"
    if ($rpcRaw) { Write-Host "      body: $($rpcRaw.Substring(0, [Math]::Min(600, $rpcRaw.Length)))" }
    $failed++
  }

  # ---- 4) optional WebSocket upgrade check ---------------------------------
  if ($IncludeWs) {
    Write-Step 'WebSocket upgrade /api/remote.mux + $events ready (node)'
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $wsScript = Join-Path $repoRoot '_scratch\relay\ws-check.mjs'
    $node = (Get-Command node.exe -ErrorAction SilentlyContinue)
    if (-not $node) {
      Write-Fail "node.exe not found on PATH; cannot run $wsScript"
      $failed++
    } elseif (-not (Test-Path -LiteralPath $wsScript)) {
      Write-Fail "WS check script not found: $wsScript"
      $failed++
    } else {
      $wsArgs = @($wsScript, $PairingFile)
      if ($Loopback) { $wsArgs += '--loopback' }
      & $node.Source @wsArgs
      if ($LASTEXITCODE -ne 0) { $failed++ }
    }
  }

  Write-Host ''
  if ($failed -eq 0) {
    Write-Host 'SMOKE: PASS (TLS relay + launch-token /connect + authenticated RPC)' -ForegroundColor Green
    if (-not $IncludeWs) { Write-Host '       (add -IncludeWs to also verify the WSS mux upgrade + $events ready)' }
    exit 0
  }
  Write-Host "SMOKE: FAIL ($failed check(s))" -ForegroundColor Red
  exit 1
} finally {
  Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

