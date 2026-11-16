<#
.SYNOPSIS
  Install or upgrade the dsh-remote-relay host plugin from a distribution zip
  (or from this repository's relay/ directory) into a DSH home.

.DESCRIPTION
  Distribution flow (M4):
    1. stage the plugin files into <DSH_HOME>\plugins\dsh-remote-relay
    2. ensure TLS material (reuse existing, copy the repo certs, or generate)
    3. junction <DSH_HOME>\profiles\<profile>\node_modules\dsh-remote-relay
       -> the plugin directory
    4. write the managed insert block in the profile cordis.patch.yml (backup first)
    5. trigger HMR and wait for /__relay/health with a passing self-check

  Idempotent: rerunning upgrades in place, keeps a patch backup, and reuses the
  installed certificate (so the phone's pinned fingerprint survives relay
  upgrades). For the repository development loop use scripts/dev-install.ps1,
  which junctions straight to the working tree.

.PARAMETER FromZip
  Path to dsh-remote-relay-<version>.zip produced by scripts/package-relay.ps1.

.PARAMETER SourceDir
  Install directly from a plugin directory (default: this repo's relay/).

.PARAMETER BindHost
  LAN/VPN IPv4 the relay binds. Defaults to the default-route interface address.

.PARAMETER Port
  Relay TLS port (default 8443, must match the existing relay config).

.PARAMETER Profile
  DSH profile to install into (default: desktop).

.PARAMETER DshHome
  Override DSH_HOME (default: $env:DSH_HOME or ~/.dsh).

.PARAMETER CertFile
  Existing PEM certificate to reuse instead of the installed/own certificate.

.PARAMETER KeyFile
  Matching unencrypted PEM private key.

.PARAMETER NoReload
  Write files but do not wait for the hot-reloaded relay.

.PARAMETER NoJunction
  Skip creating the profile junction (isolated staging/tests).

.PARAMETER NoPatch
  Skip editing the profile cordis.patch.yml (isolated staging/tests).

.PARAMETER TimeoutSeconds
  How long to wait for the hot-reloaded relay (default 60).

.EXAMPLE
  pwsh -File scripts/install-relay.ps1 -FromZip .\dist\dsh-remote-relay-0.2.0.zip

.EXAMPLE
  pwsh -File scripts/install-relay.ps1 -WhatIf -FromZip .\dist\dsh-remote-relay-0.2.0.zip
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [string]$FromZip,
  [string]$SourceDir,
  [Alias('Host')]
  [string]$BindHost,
  [int]$Port = 8443,
  [string]$PublicHost,
  [int]$PublicPort = 0,
  [string]$Profile = 'desktop',
  [string]$DshHome,
  [string]$CertFile,
  [string]$KeyFile,
  [switch]$NoReload,
  [switch]$NoJunction,
  [switch]$NoPatch,
  [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'relay-common.ps1')

if ($FromZip -and $SourceDir) { throw 'Pass either -FromZip or -SourceDir, not both.' }
if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot 'relay' }

$paths = Get-RelayProfilePaths -DshHome $DshHome -Profile $Profile
$pluginRoot = $paths.PluginRoot
$linkDir = Join-Path $paths.NodeModules 'dsh-remote-relay'
if (-not $BindHost) { $BindHost = Resolve-LanIp }
if ($BindHost -in @('0.0.0.0', '::', '*')) { throw "Refusing wildcard bind host '$BindHost'; pass a concrete LAN/VPN IPv4." }

$stageDir = $null
try {
  # ---- resolve source -------------------------------------------------------
  if ($FromZip) {
    $zipPath = (Resolve-Path -LiteralPath $FromZip).Path
    if (-not (Test-Path -LiteralPath $zipPath)) { throw "Zip not found: $zipPath" }
    $stageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-relay-install-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $zipPath -DestinationPath $stageDir -Force -WhatIf:$false
    $manifestHit = Get-ChildItem -LiteralPath $stageDir -Recurse -Filter 'package.json' -File |
      Where-Object { $_.FullName -notmatch '\\node_modules\\' } |
      Where-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).name -eq 'dsh-remote-relay' } |
      Select-Object -First 1
    if (-not $manifestHit) { throw "No dsh-remote-relay/package.json found inside $zipPath" }
    $source = $manifestHit.Directory.FullName
    Write-Host "Source   : zip $zipPath"
  } else {
    $source = (Resolve-Path -LiteralPath $SourceDir).Path
    $sourceManifest = Join-Path $source 'package.json'
    if (-not (Test-Path -LiteralPath $sourceManifest)) { throw "Plugin manifest not found: $sourceManifest" }
    $sourcePkg = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
    if ($sourcePkg.name -ne 'dsh-remote-relay') { throw "Unexpected plugin name '$($sourcePkg.name)' in $sourceManifest" }
    Write-Host "Source   : dir $source"
  }
  $srcManifest = Join-Path $source 'package.json'
  $srcPkg = Get-Content -LiteralPath $srcManifest -Raw | ConvertFrom-Json
  $srcIndex = Join-Path $source 'src\index.mjs'
  if (-not (Test-Path -LiteralPath $srcIndex)) { throw "Plugin entry not found: $srcIndex" }
  Write-Host "Version  : $($srcPkg.version)"
  Write-Host "DSH home : $($paths.DshHome)  (profile: $Profile)"
  Write-Host "Install  : $pluginRoot"
  Write-Host "Relay    : https://${BindHost}:${Port}"

  # ---- 1) stage plugin files ------------------------------------------------
  if ($PSCmdlet.ShouldProcess($pluginRoot, "stage dsh-remote-relay $($srcPkg.version)")) {
    if (Test-Path -LiteralPath $pluginRoot) {
      $existing = Get-Item -LiteralPath $pluginRoot -Force
      if ($existing.LinkType) { throw "Refusing to replace link at plugin path: $pluginRoot" }
      if (-not $existing.PSIsContainer) { throw "Plugin path is not a directory: $pluginRoot" }
      # Remove only our own plugin directory; never traverse a junction elsewhere.
      Remove-Item -LiteralPath $pluginRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $pluginRoot 'src') -Force | Out-Null
    Copy-Item -LiteralPath $srcManifest -Destination (Join-Path $pluginRoot 'package.json')
    $srcBundlePatch = Join-Path $source 'cordis.patch.yml'
    if (Test-Path -LiteralPath $srcBundlePatch) { Copy-Item -LiteralPath $srcBundlePatch -Destination (Join-Path $pluginRoot 'cordis.patch.yml') }
    $srcReadme = Join-Path $source 'README.zh.md'
    if (Test-Path -LiteralPath $srcReadme) { Copy-Item -LiteralPath $srcReadme -Destination (Join-Path $pluginRoot 'README.zh.md') }
    $srcInstall = Join-Path $source 'INSTALL.md'
    if (Test-Path -LiteralPath $srcInstall) { Copy-Item -LiteralPath $srcInstall -Destination (Join-Path $pluginRoot 'INSTALL.md') }
    foreach ($module in (Get-ChildItem -LiteralPath (Join-Path $source 'src') -Filter '*.mjs' -File)) {
      Copy-Item -LiteralPath $module.FullName -Destination (Join-Path $pluginRoot "src\$($module.Name)")
    }
    $sourceAssets = Join-Path $source 'assets'
    if (Test-Path -LiteralPath $sourceAssets) {
      Copy-Item -LiteralPath $sourceAssets -Destination (Join-Path $pluginRoot 'assets') -Recurse
    }
    Write-Host "Staged $((Get-ChildItem -LiteralPath (Join-Path $pluginRoot 'src') -Filter '*.mjs' -File).Count) module(s) into $pluginRoot" -ForegroundColor Green
  }

  # ---- 2) TLS material ------------------------------------------------------
  $certPath = $null
  $keyPath = $null
  if ($CertFile -and $KeyFile) {
    $certPath = (Resolve-Path -LiteralPath $CertFile).Path
    $keyPath = (Resolve-Path -LiteralPath $KeyFile).Path
    Write-Host "TLS      : explicit $certPath"
  } else {
    $certCandidate = Join-Path $pluginRoot 'certs\relay-cert.pem'
    $keyCandidate = Join-Path $pluginRoot 'certs\relay-key.pem'
    if ((Test-Path -LiteralPath $certCandidate) -and (Test-Path -LiteralPath $keyCandidate)) {
      $certPath = $certCandidate
      $keyPath = $keyCandidate
      Write-Host "TLS      : reusing installed certificate (keeps the phone's pinned fp)"
    } else {
      # Prefer the repository certs when present so an upgrade of an existing
      # pairing does not change the pinned fingerprint.
      $repoCert = Join-Path $repoRoot 'relay\certs\relay-cert.pem'
      $repoKey = Join-Path $repoRoot 'relay\certs\relay-key.pem'
      if ((Test-Path -LiteralPath $repoCert) -and (Test-Path -LiteralPath $repoKey)) {
        if ($PSCmdlet.ShouldProcess($pluginRoot, 'copy repository TLS material')) {
          New-Item -ItemType Directory -Path (Join-Path $pluginRoot 'certs') -Force | Out-Null
          Copy-Item -LiteralPath $repoCert -Destination $certCandidate
          Copy-Item -LiteralPath $repoKey -Destination $keyCandidate
          Write-Host 'TLS      : copied repository certificate (keeps the phone-pinned fp)'
        }
        $certPath = $certCandidate
        $keyPath = $keyCandidate
      } else {
        if ($PSCmdlet.ShouldProcess($pluginRoot, 'generate self-signed TLS material')) {
          & (Join-Path $PSScriptRoot 'gen-tls.ps1') -LanIp $BindHost -OutDir (Join-Path $pluginRoot 'certs')
          if ($LASTEXITCODE -ne 0) { throw 'gen-tls.ps1 failed.' }
        }
        $certPath = $certCandidate
        $keyPath = $keyCandidate
      }
    }
  }
  if ($certPath -and (Test-Path -LiteralPath $certPath)) {
    try {
      # Decode the PEM body and load DER directly: X509Certificate2's PEM
      # helpers are picky about line endings/trailing bytes.
      $pemText = [System.IO.File]::ReadAllText($certPath)
      $pemMatch = [regex]::Match($pemText, '(?s)-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----')
      if (-not $pemMatch.Success) { throw 'no PEM CERTIFICATE block found' }
      $der = [Convert]::FromBase64String(($pemMatch.Groups[1].Value -replace '\s', ''))
      $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($der)
      $spki = $certificate.PublicKey.ExportSubjectPublicKeyInfo()
      $fp = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki)).ToLowerInvariant()
      Write-Host "TLS fp   : $fp"
    } catch {
      Write-Warning "Could not compute certificate fingerprint: $($_.Exception.Message)"
    }
  }

  # ---- 3) profile junction --------------------------------------------------
  if (-not $NoJunction) {
    if (-not (Test-Path -LiteralPath $paths.NodeModules)) {
      Write-Warning "Profile node_modules not found, skipping junction: $($paths.NodeModules)"
    } elseif ($PSCmdlet.ShouldProcess($linkDir, "junction -> $pluginRoot")) {
      $junction = Set-RelayJunction -LinkPath $linkDir -TargetDir $pluginRoot
      if ($junction.Changed) { Write-Host "Junction : created $linkDir -> $($junction.Target)" }
      else { Write-Host "Junction : already correct $linkDir -> $($junction.Target)" }
    }
  }

  # ---- 4) profile patch block ----------------------------------------------
  if (-not $NoPatch) {
    if (-not (Test-Path -LiteralPath $paths.PatchFile)) {
      Write-Warning "Profile patch file not found, skipping patch block: $($paths.PatchFile)"
    } elseif ($PSCmdlet.ShouldProcess($paths.PatchFile, 'write managed relay insert block')) {
      $before = Get-RelayHealth -TargetHost $BindHost -TargetPort $Port
      $previousStartedAt = if ($before) { [string]$before.startedAt } else { $null }
      $result = Set-RelayPatchBlock -PatchFile $paths.PatchFile -BindHost $BindHost -Port $Port -CertFile $certPath -KeyFile $keyPath -PublicHost $PublicHost -PublicPort $PublicPort
      Write-Host "Patch    : $($paths.PatchFile)"
      Write-Host "Backup   : $($result.Backup)"

      if (-not $NoReload) {
        $processes = @(Get-DesktopHostProcesses)
        if ($processes.Count -eq 0) {
          Write-Warning 'No running DeepSeek Harness desktop process detected; the plugin loads on next start.'
        } else {
          Write-Host 'Waiting for the hot-reloaded relay self-check ...'
          $wait = Wait-RelayHealth -TargetHost $BindHost -TargetPort $Port -TimeoutSeconds $TimeoutSeconds -PreviousStartedAt $previousStartedAt
          if (-not $wait.Healthy) {
            Write-Warning "Relay did not answer at https://${BindHost}:${Port}/__relay/health within ${TimeoutSeconds}s."
            Write-Host 'Check the desktop log for [remote-relay] lines (HMR, cert path, port in use, engines.dsh).'
            exit 1
          }
          $health = $wait.Health
          # The listener answers before the self-check finishes: wait for the probe verdict.
          $probeDeadline = (Get-Date).AddSeconds(15)
          while ((Get-Date) -lt $probeDeadline -and $health.protocolProbe -and $health.protocolProbe.status -eq 'pending') {
            Start-Sleep -Milliseconds 500
            $refreshed = Get-RelayHealth -TargetHost $BindHost -TargetPort $Port
            if ($refreshed) { $health = $refreshed }
          }
          $probeOk = $health.protocolProbe -and $health.protocolProbe.ok -eq $true
          $capSummary = if ($health.protocolProbe -and $health.protocolProbe.capabilities) {
            ($health.protocolProbe.capabilities.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
          } else { '(no probe)' }
          Write-Host "Relay    : healthy $($health.relayVersion) on DSH $($health.dshVersion); self-check $(if ($probeOk) { 'OK' } else { 'FAILED' })" -ForegroundColor $(if ($probeOk) { 'Green' } else { 'Yellow' })
          Write-Host "           $capSummary"
          if ($wait.Reloaded) { Write-Host 'Hot reload: new plugin instance is serving.' -ForegroundColor Green }
          else { Write-Host 'Note: relay did not appear to restart (config/module identical); re-run without -NoReload after changing versions.' -ForegroundColor Yellow }
          if (-not $probeOk) {
            Write-Warning 'Self-check failed: pairing.json is written with active=false. Fix compatibility before pairing a phone.'
            exit 1
          }
        }
      }
    }
  }

  # ---- 5) summary -----------------------------------------------------------
  $pairing = $null
  if (Test-Path -LiteralPath $paths.PairingFile) {
    try { $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json } catch {}
  }
  Write-Host ''
  Write-Host 'Install summary:' -ForegroundColor Green
  Write-Host "  plugin : $pluginRoot (v$($srcPkg.version))"
  Write-Host "  profile: $($paths.ProfileDir)"
  Write-Host "  relay  : https://${BindHost}:${Port}"
  if ($pairing) {
    Write-Host "  pairing: $($paths.PairingFile) (active=$($pairing.active), updatedAt=$($pairing.updatedAt))"
    Write-Host "  verify : pwsh -File scripts/relay-smoke.ps1 -IncludeWs"
  }
  if ($WhatIfPreference) { Write-Host '  (WhatIf: no changes were made)' -ForegroundColor Yellow }
} finally {
  if ($stageDir -and (Test-Path -LiteralPath $stageDir)) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}



