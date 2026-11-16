<#
.SYNOPSIS
  Uninstall the dsh-remote-relay host plugin from a DSH home.

.DESCRIPTION
  Removes only what this project installed:
    1. the managed insert block in the profile cordis.patch.yml (backup first;
       HMR then stops the running plugin and releases the relay port)
    2. the profile junction node_modules/dsh-remote-relay
    3. optionally the staged plugin directory and the pairing file

  Idempotent: safe to run when the plugin is already gone. Other plugins and
  unrelated patch content are never touched. Development installs created by
  scripts/dev-install.ps1 are also handled (same managed block).

.PARAMETER DshHome
  Override DSH_HOME (default: $env:DSH_HOME or ~/.dsh).

.PARAMETER Profile
  DSH profile to clean (default: desktop).

.PARAMETER RemovePluginDir
  Also delete <DSH_HOME>\plugins\dsh-remote-relay (the staged distribution copy).

.PARAMETER RemovePairing
  Also delete <DSH_HOME>\remote\pairing.json (the phone pairing token).

.PARAMETER RestoreBackup
  Restore the newest cordis.patch.yml backup instead of editing the current file
  (the relay block is stripped from the restored copy). Use with care: a backup
  can predate unrelated patch edits.

.PARAMETER NoReload
  Do not wait for the hot-reloaded desktop to drop the relay listener.

.PARAMETER TimeoutSeconds
  How long to wait for the listener/relay to disappear (default 30).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [string]$DshHome,
  [string]$Profile = 'desktop',
  [switch]$RemovePluginDir,
  [switch]$RemovePairing,
  [switch]$RestoreBackup,
  [switch]$NoReload,
  [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'relay-common.ps1')

$paths = Get-RelayProfilePaths -DshHome $DshHome -Profile $Profile
$linkDir = Join-Path $paths.NodeModules 'dsh-remote-relay'
$changes = New-Object System.Collections.Generic.List[string]

# Capture the relay address before we forget about it (for the shutdown check).
$relayHost = $null
$relayPort = 8443
if (Test-Path -LiteralPath $paths.PairingFile) {
  try {
    $pairing = Get-Content -LiteralPath $paths.PairingFile -Raw | ConvertFrom-Json
    if ($pairing.host) { $relayHost = [string]$pairing.host }
    if ($pairing.port) { $relayPort = [int]$pairing.port }
  } catch {}
}

Write-Host "DSH home : $($paths.DshHome)  (profile: $Profile)"
Write-Host "Patch    : $($paths.PatchFile)"
Write-Host "Junction : $linkDir"

# ---- 1) patch block ---------------------------------------------------------
if (Test-Path -LiteralPath $paths.PatchFile) {
  if ($RestoreBackup) {
    $backup = Get-ChildItem -LiteralPath (Split-Path -Parent $paths.PatchFile) -Filter 'cordis.patch.yml.bak-*' -File -ErrorAction SilentlyContinue |
      Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $backup) {
      Write-Warning "RestoreBackup requested but no cordis.patch.yml.bak-* file exists; falling back to in-place block removal."
    } elseif ($PSCmdlet.ShouldProcess($paths.PatchFile, "restore $($backup.Name) and strip relay block")) {
      $currentBackup = "$($paths.PatchFile).bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
      Copy-Item -LiteralPath $paths.PatchFile -Destination $currentBackup -Force
      $restored = [System.IO.File]::ReadAllText($backup.FullName)
      $stripped = Remove-RelayBlockFromText -Text $restored
      Write-RelayPatchFile -Path $paths.PatchFile -Text $stripped.Text
      Write-Host "Patch    : restored $(backup.Name) (current saved as $(Split-Path -Leaf $currentBackup))"
      $changes.Add("restored patch backup $($backup.Name)")
    }
  }
  if (-not $RestoreBackup) {
    if ($PSCmdlet.ShouldProcess($paths.PatchFile, 'remove managed relay insert block')) {
      if (Remove-RelayPatchBlock -PatchFile $paths.PatchFile) {
        Write-Host 'Patch    : managed relay block removed' -ForegroundColor Green
        $changes.Add('removed patch block')
      } else {
        Write-Host 'Patch    : no managed relay block present (nothing to do)'
      }
    }
  }
} else {
  Write-Host "Patch    : not found ($($paths.PatchFile)); nothing to edit"
}

# ---- 2) junction ------------------------------------------------------------
if (Test-Path -LiteralPath $linkDir) {
  if ($PSCmdlet.ShouldProcess($linkDir, 'remove profile junction')) {
    if (Remove-RelayJunction -LinkPath $linkDir) {
      Write-Host 'Junction : removed' -ForegroundColor Green
      $changes.Add('removed junction')
    }
  }
} else {
  Write-Host 'Junction : not present (nothing to do)'
}

# ---- 3) staged plugin directory --------------------------------------------
if ($RemovePluginDir) {
  if (Test-Path -LiteralPath $paths.PluginRoot) {
    $item = Get-Item -LiteralPath $paths.PluginRoot -Force
    if ($item.LinkType) {
      Write-Warning "Refusing to delete link at plugin path: $($paths.PluginRoot)"
    } elseif ($PSCmdlet.ShouldProcess($paths.PluginRoot, 'delete staged plugin directory')) {
      Remove-Item -LiteralPath $paths.PluginRoot -Recurse -Force
      Write-Host 'Plugin   : staged directory removed' -ForegroundColor Green
      $changes.Add('removed plugin directory')
    }
  } else {
    Write-Host 'Plugin   : staged directory not present (nothing to do)'
  }
}

# ---- 4) pairing file --------------------------------------------------------
if ($RemovePairing) {
  if (Test-Path -LiteralPath $paths.PairingFile) {
    if ($PSCmdlet.ShouldProcess($paths.PairingFile, 'delete pairing file')) {
      Remove-Item -LiteralPath $paths.PairingFile -Force
      Write-Host 'Pairing  : deleted' -ForegroundColor Green
      $changes.Add('removed pairing file')
    }
  } else {
    Write-Host 'Pairing  : not present (nothing to do)'
  }
}

# ---- 5) wait until the relay listener is gone ------------------------------
if (-not $NoReload -and $relayHost -and -not $WhatIfPreference) {
  $processes = @(Get-DesktopHostProcesses)
  if ($processes.Count -eq 0) {
    Write-Host 'Desktop  : not running; nothing to hot-unload'
  } else {
    Write-Host "Waiting for https://${relayHost}:${relayPort}/__relay/health to stop answering ..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $stillUp = $true
    while ((Get-Date) -lt $deadline) {
      if (-not (Get-RelayHealth -TargetHost $relayHost -TargetPort $relayPort)) { $stillUp = $false; break }
      Start-Sleep -Milliseconds 1000
    }
    if ($stillUp) {
      Write-Warning "Relay still answers after ${TimeoutSeconds}s; the plugin may still be loaded (check the patch file / desktop log)."
    } else {
      Write-Host 'Relay    : listener stopped (plugin hot-unloaded)' -ForegroundColor Green
      $changes.Add('relay listener stopped')
    }
  }
}

Write-Host ''
if ($changes.Count -eq 0) {
  Write-Host 'Uninstall: nothing to change (already uninstalled).' -ForegroundColor Green
} else {
  Write-Host "Uninstall: done ($($changes.Count) change(s): $($changes -join ', '))." -ForegroundColor Green
}
if ($WhatIfPreference) { Write-Host '(WhatIf: no changes were made)' -ForegroundColor Yellow }
