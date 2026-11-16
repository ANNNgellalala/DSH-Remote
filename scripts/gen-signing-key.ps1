<#
.SYNOPSIS
  Generate the Android release signing keystore for DshRemote (local secret).

.DESCRIPTION
  Creates app/keys/release.keystore (PKCS#12) with keytool from the JDK used by
  the Android build. Passwords come from parameters or environment variables:

    DSHREMOTE_KEYSTORE_PASS   store password (also used as key password for PKCS12)
    DSHREMOTE_KEY_PASS        optional separate key password (PKCS12 warns/ignores)

  With no password supplied, a random one is generated and printed ONCE — save it
  in your password manager / CI secret store. The keystore is git-ignored;
  never commit it and never reuse it across unrelated apps.

.PARAMETER KeystorePath
  Output keystore (default: app/keys/release.keystore).

.PARAMETER Alias
  Key alias (default: dshremote).

.PARAMETER StorePass
  Store password. Falls back to DSHREMOTE_KEYSTORE_PASS, then a generated one.

.PARAMETER KeyPass
  Key password. Falls back to DSHREMOTE_KEY_PASS, then the store password.

.PARAMETER Dname
  Distinguished name.

.PARAMETER ValidityDays
  Certificate validity in days (default 10000 ≈ 27 years; Android requires the
  signing key to outlive every app update).

.PARAMETER JavaSdkDirectory
  JDK containing bin/keytool.exe (default: JAVA_HOME, then C:\Program Files\Java\jdk-21).

.PARAMETER Force
  Overwrite an existing keystore (old APK signatures become non-upgradable).

.EXAMPLE
  pwsh -File scripts/gen-signing-key.ps1

.EXAMPLE
  $env:DSHREMOTE_KEYSTORE_PASS = '...'; pwsh -File scripts/gen-signing-key.ps1 -Force
#>
[CmdletBinding()]
param(
  [string]$KeystorePath,
  [string]$Alias = 'dshremote',
  [string]$StorePass = $env:DSHREMOTE_KEYSTORE_PASS,
  [string]$KeyPass = $env:DSHREMOTE_KEY_PASS,
  [string]$Dname = 'CN=DSH Remote, OU=Mobile, O=DSHRemote, L=Shanghai, C=CN',
  [int]$ValidityDays = 10000,
  [string]$JavaSdkDirectory = $env:JAVA_HOME,
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $KeystorePath) { $KeystorePath = Join-Path $repoRoot 'app\keys\release.keystore' }
if (-not $JavaSdkDirectory -and (Test-Path 'C:\Program Files\Java\jdk-21')) { $JavaSdkDirectory = 'C:\Program Files\Java\jdk-21' }
if (-not $JavaSdkDirectory) { throw 'JDK not found: pass -JavaSdkDirectory or set JAVA_HOME.' }
$keytool = Join-Path $JavaSdkDirectory 'bin\keytool.exe'
if (-not (Test-Path -LiteralPath $keytool)) { throw "keytool not found: $keytool" }

$generated = $false
if (-not $StorePass) {
  $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
  $chars = New-Object char[] 28
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  $bytes = New-Object byte[] 28
  $rng.GetBytes($bytes)
  for ($i = 0; $i -lt $chars.Length; $i++) { $chars[$i] = $alphabet[$bytes[$i] % $alphabet.Length] }
  $rng.Dispose()
  $StorePass = -join $chars
  $generated = $true
}
if (-not $KeyPass) { $KeyPass = $StorePass }

$keyDir = Split-Path -Parent $KeystorePath
if (-not (Test-Path -LiteralPath $keyDir)) { New-Item -ItemType Directory -Path $keyDir -Force | Out-Null }

if ((Test-Path -LiteralPath $KeystorePath) -and -not $Force) {
  Write-Host "Keystore already exists: $KeystorePath" -ForegroundColor Yellow
  Write-Host 'Refusing to overwrite (existing APKs would become non-upgradable). Pass -Force to replace.'
  Write-Host ''
  Write-Host 'List the current key with:'
  Write-Host "  `"$keytool`" -list -v -keystore `"$KeystorePath`" -alias $Alias"
  Write-Host 'If you lost the password, generate a NEW keystore and plan an app uninstall/reinstall.'
  exit 0
}

Write-Host "keytool  : $keytool"
Write-Host "keystore : $KeystorePath"
Write-Host "alias    : $Alias"
Write-Host "dname    : $Dname"
Write-Host "validity : $ValidityDays days"

$arguments = @(
  '-J-Duser.language=en',
  '-J-Duser.country=US',
  '-genkeypair',
  '-v',
  '-keystore', $KeystorePath,
  '-alias', $Alias,
  '-keyalg', 'RSA',
  '-keysize', '4096',
  '-validity', "$ValidityDays",
  '-storetype', 'PKCS12',
  '-storepass', $StorePass,
  '-keypass', $KeyPass,
  '-dname', $Dname
)
& $keytool @arguments
if ($LASTEXITCODE -ne 0) { throw "keytool failed with exit code $LASTEXITCODE" }

# Best-effort permission hardening: owner read/write only.
try {
  $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
  & icacls.exe $KeystorePath /inheritance:r /grant:r "$($me):(R,W)" | Out-Null
} catch {
  Write-Warning "Could not restrict keystore ACLs: $($_.Exception.Message)"
}

$hash = (Get-FileHash -LiteralPath $KeystorePath -Algorithm SHA256).Hash.ToLowerInvariant()
$keystoreSize = (Get-Item -LiteralPath $KeystorePath).Length
Write-Host ''
Write-Host 'Signing key ready:' -ForegroundColor Green
Write-Host "  keystore : $KeystorePath ($keystoreSize bytes)"
Write-Host "  alias    : $Alias"
Write-Host "  sha256   : $hash"
Write-Host ''
Write-Host 'Save the password now (only shown if it was generated):'
if ($generated) {
  Write-Host "  store/key password : $StorePass" -ForegroundColor Yellow
} else {
  Write-Host '  store/key password : (from parameter/environment)'
}
Write-Host ''
Write-Host 'Use it for the Release build:'
Write-Host '  $env:DSHREMOTE_KEYSTORE_PASS = ''<password>'''
Write-Host '  pwsh -File scripts/build-release-apk.ps1'
Write-Host ''
Write-Host 'The keystore is git-ignored (app/keys/). For CI, store the keystore + password as secrets.'

