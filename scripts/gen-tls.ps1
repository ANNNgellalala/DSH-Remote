<#
.SYNOPSIS
  Generate the DSH Remote relay self-signed TLS certificate (PEM cert/key).

.DESCRIPTION
  Creates a self-signed certificate whose SAN covers this machine's LAN IPv4
  address, 127.0.0.1, localhost and the computer name, exports it to a temporary
  PFX, then converts it with OpenSSL to unencrypted PEM files under relay/certs/.

  The phone client does not use the system trust store for this certificate: it
  pins the SHA-256 of the certificate's SubjectPublicKeyInfo (SPKI). This script
  prints that fingerprint, and the relay plugin recomputes/republishes it in
  ~/.dsh/remote/pairing.json.

.PARAMETER LanIp
  IPv4 address to put in the certificate SAN. Defaults to `network.lanIp` in
  `config/dshremote.settings.json`, then to the address of the interface that
  owns the default route.

.PARAMETER Force
  Overwrite existing PEM files.

.PARAMETER OpenSslPath
  Path to openssl.exe. Defaults to `tools.openSslPath` in
  `config/dshremote.settings.json`, then to openssl.exe on PATH.

.PARAMETER ValidityYears
  Certificate lifetime in years (default 5).
#>
[CmdletBinding()]
param(
  [string]$LanIp = '',
  [switch]$Force,
  [string]$OpenSslPath = '',
  [int]$ValidityYears = 5,
  [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# Machine-local values live in config/dshremote.settings.json (git-ignored).
# Empty LanIp still means "auto-detect the default-route IPv4".
. (Join-Path $PSScriptRoot 'config.ps1')
$settings = Get-DshRemoteSettings
if (-not $LanIp) { $LanIp = $settings.Network.LanIp }
if (-not $OpenSslPath) { $OpenSslPath = $settings.Tools.OpenSslPath }

$repoRoot = Split-Path -Parent $PSScriptRoot
$certDir = if ($OutDir) { $OutDir } else { Join-Path $env:USERPROFILE '.dsh\remote\certs' }
$certFile = Join-Path $certDir 'relay-cert.pem'
$keyFile = Join-Path $certDir 'relay-key.pem'

function Resolve-OpenSsl {
  param([string]$Preferred)
  if ($Preferred -and (Test-Path -LiteralPath $Preferred)) { return $Preferred }
  $cmd = Get-Command openssl.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  throw "openssl.exe not found. Looked at '$Preferred' and PATH. Install OpenSSL or pass -OpenSslPath <path>."
}

function Resolve-LanIp {
  try {
    $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 -ErrorAction Stop |
      Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1
    if ($route) {
      $addr = Get-NetIPAddress -InterfaceIndex $route.ifIndex -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -First 1
      if ($addr) { return $addr.IPAddress }
    }
  } catch { }
  $fallback = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -First 1
  if ($fallback) { return $fallback.IPAddress }
  throw 'No non-loopback IPv4 address found; pass -LanIp explicitly.'
}

# Keep only the PEM block(s), dropping OpenSSL's "Bag Attributes" comment lines.
function Get-PemBlock {
  param([string]$Text, [string]$Begin, [string]$End)
  $lines = $Text -split "`r?`n"
  $out = New-Object System.Collections.Generic.List[string]
  $inside = $false
  foreach ($line in $lines) {
    if ($line.Trim() -eq $Begin) { $inside = $true }
    if ($inside) { $out.Add($line) }
    if ($line.Trim() -eq $End) { $inside = $false }
  }
  return ($out -join "`n") + "`n"
}

function Invoke-OpenSsl {
  param([string[]]$Arguments)
  $output = & $openssl @Arguments 2>&1
  if ($LASTEXITCODE -ne 0) {
    throw ("openssl {0} failed ({1}):`n{2}" -f ($Arguments -join ' '), $LASTEXITCODE, ($output -join "`n"))
  }
  return $output
}

if (-not (Test-Path -LiteralPath $certDir)) {
  New-Item -ItemType Directory -Path $certDir -Force | Out-Null
}

if ((Test-Path -LiteralPath $certFile) -and (Test-Path -LiteralPath $keyFile) -and -not $Force) {
  Write-Host "TLS material already exists:"
  Write-Host "  cert: $certFile"
  Write-Host "  key : $keyFile"
  Write-Host 'Pass -Force to regenerate.'
  exit 0
}

$openssl = Resolve-OpenSsl -Preferred $OpenSslPath
if (-not $LanIp) { $LanIp = Resolve-LanIp }
if ($LanIp -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw "Invalid -LanIp '$LanIp' (expected an IPv4 literal)." }
Write-Host "OpenSSL  : $openssl"
Write-Host "LAN IP   : $LanIp"
Write-Host "Output   : $certDir"

$dnsNames = @('localhost', $env:COMPUTERNAME)
if ($env:COMPUTERNAME -and $env:COMPUTERNAME -ne $env:COMPUTERNAME.ToLower()) { $dnsNames += $env:COMPUTERNAME.ToLower() }
$sanParts = @()
foreach ($dns in ($dnsNames | Select-Object -Unique)) { $sanParts += "DNS=$dns" }
$sanParts += "IPAddress=$LanIp"
$sanParts += 'IPAddress=127.0.0.1'
$sanSpec = ($sanParts -join '&')

$pfxPasswordPlain = 'dsh-relay-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$pfxPassword = ConvertTo-SecureString -String $pfxPasswordPlain -Force -AsPlainText
$pfxPath = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-" + [guid]::NewGuid().ToString('N') + ".pfx")

$cert = $null
try {
  Write-Host "Creating self-signed certificate (SAN: $sanSpec) ..."
  $cert = New-SelfSignedCertificate `
    -Subject "CN=DSH Remote Relay ($env:COMPUTERNAME)" `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -TextExtension @("2.5.29.17={text}$sanSpec") `
    -NotAfter (Get-Date).AddYears($ValidityYears)

  Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPassword | Out-Null
  Write-Host "Exported PFX: $pfxPath"

  # OpenSSL 3 rejects some legacy PFX encodings unless -legacy is passed; try
  # the plain form first, then the compatibility form.
  $common = @('pkcs12', '-in', $pfxPath, '-passin', "pass:$pfxPasswordPlain")
  $certPemRaw = $null
  $keyPemRaw = $null
  try {
    $certPemRaw = (Invoke-OpenSsl -Arguments ($common + @('-clcerts', '-nokeys'))) -join "`n"
    $keyPemRaw = (Invoke-OpenSsl -Arguments ($common + @('-nocerts', '-nodes'))) -join "`n"
  } catch {
    Write-Host 'Retrying PFX conversion with -legacy (OpenSSL 3 legacy provider) ...'
    $legacyArgs = @('-legacy')
    $certPemRaw = (Invoke-OpenSsl -Arguments (($common + $legacyArgs) + @('-clcerts', '-nokeys'))) -join "`n"
    $keyPemRaw = (Invoke-OpenSsl -Arguments (($common + $legacyArgs) + @('-nocerts', '-nodes'))) -join "`n"
  }

  $certPem = Get-PemBlock -Text $certPemRaw -Begin '-----BEGIN CERTIFICATE-----' -End '-----END CERTIFICATE-----'
  $keyPem = Get-PemBlock -Text $keyPemRaw -Begin '-----BEGIN PRIVATE KEY-----' -End '-----END PRIVATE KEY-----'
  if ($keyPem -notmatch 'BEGIN PRIVATE KEY') {
    $keyPem = Get-PemBlock -Text $keyPemRaw -Begin '-----BEGIN RSA PRIVATE KEY-----' -End '-----END RSA PRIVATE KEY-----'
  }
  if ($certPem -notmatch 'BEGIN CERTIFICATE') { throw 'Failed to extract a PEM certificate from the PFX.' }
  if ($keyPem -match 'ENCRYPTED PRIVATE KEY') { throw 'Extracted private key is encrypted; expected -nodes output.' }

  Set-Content -LiteralPath $certFile -Value $certPem -NoNewline -Encoding ascii
  Set-Content -LiteralPath $keyFile -Value $keyPem -NoNewline -Encoding ascii

  # Restrict the private key to the current user (best effort).
  try {
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $keyFile /inheritance:r /grant:r "$($me):(R,W)" | Out-Null
  } catch { }

  # SPKI SHA-256: cert -> public key PEM -> DER -> sha256 hex.
  $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-spki-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
  try {
    $pubPem = Join-Path $tmpDir 'pub.pem'
    $spkiDer = Join-Path $tmpDir 'spki.der'
    $digestFile = Join-Path $tmpDir 'digest.txt'
    Invoke-OpenSsl -Arguments @('x509', '-in', $certFile, '-pubkey', '-noout', '-out', $pubPem) | Out-Null
    Invoke-OpenSsl -Arguments @('pkey', '-pubin', '-in', $pubPem, '-outform', 'DER', '-out', $spkiDer) | Out-Null
    Invoke-OpenSsl -Arguments @('dgst', '-sha256', '-hex', '-out', $digestFile, $spkiDer) | Out-Null
    $digestText = (Get-Content -LiteralPath $digestFile -Raw).Trim()
    $fp = ($digestText -replace '^.*=\s*', '').ToLowerInvariant()
  } finally {
    Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
  }

  Write-Host ''
  Write-Host 'TLS material ready:' -ForegroundColor Green
  Write-Host "  cert    : $certFile"
  Write-Host "  key     : $keyFile"
  Write-Host "  SAN     : localhost, $env:COMPUTERNAME, $LanIp, 127.0.0.1"
  Write-Host "  SPKI-SHA256: $fp"
  Write-Host ''
  Write-Host 'Next: pwsh -File scripts/dev-install.ps1  (installs/refreshes the relay plugin)'
} finally {
  if ($cert) {
    try { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force -ErrorAction SilentlyContinue } catch { }
  }
  if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue }
}

