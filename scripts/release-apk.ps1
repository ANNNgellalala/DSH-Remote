<#
.SYNOPSIS
  Standard DSH Remote release: build + sign the APK, publish it to the VPS, verify the download.

.DESCRIPTION
  One command for the release flow that is now mandatory for every build handed to a
  phone:

    1. build a signed Release APK (arm64, SdkOnly) with build-release-apk.ps1
    2. upload it to the VPS resource centre category `dsh-remote`
       (gateway keeps the newest 3 versions)
    3. verify the gateway's sha256 and a fresh HTTPS read-back against the local file

  The signing password comes from -StorePass, DSHREMOTE_KEYSTORE_PASS, or the
  local development file app/keys/local-password.txt. Never commit that file.

  Use -NoPublish only for a local/offline build (debugging, emulator work); a
  build that is meant to be installed on the phone must be published.

.PARAMETER DisplayVersion
  versionName shown by Android/About (e.g. 1.8.0).

.PARAMETER VersionCode
  Monotonically increasing versionCode (e.g. 40).

.PARAMETER StorePass
  Keystore password. Defaults to DSHREMOTE_KEYSTORE_PASS or app/keys/local-password.txt.

.PARAMETER LinkMode
  AndroidLinkMode (SdkOnly default; None is the fallback when a trimmed build crashes).

.PARAMETER NoPublish
  Build only; skip the upload + read-back verification.

.PARAMETER NoKeepVersions
  Tell the gateway to drop all other versions of the category (default keeps 3).

.EXAMPLE
  pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.8.0 -VersionCode 40

.EXAMPLE
  pwsh -File scripts/release-apk.ps1 -DisplayVersion 1.9.0 -VersionCode 41 -LinkMode None
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$DisplayVersion,

  [Parameter(Mandatory = $true)]
  [string]$VersionCode,

  [string]$StorePass,
  [ValidateSet('None', 'SdkOnly', 'Full')]
  [string]$LinkMode = 'SdkOnly',
  [string]$OutputDir,
  [switch]$NoSignVerify,
  [switch]$NoKeepVersions,
  [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $PSScriptRoot 'build-release-apk.ps1'
$passwordFile = Join-Path $repoRoot 'app/keys/local-password.txt'

if (-not $StorePass) { $StorePass = $env:DSHREMOTE_KEYSTORE_PASS }
if (-not $StorePass -and (Test-Path -LiteralPath $passwordFile)) {
  $StorePass = (Get-Content -LiteralPath $passwordFile -Raw).Trim()
}
if (-not $StorePass) {
  throw 'Missing keystore password: pass -StorePass, set DSHREMOTE_KEYSTORE_PASS, or create app/keys/local-password.txt.'
}
if (-not (Test-Path -LiteralPath $buildScript)) { throw "build script not found: $buildScript" }

$arguments = @{
  StorePass       = $StorePass
  DisplayVersion  = $DisplayVersion
  VersionCode     = $VersionCode
  LinkMode        = $LinkMode
  NoKeepVersions  = $NoKeepVersions
  Publish         = (-not $NoPublish)
}
if ($OutputDir) { $arguments.OutputDir = $OutputDir }
if ($NoSignVerify) { $arguments.NoSignVerify = $true }

Write-Host ''
Write-Host 'DSH Remote standard release' -ForegroundColor Cyan
Write-Host "  version : $DisplayVersion ($VersionCode)"
Write-Host "  publish : $([bool]$arguments.Publish)"
Write-Host ''

& $buildScript @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($arguments.Publish) {
  Write-Host ''
  Write-Host 'Standard release complete: APK published and read-back verified.' -ForegroundColor Green
} else {
  Write-Host ''
  Write-Host 'Local-only build complete (-NoPublish): not uploaded to the VPS.' -ForegroundColor Yellow
}
