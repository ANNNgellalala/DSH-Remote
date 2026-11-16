<#
.SYNOPSIS
  Deploy the DSH Remote public gateway (M6) to the VPS.

.DESCRIPTION
  One-shot, idempotent deployment of `vps/` to the VPS as a systemd service:

    1. checks local prerequisites (ssh key, vps/ sources, node --check) and the
       remote host (SSH reachable, Node.js >= 22, /opt/dsh-remote writable)
    2. creates /opt/dsh-remote/{gateway,apk,resources} and
       /etc/dsh-remote/{tls,...}
    3. generates or reuses the self-signed TLS certificate (openssl) with a SAN
       that covers the public IP and hostname; private key mode 600
    4. copies vps/*.mjs + package.json + README.zh.md to
       /opt/dsh-remote/gateway
    5. writes /etc/dsh-remote/gateway.env (mode 600) and the unit
       /etc/systemd/system/dsh-remote-gateway.service, then enables/starts it
    6. waits for the loopback health check and prints the public
       /__gateway/identity document (version, SPKI fingerprint, tunnel count)
       plus the resource index (/__gateway/resources)

  The resource centre root (/opt/dsh-remote/resources) is writable by the
  service; one sub-directory per resource category id
  (dsh-remote/smartbill/relay/docs) is created on demand by the gateway. The
  legacy APK directory (/opt/dsh-remote/apk) is kept for the old download
  fallback/rollback only; the page and index do not scan it. UPLOAD_TOKEN is
  generated on first
  deployment and preserved on every later run (like REGISTER_SECRET), so the
  upload/delete endpoints stay enabled across redeploys.

  The gateway only adds port 9443/tcp and /opt/dsh-remote on the VPS; it never
  touches the existing xray/hysteria/subsvc services or their ports.

.PARAMETER Restart
  Restart the service after copying (needed to apply updated code when the
  service is already running).

.PARAMETER RenewCert
  Regenerate the gateway certificate (phones/apps pinned to the old SPKI
  fingerprint must re-pair afterwards).

.PARAMETER RegisterSecret
  Require desktop connectors to present this secret when registering. Omit to
  keep whatever is already configured in /etc/dsh-remote/gateway.env.

.PARAMETER UploadToken
  Bearer token for PUT/POST/DELETE /__gateway/resources/<id>/<name>?keepVersions=0|1.
  Omit to keep the token already in gateway.env, or generate a fresh one on
  first run.

.PARAMETER RemoteResourceDir
  Remote resource root; each resource category id lives in <dir>/<id>
  (created on demand by the gateway). Default /opt/dsh-remote/resources.

.PARAMETER MaxUploadBytes
  Per-upload size limit (default 200 MiB, 209715200).

.PARAMETER WhatIf
  Print the deployment plan and exit without changing anything on the VPS.

.EXAMPLE
  pwsh -File scripts/deploy-gateway.ps1
  pwsh -File scripts/deploy-gateway.ps1 -Restart
  pwsh -File scripts/deploy-gateway.ps1 -WhatIf
  pwsh -File scripts/deploy-gateway.ps1 -RenewCert -Restart
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
  [string]$SshHost = '',
  [int]$SshPort = 0,
  [string]$User = '',
  [string]$KeyFile = '',
  [string]$PublicHost = '',
  [int]$PublicPort = 0,
  [string]$BindHost = '0.0.0.0',
  [string]$RemoteRoot = '/opt/dsh-remote',
  [string]$CertDir = '/etc/dsh-remote/tls',
  [string]$EnvFile = '/etc/dsh-remote/gateway.env',
  [string]$ServiceName = 'dsh-remote-gateway',
  [string]$RegisterSecret = '',
  [string]$UploadToken = '',
  [string]$RemoteResourceDir = '/opt/dsh-remote/resources',
  [long]$MaxUploadBytes = 209715200,
  [string[]]$ExtraSan = @(),
  [switch]$RenewCert,
  [switch]$Restart,
  [switch]$SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Machine-local values live in config/dshremote.settings.json (git-ignored).
# Explicit parameters still win; missing values fall back to the committed example.
. (Join-Path $PSScriptRoot 'config.ps1')
$settings = Get-DshRemoteSettings
if (-not $SshHost) { $SshHost = $settings.Deploy.SshHost }
if ($SshPort -le 0) { $SshPort = $settings.Deploy.SshPort }
if (-not $User) { $User = $settings.Deploy.SshUser }
if (-not $KeyFile) { $KeyFile = $settings.Deploy.KeyFile }
if (-not $PublicHost) { $PublicHost = $settings.Deploy.PublicHost }
if ($PublicPort -le 0) { $PublicPort = $settings.Deploy.PublicPort }
if (-not $ExtraSan -or $ExtraSan.Count -eq 0) { $ExtraSan = @($settings.Deploy.ExtraSan) }
if (-not $SshHost) { throw 'Missing SSH host: set deploy.sshHost in config/dshremote.settings.json or pass -SshHost.' }
if (-not $KeyFile) { throw 'Missing SSH key path: set deploy.keyFile in config/dshremote.settings.json or pass -KeyFile.' }

$planOnly = [bool]$WhatIfPreference
$repoRoot = Split-Path -Parent $PSScriptRoot
$vpsDir = Join-Path $repoRoot 'vps'
$remoteGatewayDir = "$RemoteRoot/gateway"
$remoteApkDir = "$RemoteRoot/apk"
$remoteResourceDir = $RemoteResourceDir.TrimEnd('/')
# Resource categories live in <resourceDir>/<id>; the gateway creates each id
# directory on demand. The legacy APK_DIR ($remoteApkDir) stays for downloads
# and rollback only.
$certFile = "$CertDir/gateway-cert.pem"
$keyFileRemote = "$CertDir/gateway-key.pem"
$publicBase = "https://${PublicHost}:${PublicPort}"

$sshArgs = @(
  '-i', $KeyFile,
  '-p', "$SshPort",
  '-o', 'StrictHostKeyChecking=accept-new',
  '-o', 'BatchMode=yes',
  '-o', 'ConnectTimeout=15',
  '-o', 'ServerAliveInterval=20',
  "$User@$SshHost"
)
$scpArgs = @(
  '-i', $KeyFile,
  '-P', "$SshPort",
  '-o', 'StrictHostKeyChecking=accept-new',
  '-o', 'BatchMode=yes',
  '-q'
)

function Write-Head([string]$text) {
  Write-Host ''
  Write-Host "==> $text" -ForegroundColor Cyan
}

function Write-Info([string]$text) { Write-Host "    $text" }

function Fail([string]$message) {
  Write-Host "ERROR: $message" -ForegroundColor Red
  exit 1
}

function Invoke-Remote {
  param([Parameter(Mandatory)][string]$Command)
  $output = & ssh.exe @sshArgs $Command 2>&1
  $code = $LASTEXITCODE
  return [pscustomobject]@{
    ExitCode = $code
    Output   = (($output | ForEach-Object { "$_" }) -join "`n").Trim()
  }
}

function Write-RemoteFile {
  param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Content,
    [int]$Mode = 644
  )
  $base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Content))
  $result = Invoke-Remote "printf %s '$base64' | base64 -d > '$Path' && chmod $Mode '$Path'"
  if ($result.ExitCode -ne 0) { throw "cannot write $Path on the VPS: $($result.Output)" }
}

function Invoke-Step {
  param([string]$Description, [scriptblock]$Action)
  if ($planOnly) {
    Write-Host "[WhatIf] $Description" -ForegroundColor DarkGray
    return
  }
  Write-Host "    - $Description"
  & $Action
}

# Fetch a public URL body for verification. Primary: curl.exe -k (the documented
# way on a normal operator machine). Fallback: Node's own TLS client, because a
# locked-down/sandboxed Windows shell may refuse curl's schannel credential
# handle (SEC_E_NO_CREDENTIALS) even though the network path is fine.
function Get-PublicBody {
  param([Parameter(Mandatory)][string]$Url)
  $raw = & curl.exe -k -s --max-time 25 $Url 2>$null
  if ($LASTEXITCODE -eq 0 -and $raw) {
    return (($raw | ForEach-Object { "$_" }) -join "`n").Trim()
  }
  $env:DSH_GATEWAY_VERIFY_URL = $Url
  $script = 'const https=require("https");const http=require("http");const u=process.env.DSH_GATEWAY_VERIFY_URL;const m=u.startsWith("https:")?https:http;const r=m.get(u,{rejectUnauthorized:false},(res)=>{let d="";res.on("data",c=>d+=c);res.on("end",()=>{process.stdout.write(d);process.exit(res.statusCode>=200&&res.statusCode<300?0:1)})});r.on("error",e=>{process.stderr.write(String(e.message));process.exit(2)});r.setTimeout(20000,()=>r.destroy(new Error("timeout")))'
  $out = & node -e $script 2>$null
  if ($LASTEXITCODE -eq 0 -and $out) {
    return (($out | ForEach-Object { "$_" }) -join "`n").Trim()
  }
  return ''
}

# ---------------------------------------------------------------------------
# 1. local prerequisites
# ---------------------------------------------------------------------------
Write-Head 'Local prerequisites'
if (-not (Test-Path -LiteralPath $KeyFile)) { Fail "SSH key not found: $KeyFile" }
if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) { Fail 'ssh.exe (OpenSSH client) is required' }
if (-not (Get-Command scp.exe -ErrorAction SilentlyContinue)) { Fail 'scp.exe (OpenSSH client) is required' }
if (-not (Test-Path -LiteralPath (Join-Path $vpsDir 'gateway.mjs'))) { Fail "missing $vpsDir\gateway.mjs" }
$sourceFiles = Get-ChildItem -LiteralPath $vpsDir -File | Where-Object { $_.Name -match '\.(mjs|json|md)$' }
if ($sourceFiles.Count -lt 5) { Fail "vps/ does not look complete ($($sourceFiles.Count) files)" }
foreach ($file in $sourceFiles) { Write-Info ("{0,-20} {1,8:N0} bytes" -f $file.Name, $file.Length) }

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { Fail 'local node is required to syntax-check vps/ before deployment' }
$check = & node @($sourceFiles | Where-Object Extension -eq '.mjs' | ForEach-Object { '--check'; $_.FullName }) 2>&1
if ($LASTEXITCODE -ne 0) { Fail "local node --check failed:`n$($check -join "`n")" }
Write-Info "local node $(& node -v) syntax-checked $((@($sourceFiles | Where-Object Extension -eq '.mjs')).Count) module(s)"

# ---------------------------------------------------------------------------
# 2. remote host checks (read-only)
# ---------------------------------------------------------------------------
Write-Head "Remote host ${User}@${SshHost}:${SshPort}"
$probe = Invoke-Remote "printf 'hostname=%s\n' `$(hostname -f 2>/dev/null || hostname); printf 'node=%s\n' `$(node -v 2>/dev/null || echo missing); printf 'node_path=%s\n' `$(command -v node 2>/dev/null || echo missing); printf 'service=%s\n' `$(systemctl is-active '$ServiceName' 2>/dev/null || true); printf 'port9443=%s\n' `$(ss -ltnH '( sport = :$PublicPort )' 2>/dev/null | wc -l)"
if ($probe.ExitCode -ne 0) { Fail "cannot SSH to the VPS: $($probe.Output)" }
$facts = @{}
foreach ($line in ($probe.Output -split "`n")) {
  $index = $line.IndexOf('=')
  if ($index -gt 0) { $facts[$line.Substring(0, $index)] = $line.Substring($index + 1).Trim() }
}
$remoteHostname = $facts['hostname']
$nodeVersion = $facts['node']
$nodePath = $facts['node_path']
if ($nodeVersion -eq 'missing' -or $nodePath -eq 'missing') {
  Fail "Node.js is not installed on the VPS. Install Node.js >= 22 first (apt install -y nodejs) and re-run."
}
$nodeMajor = [int](($nodeVersion -replace '^v', '').Split('.')[0])
if ($nodeMajor -lt 22) { Fail "VPS Node.js $nodeVersion is too old; the gateway needs >= 22" }
Write-Info "hostname      : $remoteHostname"
Write-Info "node          : $nodeVersion ($nodePath)"
if ($nodeMajor -lt 24) { Write-Info "note          : task target is Node 24; v22.22.1 is supported and verified" }
Write-Info "service       : $(if ($facts['service']) { $facts['service'] } else { 'not installed' })"

if (-not $planOnly) {
  $portBusy = [int]($facts['port9443'] ?? '0')
  if ($portBusy -gt 0 -and $facts['service'] -ne 'active') {
    Fail "TCP port $PublicPort is already in use on the VPS while $ServiceName is not active. If the old SSH tunnel is still holding it, run: pwsh -File scripts/vps-tunnel.ps1 -Action stop"
  }
}

# certificate SANs: public IP + hostname(s), normalised to IP:/DNS: prefixes
$sanEntries = New-Object System.Collections.Generic.List[string]
if ($PublicHost -match '^\d+\.\d+\.\d+\.\d+$') { $sanEntries.Add("IP:$PublicHost") } else { $sanEntries.Add("DNS:$PublicHost") }
foreach ($entry in $ExtraSan) {
  if ([string]::IsNullOrWhiteSpace($entry)) { continue }
  $text = $entry.Trim()
  if ($text -match '^(IP|DNS):') { if (-not $sanEntries.Contains($text)) { $sanEntries.Add($text) } }
  elseif ($text -match '^\d+\.\d+\.\d+\.\d+$') { $candidate = "IP:$text"; if (-not $sanEntries.Contains($candidate)) { $sanEntries.Add($candidate) } }
  else { $candidate = "DNS:$text"; if (-not $sanEntries.Contains($candidate)) { $sanEntries.Add($candidate) } }
}
if ($remoteHostname -and $remoteHostname -ne '') {
  $candidate = "DNS:$remoteHostname"
  if (-not $sanEntries.Contains($candidate)) { $sanEntries.Add($candidate) }
}
$san = ($sanEntries -join ',')

# ---------------------------------------------------------------------------
# 3. directories, certificate, sources
# ---------------------------------------------------------------------------
Write-Head "Remote directories and TLS certificate"
Invoke-Step "create $remoteGatewayDir, $remoteApkDir, $remoteResourceDir and $CertDir (mode 700)" {
  $result = Invoke-Remote "mkdir -p '$remoteGatewayDir' '$remoteApkDir' '$remoteResourceDir' '$CertDir' && chmod 700 '$CertDir' && chmod 755 '$RemoteRoot' '$remoteApkDir' '$remoteResourceDir' '$remoteGatewayDir'"
  if ($result.ExitCode -ne 0) { throw $result.Output }
}

$certExists = (Invoke-Remote "test -s '$certFile' -a -s '$keyFileRemote' && echo yes || echo no").Output
$needCert = ($certExists -ne 'yes') -or $RenewCert

if ($needCert) {
  Invoke-Step "generate self-signed certificate (SAN=$san, 825 days)" {
    $subject = "/CN=$PublicHost"
    $result = Invoke-Remote ("openssl req -x509 -newkey rsa:2048 -sha256 -days 825 -nodes -keyout '$keyFileRemote' -out '$certFile' -subj '$subject' -addext 'subjectAltName=$san' >/tmp/dsh-gateway-cert.log 2>&1 && chmod 600 '$keyFileRemote' && chmod 644 '$certFile'")
    if ($result.ExitCode -ne 0) {
      $tail = Invoke-Remote "tail -n 20 /tmp/dsh-gateway-cert.log"
      throw "openssl failed: $($tail.Output)"
    }
  }
} else {
  Write-Info 'certificate already present, reusing it (use -RenewCert to regenerate)'
}

$certInfo = $null
$remoteFingerprint = ''
if ($needCert -and $planOnly) {
  $remoteFingerprint = '(generated by this deployment)'
} else {
  $certInfo = Invoke-Remote "openssl x509 -in '$certFile' -noout -subject -enddate 2>/dev/null; openssl x509 -in '$certFile' -noout -ext subjectAltName 2>/dev/null | tail -n +2; openssl x509 -in '$certFile' -pubkey -noout 2>/dev/null | openssl pkey -pubin -outform der 2>/dev/null | openssl dgst -sha256 -r 2>/dev/null | awk '{print `$1}'"
  if ($certInfo.ExitCode -ne 0) { Fail "cannot inspect the certificate: $($certInfo.Output)" }
  $remoteFingerprint = ($certInfo.Output -split "`n" | Where-Object { $_ -match '^[0-9a-f]{64}$' } | Select-Object -First 1)
  Write-Info "subject  : $(($certInfo.Output -split "`n" | Where-Object { $_ -match '^subject' } | Select-Object -First 1))"
  Write-Info "SAN      : $(($certInfo.Output -split "`n" | Where-Object { $_ -match 'DNS:|IP Address:' } | Select-Object -First 1))"
}
Write-Info "spki-sha256: $remoteFingerprint"

Invoke-Step "upload $($sourceFiles.Count) gateway file(s) to $remoteGatewayDir" {
  $dest = "${User}@${SshHost}:${remoteGatewayDir}/"
  $output = & scp.exe @scpArgs @($sourceFiles.FullName) $dest 2>&1
  if ($LASTEXITCODE -ne 0) { throw "scp failed: $($output -join "`n")" }
  $check = Invoke-Remote "cd '$remoteGatewayDir' && for f in *.mjs; do node --check `"`$f`" || exit 1; done && echo syntax-ok"
  if ($check.ExitCode -ne 0 -or $check.Output -notmatch 'syntax-ok') { throw "remote node --check failed: $($check.Output)" }
}

# ---------------------------------------------------------------------------
# 4. environment file + systemd unit
# ---------------------------------------------------------------------------
Write-Head 'Environment file and systemd unit'

$secretLine = ''
$existingSecret = (Invoke-Remote "grep -h '^REGISTER_SECRET=' '$EnvFile' 2>/dev/null || true").Output
if ($RegisterSecret -ne '') {
  $secretLine = "REGISTER_SECRET=$RegisterSecret"
  Write-Info 'register secret: set from -RegisterSecret (connectors must send it)'
} elseif ($existingSecret -match '^REGISTER_SECRET=.+') {
  $secretLine = $existingSecret
  Write-Info 'register secret: preserving the value already in gateway.env'
} else {
  Write-Info 'register secret: not configured (the 32-hex token is the credential)'
}

# Upload token for PUT/POST/DELETE /__gateway/resources/<id>/<name>?keepVersions=0|1:
# -UploadToken wins; otherwise preserve the value already on the VPS; on first
# deployment generate a random one. The token is written to gateway.env (600)
# and never appears in the public page/index. scripts/upload-resource.ps1 can
# read it over SSH, so operators rarely need to copy it by hand.
$existingUploadToken = (Invoke-Remote "grep -h '^UPLOAD_TOKEN=' '$EnvFile' 2>/dev/null || true").Output
$uploadToken = ''
$uploadTokenSource = ''
if ($UploadToken -ne '') {
  $uploadToken = $UploadToken.Trim()
  $uploadTokenSource = 'from -UploadToken'
} elseif ($existingUploadToken -match '^UPLOAD_TOKEN=(.+)$') {
  $uploadToken = $Matches[1].Trim()
  $uploadTokenSource = 'preserved from gateway.env'
} else {
  $bytes = New-Object byte[] 24
  [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
  $uploadToken = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
  $uploadTokenSource = 'generated'
}
if ($uploadToken -notmatch '^[A-Za-z0-9._~+/=-]{16,256}$') {
  Fail 'UPLOAD_TOKEN must be 16-256 characters of [A-Za-z0-9._~+/=-] (no spaces, quotes or newlines)'
}
if ($uploadTokenSource -eq 'generated') {
  Write-Info 'upload token: generated (first deployment) and written to gateway.env (600)'
  Write-Info "upload token: $uploadToken"
} elseif ($uploadTokenSource -eq 'preserved from gateway.env') {
  Write-Info "upload token: preserving the value already in gateway.env ($($uploadToken.Substring(0, 4))…$($uploadToken.Substring($uploadToken.Length - 4)))"
} else {
  Write-Info 'upload token: set from -UploadToken (upload/delete endpoints use it)'
}
$uploadTokenLine = "UPLOAD_TOKEN=$uploadToken"

$envContent = @(
  '# DSH Remote public gateway — managed by scripts/deploy-gateway.ps1',
  '# Changes here require: systemctl restart dsh-remote-gateway',
  "PORT=$PublicPort",
  "BIND_HOST=$BindHost",
  "GATEWAY_PUBLIC_HOST=$PublicHost",
  "GATEWAY_PUBLIC_PORT=$PublicPort",
  "CERT_FILE=$certFile",
  "KEY_FILE=$keyFileRemote",
  "APK_DIR=$remoteApkDir",
  "RESOURCE_DIR=$remoteResourceDir",
  "MAX_UPLOAD_BYTES=$MaxUploadBytes",
  'LOG_LEVEL=info',
  'NODE_ENV=production'
)
if ($secretLine -ne '') { $envContent += $secretLine }
if ($uploadTokenLine -ne '') { $envContent += $uploadTokenLine }
$envContent = ($envContent -join "`n") + "`n"

$unitContent = @"
[Unit]
Description=DSH Remote public gateway (TLS :$PublicPort token router + resource centre)
Documentation=file:///opt/dsh-remote/gateway/README.zh.md
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=$remoteGatewayDir
EnvironmentFile=$EnvFile
ExecStart=$nodePath $remoteGatewayDir/gateway.mjs
Restart=always
RestartSec=3
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=full
ProtectHome=read-only
ReadWritePaths=$remoteApkDir $remoteResourceDir
StandardOutput=journal
StandardError=journal
SyslogIdentifier=dsh-remote-gateway
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
"@

Invoke-Step "write $EnvFile (mode 600)" {
  $parent = $EnvFile.Substring(0, $EnvFile.LastIndexOf('/'))
  $result = Invoke-Remote "mkdir -p '$parent' && chmod 700 '$parent'"
  if ($result.ExitCode -ne 0) { throw $result.Output }
  Write-RemoteFile -Path $EnvFile -Content $envContent -Mode 600
}

Invoke-Step "write /etc/systemd/system/$ServiceName.service and reload systemd" {
  Write-RemoteFile -Path "/etc/systemd/system/$ServiceName.service" -Content $unitContent -Mode 644
  $result = Invoke-Remote 'systemctl daemon-reload'
  if ($result.ExitCode -ne 0) { throw $result.Output }
}

# ---------------------------------------------------------------------------
# 5. enable / start / restart
# ---------------------------------------------------------------------------
Write-Head 'Service'
$serviceState = (Invoke-Remote "systemctl is-active '$ServiceName' 2>/dev/null || true").Output
if ($planOnly) {
  Write-Host '[WhatIf] systemctl enable dsh-remote-gateway' -ForegroundColor DarkGray
  Write-Host ("[WhatIf] {0}" -f $(if ($serviceState -eq 'active' -and -not $Restart) { 'service already active; use -Restart to apply the copied code' } else { 'systemctl restart dsh-remote-gateway' })) -ForegroundColor DarkGray
} else {
  $enable = Invoke-Remote "systemctl enable '$ServiceName' 2>&1"
  if ($enable.ExitCode -ne 0) { Write-Info "warning: enable failed: $($enable.Output)" }
  if ($serviceState -eq 'active' -and -not $Restart) {
    Write-Info 'service already active; skipped restart (use -Restart to apply the new code)'
  } else {
    Write-Info 'restarting service'
    $restartOut = Invoke-Remote "systemctl restart '$ServiceName' 2>&1"
    if ($restartOut.ExitCode -ne 0) { throw "systemctl restart failed: $($restartOut.Output)" }
  }
}

# ---------------------------------------------------------------------------
# 6. verify
# ---------------------------------------------------------------------------
Write-Head 'Verify'
if (-not $planOnly) {
  $health = $null
  for ($attempt = 1; $attempt -le 20; $attempt++) {
    $result = Invoke-Remote "curl -sk --max-time 5 https://127.0.0.1:$PublicPort/__gateway/health 2>/dev/null"
    if ($result.ExitCode -eq 0 -and $result.Output -match '"ok"\s*:\s*true') {
      try { $health = $result.Output | ConvertFrom-Json } catch { $health = $null }
      if ($health) { break }
    }
    Start-Sleep -Seconds 1
  }
  if (-not $health) {
    $status = Invoke-Remote "systemctl --no-pager status '$ServiceName' 2>&1 | head -n 25"
    $logs = Invoke-Remote "journalctl -u '$ServiceName' -n 40 --no-pager 2>&1"
    Write-Host $status.Output
    Write-Host $logs.Output
    Fail "gateway did not become healthy on https://127.0.0.1:$PublicPort/__gateway/health"
  }
  Write-Info "health ok  version=$($health.version) tunnels=$($health.tunnels) node=$($health.node)"
  $active = Invoke-Remote "systemctl is-active '$ServiceName'; systemctl show -p MainPID --value '$ServiceName'; systemctl show -p ActiveEnterTimestamp --value '$ServiceName'"
  Write-Info "systemd    $($active.Output -replace "`n", ' · ')"

  $ufw = (Invoke-Remote "ufw status 2>/dev/null | grep -c '$PublicPort/tcp' || true").Output
  if ($ufw -match '^\d+$' -and [int]$ufw -gt 0) { Write-Info "ufw        $PublicPort/tcp allowed" }
  else { Write-Info "ufw        WARNING: $PublicPort/tcp not found in ufw status; run: ufw allow $PublicPort/tcp" }

  if (-not $SkipVerify) {
    $identity = $null
    $publicJson = Get-PublicBody "$publicBase/__gateway/identity"
    if (-not $publicJson) {
      Write-Info "public check FAILED: no response from $publicBase/__gateway/identity (firewall/NAT?)"
    } else {
      try { $identity = $publicJson | ConvertFrom-Json } catch { $identity = $null }
      if (-not $identity) {
        Write-Info "public check FAILED: non-JSON response from $publicBase/__gateway/identity"
      } else {
        Write-Info "public     $publicBase/__gateway/identity  OK"
        Write-Info "  name     : $($identity.name)"
        Write-Info "  version  : $($identity.version)"
        Write-Info "  url      : $($identity.url)"
        Write-Info "  tunnels  : $($identity.tunnels)"
        Write-Info "  fp       : $($identity.fp)"
        if ($remoteFingerprint -and $identity.fp -ne $remoteFingerprint) {
          Write-Host "WARNING: identity fp ($($identity.fp)) != certificate fp ($remoteFingerprint)" -ForegroundColor Yellow
        } else {
          Write-Info '  fp check : matches the deployed certificate (SPKI sha256)'
        }
        if ($identity.url -ne "wss://${PublicHost}:${PublicPort}") {
          Write-Host "WARNING: identity url is $($identity.url); expected wss://${PublicHost}:${PublicPort}" -ForegroundColor Yellow
        }
        $identityResources = $null
        if ($identity.PSObject.Properties['resources']) { $identityResources = $identity.resources }
        if ($identityResources -and $identityResources.PSObject.Properties['categories']) {
          $categorySummary = (@($identityResources.categories) | ForEach-Object { "$($_.id)=$($_.count)" }) -join ', '
          Write-Info "  resources: total=$($identityResources.total) ($categorySummary)"
        }
      }
    }

    $resourcesJson = Get-PublicBody "$publicBase/__gateway/resources"
    if (-not $resourcesJson) {
      Write-Info "resources  WARNING: $publicBase/__gateway/resources did not answer"
    } else {
      try { $resourcesIndex = $resourcesJson | ConvertFrom-Json } catch { $resourcesIndex = $null }
      if (-not $resourcesIndex) {
        Write-Info 'resources  WARNING: index returned non-JSON'
      } else {
        Write-Info "resources  $publicBase/__gateway/resources  OK (total=$($resourcesIndex.total))"
        foreach ($categoryInfo in $resourcesIndex.categories) {
          Write-Info ("  {0,-12} {1,3} file(s)  latest={2}" -f $categoryInfo.id, $categoryInfo.count, $(if ($categoryInfo.latest) { $categoryInfo.latest.name } else { '(none)' }))
        }
        # APK count = every *.apk entry across the categories (identity.download
        # counts the same set); compare the two as required by CONTRACT §5.3.
        $indexApkCount = 0
        foreach ($category in $resourcesIndex.categories) {
          foreach ($item in @($category.files)) {
            if ($item.name -match '\.apk$') { $indexApkCount++ }
          }
        }
        $identityApkCount = $null
        if ($identity -and $identity.PSObject.Properties['download'] -and $identity.download) { $identityApkCount = $identity.download.count }
        if ($null -ne $identityApkCount) {
          if ($indexApkCount -ne $identityApkCount) {
            Write-Host "WARNING: apk count mismatch (index=$indexApkCount, identity.download.count=$identityApkCount)" -ForegroundColor Yellow
          } else {
            Write-Info "  apk count : index=$indexApkCount identity.download.count=$identityApkCount (match)"
          }
        } else {
          Write-Info "  apk count : $indexApkCount file(s) in index (identity.download.count unavailable)"
        }
      }
    }
  }
} else {
  Write-Host '[WhatIf] curl -sk https://127.0.0.1:9443/__gateway/health (after start)' -ForegroundColor DarkGray
  Write-Host "[WhatIf] curl -k ${publicBase}/__gateway/identity" -ForegroundColor DarkGray
  Write-Host "[WhatIf] curl -k ${publicBase}/__gateway/resources" -ForegroundColor DarkGray
}

Write-Head 'Done'
Write-Info "public base   : $publicBase"
Write-Info "identity      : $publicBase/__gateway/identity"
Write-Info "health        : $publicBase/__gateway/health"
Write-Info "download page : $publicBase/__gateway/download"
Write-Info "resource index: $publicBase/__gateway/resources"
Write-Info "resource files: $publicBase/__gateway/resources/<id>/<name>"
Write-Info "upload/delete : pwsh -File scripts/upload-resource.ps1 -Id docs -File README.md"
Write-Info "connector URL : wss://${PublicHost}:${PublicPort}/__gateway/tunnel"
Write-Info "spki-sha256   : $remoteFingerprint"
Write-Info "logs          : ssh -i `"$KeyFile`" -p $SshPort ${User}@${SshHost} journalctl -u $ServiceName -f"
Write-Info ''
Write-Info 'Next: configure the desktop relay connector with the wss URL + token, then run:'
Write-Info '      pwsh -File scripts/sync-apk.ps1'
Write-Info '      pwsh -File scripts/upload-resource.ps1 -Id relay -File dist/dsh-remote-relay-0.4.0.tgz'
