<#
.SYNOPSIS
  Publish release APK(s) to the VPS gateway resource centre (upload API).

.DESCRIPTION
  Thin wrapper around scripts/upload-resource.ps1: it only selects the local
  APK file(s) and then calls the upload script once per file. The gateway (not
  this script) does the sha256 check, the HTTPS read-back verification and the
  keepVersions retention policy -- no scp any more.

  Default source: <repo>/dist/apk, files matching
  dshremote-<version>-<code>-arm64-release.apk (latest versionCode wins).

  Default: upload the newest release APK to -Id dsh-remote with keepVersions
  enabled, so the gateway keeps the newest 3 versions of that category.
  -All uploads every APK in versionCode ascending order: with keepVersions the
  last three uploads are the newest three, which is what stays on the VPS.

.PARAMETER Id
  Resource category id used for the upload; one of
  dsh-remote (default) / smartbill / relay / docs.

.PARAMETER NoKeepVersions
  Ask the gateway to keep only the version just uploaded (keepVersions=0,
  the other files of the id are deleted). Default is keepVersions enabled.

.EXAMPLE
  pwsh -File scripts/sync-apk.ps1
  pwsh -File scripts/sync-apk.ps1 -All
  pwsh -File scripts/sync-apk.ps1 -File dist\apk\dshremote-1.1.0-12-arm64-release.apk
  pwsh -File scripts/sync-apk.ps1 -File dshremote-1.1.0-12-arm64-release.apk
  pwsh -File scripts/sync-apk.ps1 -Id smartbill -File dist\apk\smartbill-1.0.0-1-universal-debugsign.apk
  pwsh -File scripts/sync-apk.ps1 -NoKeepVersions
#>
[CmdletBinding()]
param(
  [ValidateSet('dsh-remote', 'smartbill', 'relay', 'docs')]
  [string]$Id = 'dsh-remote',
  [string]$SshHost = '',
  [int]$SshPort = 0,
  [string]$User = '',
  [string]$KeyFile = '',
  [string]$LocalDir = '',
  [string]$File = '',
  [switch]$All,
  [string]$PublicHost = '',
  [int]$PublicPort = 0,
  [switch]$KeepVersions = $true,
  [switch]$NoKeepVersions,
  [switch]$SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Machine-local deployment values live in config/dshremote.settings.json (git-ignored).
. (Join-Path $PSScriptRoot 'config.ps1')
$settings = Get-DshRemoteSettings
if (-not $SshHost) { $SshHost = $settings.Deploy.SshHost }
if ($SshPort -le 0) { $SshPort = $settings.Deploy.SshPort }
if (-not $User) { $User = $settings.Deploy.SshUser }
if (-not $KeyFile) { $KeyFile = $settings.Deploy.KeyFile }
if (-not $PublicHost) { $PublicHost = $settings.Deploy.PublicHost }
if ($PublicPort -le 0) { $PublicPort = $settings.Deploy.PublicPort }

$repoRoot = Split-Path -Parent $PSScriptRoot
if ($LocalDir -eq '') { $LocalDir = Join-Path $repoRoot 'dist\apk' }
$publicBase = "https://${PublicHost}:${PublicPort}"
$uploadScript = Join-Path $PSScriptRoot 'upload-resource.ps1'
$keepVersions = (-not $NoKeepVersions) -and $KeepVersions

function Write-Head([string]$text) {
  Write-Host ''
  Write-Host "==> $text" -ForegroundColor Cyan
}
function Write-Info([string]$text) { Write-Host "    $text" }
function Fail([string]$message) {
  Write-Host "ERROR: $message" -ForegroundColor Red
  exit 1
}

function Get-ApkMeta {
  param([System.IO.FileInfo]$Item)
  $meta = [pscustomobject]@{
    File        = $Item
    Version     = $Item.Name
    VersionCode = -1
    Timestamp   = $Item.LastWriteTimeUtc.Ticks
  }
  if ($Item.Name -match '^dshremote-(?<version>.+)-(?<code>\d+)-arm64-release\.apk$') {
    $meta.Version = $Matches['version']
    $meta.VersionCode = [int]$Matches['code']
  } elseif ($Item.Name -match '^[A-Za-z0-9][A-Za-z0-9._-]*?-(?<version>\d+(?:\.\d+)*)-(?<code>\d+)-[A-Za-z0-9._-]+\.apk$') {
    # Other ids use the same convention, e.g.
    # smartbill-1.0.0-7-universal-debugsign.apk; keeps -All sorted by versionCode.
    $meta.Version = $Matches['version']
    $meta.VersionCode = [int]$Matches['code']
  }
  return $meta
}

if (-not (Test-Path -LiteralPath $uploadScript)) { Fail "upload script not found: $uploadScript" }
if (-not (Test-Path -LiteralPath $LocalDir)) { Fail "local APK directory not found: $LocalDir (run scripts/build-release-apk.ps1 first)" }

# Resolve the running pwsh so the upload script runs in the same interpreter even
# when pwsh is not on PATH (for example when started from a GUI shortcut).
$pwshExe = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwshExe) { $pwshExe = (Get-Process -Id $PID).Path }
if (-not $pwshExe) { Fail 'cannot resolve pwsh; run scripts/upload-resource.ps1 directly' }

$allApks = @(Get-ChildItem -LiteralPath $LocalDir -File -Filter '*.apk' | ForEach-Object { Get-ApkMeta $_ })
if ($allApks.Count -eq 0) { Fail "no *.apk in $LocalDir (build one with scripts/build-release-apk.ps1)" }
$sortedDesc = @($allApks | Sort-Object -Property @{ Expression = 'VersionCode'; Descending = $true }, @{ Expression = 'Timestamp'; Descending = $true })
# -All uploads oldest first so keepVersions leaves the newest 3 versions behind.
$sortedAsc = @($allApks | Sort-Object -Property @{ Expression = 'VersionCode'; Descending = $false }, @{ Expression = 'Timestamp'; Descending = $false })

$selected = @()
if ($File -ne '') {
  $candidate = $File
  if (-not (Test-Path -LiteralPath $candidate)) { $candidate = Join-Path $LocalDir $File }
  if (-not (Test-Path -LiteralPath $candidate)) { Fail "APK not found: $File (looked in the current directory and $LocalDir)" }
  $item = Get-Item -LiteralPath $candidate
  $selected = @(Get-ApkMeta $item)
} elseif ($All) {
  $selected = $sortedAsc
} else {
  $selected = @($sortedDesc[0])
}

Write-Head 'Plan'
Write-Info "local       : $LocalDir"
Write-Info "category    : $Id"
Write-Info "keepVersions: $(if ($keepVersions) { 'on (gateway keeps the newest 3 versions)' } else { 'off (only the uploaded version stays)' })"
Write-Info "public URL  : $publicBase/__gateway/download"
foreach ($meta in $selected) {
  Write-Info ("upload      : {0} ({1:N2} MiB, version {2}, code {3})" -f $meta.File.Name, ($meta.File.Length / 1MB), $meta.Version, $meta.VersionCode)
}

$failed = @()
$uploaded = @()
foreach ($meta in $selected) {
  Write-Head "Publish $($meta.File.Name) -> $Id"
  $uploadArgs = @(
    '-NoProfile', '-File', $uploadScript,
    '-Id', $Id,
    '-File', $meta.File.FullName,
    '-SshHost', $SshHost,
    '-SshPort', "$SshPort",
    '-User', $User,
    '-KeyFile', $KeyFile,
    '-PublicHost', $PublicHost,
    '-PublicPort', "$PublicPort"
  )
  if ($keepVersions) { $uploadArgs += '-KeepVersions' }
  if ($SkipVerify) { $uploadArgs += '-SkipVerify' }
  & $pwshExe @uploadArgs
  if ($LASTEXITCODE -ne 0) {
    Write-Host "    upload failed (scripts/upload-resource.ps1 exit $LASTEXITCODE)" -ForegroundColor Red
    $failed += $meta.File.Name
    continue
  }
  $uploaded += $meta.File.Name
}

if ($failed.Count -gt 0) { Fail "failed to sync: $($failed -join ', ')" }

if (-not $SkipVerify) {
  Write-Head 'Verify gateway'
  $identityJson = & curl.exe -k -s --max-time 20 "$publicBase/__gateway/identity" 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $identityJson) {
    Write-Host "    WARNING: $publicBase/__gateway/identity did not answer; run scripts/deploy-gateway.ps1 first" -ForegroundColor Yellow
  } else {
    try {
      $identity = $identityJson | ConvertFrom-Json
      Write-Info "gateway     : $($identity.name) v$($identity.version), tunnels=$($identity.tunnels)"
      Write-Info "fp          : $($identity.fp)"
      if ($identity.PSObject.Properties['download'] -and $identity.download) {
        Write-Info "apk files   : count=$($identity.download.count)"
      }
      if ($identity.PSObject.Properties['resources'] -and $identity.resources -and $identity.resources.PSObject.Properties['categories']) {
        $categorySummary = (@($identity.resources.categories) | ForEach-Object { "$($_.id)=$($_.count)" }) -join ', '
        Write-Info "categories  : total=$($identity.resources.total) ($categorySummary)"
        $mine = @($identity.resources.categories | Where-Object { $_.id -eq $Id })[0]
        if ($mine) {
          $latestFile = if ($mine.PSObject.Properties['latest'] -and $mine.latest) { $mine.latest.file } else { '(none)' }
          Write-Info "  $Id : $($mine.count) file(s), latest=$latestFile"
        }
      }
    } catch {
      Write-Host '    WARNING: identity endpoint returned non-JSON' -ForegroundColor Yellow
    }
  }
}

Write-Head 'Done'
Write-Info "page        : $publicBase/__gateway/download"
Write-Info "index       : $publicBase/__gateway/resources?id=$Id"
Write-Info "category    : $Id ($(if ($keepVersions) { 'keepVersions=1' } else { 'keepVersions=0' }))"
Write-Info "uploaded    : $(if ($uploaded.Count -gt 0) { $uploaded -join ', ' } else { '(none)' })"
