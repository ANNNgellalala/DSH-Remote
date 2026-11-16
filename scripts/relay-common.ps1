<#
.SYNOPSIS
  Shared helpers for the DSH Remote relay scripts (dot-source this file).

.DESCRIPTION
  One source of truth for: DSH home/profile discovery, the managed
  cordis.patch.yml block, the profile junction, and the relay health probe.
  Used by dev-install.ps1 (repo development), install-relay.ps1 (distribution),
  uninstall-relay.ps1, and e2e.ps1.
#>

$script:RelayPluginId = 'remote-relay'
$script:RelayPluginName = 'dsh-remote-relay'
$script:RelayBlockStartPattern = '^\s*#\s*-{2,}\s*dsh-remote-relay (?:\(managed by [^)]*\)|config override)';
$script:RelayBlockEndPattern = '^\s*#\s*-{2,}\s*end dsh-remote-relay\s*-{2,}\s*$'
$script:RelayTickPattern = '^\s*#\s*dsh-remote-relay reload tick:'

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
  throw 'No non-loopback IPv4 address found; pass an explicit bind host.'
}

function Get-DshHome {
  param([string]$DshHome)
  if ($DshHome) { return (Resolve-Path -LiteralPath $DshHome -ErrorAction Stop).Path.TrimEnd('\') }
  if ($env:DSH_HOME) { return ($env:DSH_HOME).TrimEnd('\') }
  return (Join-Path $env:USERPROFILE '.dsh').TrimEnd('\')
}

function Get-RelayProfilePaths {
  param(
    [string]$DshHome,
    [string]$Profile = 'desktop'
  )
  $home_ = Get-DshHome -DshHome $DshHome
  $profileDir = Join-Path $home_ "profiles\$Profile"
  [pscustomobject]@{
    DshHome      = $home_
    Profile      = $Profile
    ProfileDir   = $profileDir
    NodeModules  = Join-Path $profileDir 'node_modules'
    PatchFile    = Join-Path $profileDir 'cordis.patch.yml'
    PluginRoot   = Join-Path $home_ "plugins\$script:RelayPluginName"
    PairingFile  = Join-Path $home_ 'remote\pairing.json'
  }
}

function Write-RelayPatchFile {
  param([string]$Path, [string]$Text)
  [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

<#
  Persistent relay-server configuration document (relay v0.4.0+).

  The plugin reads <DSH_HOME>/remote/relay-config.json; the file wins over the
  legacy `config.gateway` patch block. scripts/relay-config.ps1 edits it through
  the running relay's loopback API; scripts/install-relay-bundle.ps1 only reads
  it (to verify the one-time migration) and never has to write it directly.
#>
function Get-RelayConfigPath {
  param([string]$DshHome)
  return (Join-Path (Get-DshHome -DshHome $DshHome) 'remote\relay-config.json')
}

function Get-RelayPersistedConfig {
  param([string]$ConfigFile)
  if (-not (Test-Path -LiteralPath $ConfigFile)) { return $null }
  try {
    $raw = [System.IO.File]::ReadAllText($ConfigFile)
    if (-not $raw.Trim()) { return $null }
    return ($raw | ConvertFrom-Json)
  } catch {
    return $null
  }
}

<#
  Remove every managed relay block (and reload-tick line) from the patch text.
  Returns @{ Text; Changed }.
#>
function Remove-RelayBlockFromText {
  param([string]$Text)
  $lines = $Text -split "`r?`n"
  $kept = New-Object System.Collections.Generic.List[string]
  $inBlock = $false
  $changed = $false
  foreach ($line in $lines) {
    if (-not $inBlock -and $line -match $script:RelayBlockStartPattern) { $inBlock = $true; $changed = $true; continue }
    if ($inBlock) {
      if ($line -match $script:RelayBlockEndPattern) { $inBlock = $false }
      continue
    }
    if ($line -match $script:RelayTickPattern) { $changed = $true; continue }
    $kept.Add($line)
  }
  while ($kept.Count -gt 0 -and $kept[$kept.Count - 1].Trim() -eq '') { $kept.RemoveAt($kept.Count - 1) }
  $text = ($kept -join "`r`n")
  if ($text.Length -gt 0) { $text += "`r`n" }
  [pscustomobject]@{ Text = $text; Changed = $changed }
}

<#
  Add (or refresh) the managed relay insert block in a profile patch file.
  Always backs the file up to <file>.bak-<timestamp> first. Idempotent.
  Returns @{ Backup; PatchFile; Changed }.
#>
function Set-RelayPatchBlock {
  param(
    [string]$PatchFile,
    [string]$BindHost,
    [int]$Port,
    [string]$CertFile,
    [string]$KeyFile,
    [string]$Token,
    [string]$Name,
    [string]$PublicHost,
    [int]$PublicPort,
    [switch]$GatewayEnabled,
    [string]$GatewayUrl,
    [string]$GatewayName,
    [string]$GatewaySecret,
    [string]$GatewayToken,
    [string]$GatewayFingerprint,
    [switch]$NoBackup
  )
  if (-not (Test-Path -LiteralPath $PatchFile)) { throw "Profile patch file not found: $PatchFile" }
  $backup = $null
  if (-not $NoBackup) {
    $backup = "$PatchFile.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $PatchFile -Destination $backup -Force
  }
  $original = [System.IO.File]::ReadAllText($PatchFile)
  $stripped = Remove-RelayBlockFromText -Text $original

  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add('')
  $lines.Add('# ---- dsh-remote-relay (managed by scripts/install-relay.ps1 / dev-install.ps1; do not edit by hand) ----')
  $lines.Add('- insert:')
  $lines.Add("    - id: $script:RelayPluginId")
  $lines.Add("      name: $script:RelayPluginName")
  $lines.Add('      meta:')
  $lines.Add('        title:')
  $lines.Add('          zh: DSH Remote 中继')
  $lines.Add('          en: DSH Remote Relay')
  $lines.Add('        description:')
  $lines.Add('          zh: 为手机/远程客户端提供 TLS 反代、配对信息、APK 下载与公网入口。')
  $lines.Add('          en: TLS reverse proxy, pairing, APK downloads and public ingress for DSH Remote clients.')
  $lines.Add('      config:')
  $lines.Add("        host: $BindHost")
  $lines.Add("        port: $Port")
  if ($PublicHost) {
    $lines.Add("        publicHost: '$PublicHost'")
    if ($PublicPort -gt 0) { $lines.Add("        publicPort: $PublicPort") }
  }
  $lines.Add('        tls:')
  $lines.Add("          certFile: '$CertFile'")
  $lines.Add("          keyFile: '$KeyFile'")
  if ($Name) { $lines.Add("        name: '$Name'") }
  if ($Token) { $lines.Add("        token: '$Token'") }
  if ($GatewayEnabled -or $GatewayUrl) {
    $lines.Add('        gateway:')
    $lines.Add('          enabled: true')
    if ($GatewayUrl) { $lines.Add("          url: '$GatewayUrl'") }
    if ($GatewayName) { $lines.Add("          name: '$GatewayName'") }
    if ($GatewaySecret) { $lines.Add("          secret: '$GatewaySecret'") }
    if ($GatewayToken) { $lines.Add("          token: '$GatewayToken'") }
    if ($GatewayFingerprint) { $lines.Add("          fingerprint: '$GatewayFingerprint'") }
  }
  $lines.Add('# ---- end dsh-remote-relay ----')
  $lines.Add('')
  $lines.Add('# dsh-remote-relay reload tick: ' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))

  $text = ($stripped.Text.TrimEnd() + "`r`n" + ($lines -join "`r`n") + "`r`n")
  Write-RelayPatchFile -Path $PatchFile -Text $text
  [pscustomobject]@{ Backup = $backup; PatchFile = $PatchFile; Changed = ($text -ne $original) }
}

<#
  Remove the managed relay block (keeps a backup unless -NoBackup).
  Returns $true when the file changed, $false when there was nothing to remove.
#>
function Remove-RelayPatchBlock {
  param([string]$PatchFile, [switch]$NoBackup)
  if (-not (Test-Path -LiteralPath $PatchFile)) { return $false }
  $original = [System.IO.File]::ReadAllText($PatchFile)
  $stripped = Remove-RelayBlockFromText -Text $original
  if (-not $stripped.Changed) { return $false }
  if (-not $NoBackup) {
    $backup = "$PatchFile.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $PatchFile -Destination $backup -Force
  }
  Write-RelayPatchFile -Path $PatchFile -Text $stripped.Text
  return $true
}

<#
  Create/replace the profile junction that makes the plugin resolvable by name.
#>
function Set-RelayJunction {
  param([string]$LinkPath, [string]$TargetDir)
  if (-not (Test-Path -LiteralPath $TargetDir)) { throw "Plugin directory not found: $TargetDir" }
  $target = (Resolve-Path -LiteralPath $TargetDir).Path.TrimEnd('\')
  $parent = Split-Path -Parent $LinkPath
  if (-not (Test-Path -LiteralPath $parent)) { throw "Profile node_modules not found: $parent" }
  if (Test-Path -LiteralPath $LinkPath) {
    $item = Get-Item -LiteralPath $LinkPath -Force
    if ($item.LinkType -eq 'Junction' -or $item.LinkType -eq 'SymbolicLink') {
      $current = @($item.LinkTarget)[0]
      if ($current -and ([System.IO.Path]::GetFullPath($current).TrimEnd('\')) -ieq $target) {
        return [pscustomobject]@{ LinkPath = $LinkPath; Target = $target; Changed = $false }
      }
      Remove-Item -LiteralPath $LinkPath -Force
    } else {
      throw "Refusing to replace non-link path: $LinkPath (remove it manually if it is stale)"
    }
  }
  New-Item -ItemType Junction -Path $LinkPath -Target $target | Out-Null
  [pscustomobject]@{ LinkPath = $LinkPath; Target = $target; Changed = $true }
}

function Remove-RelayJunction {
  param([string]$LinkPath)
  if (-not (Test-Path -LiteralPath $LinkPath)) { return $false }
  $item = Get-Item -LiteralPath $LinkPath -Force
  if ($item.LinkType -ne 'Junction' -and $item.LinkType -ne 'SymbolicLink') {
    throw "Refusing to remove non-link path: $LinkPath"
  }
  Remove-Item -LiteralPath $LinkPath -Force
  return $true
}

function Get-RelayHealth {
  param([string]$TargetHost, [int]$TargetPort, [int]$TimeoutSeconds = 3)
  $url = "https://${TargetHost}:${TargetPort}/__relay/health"
  $raw = & curl.exe -k -s -m $TimeoutSeconds $url 2>$null
  if (-not $raw) { return $null }
  try { return ($raw -join "`n" | ConvertFrom-Json) } catch { return $null }
}

function Get-DesktopHostProcesses {
  try {
    Get-CimInstance Win32_Process -Filter "Name = 'DeepSeek Harness.exe'" -ErrorAction Stop |
      Where-Object { $_.CommandLine -match 'dsh-desktop-host|resources\\app' }
  } catch {
    @()
  }
}

<#
  Poll the relay health endpoint until it answers (and, when -PreviousStartedAt is
  given, until the answering instance is a different one).
  A healthy-but-not-restarted relay is reported as Healthy=$true/Reloaded=$false:
  an idempotent rerun is a success, while callers that need a restart can check
  the Reloaded flag (see dev-install -RequireReload).
  Returns @{ Healthy; Reloaded; Health }.
#>
function Wait-RelayHealth {
  param(
    [string]$TargetHost,
    [int]$TargetPort,
    [int]$TimeoutSeconds = 45,
    [string]$PreviousStartedAt,
    [switch]$Quiet
  )
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $health = $null
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 1000
    $health = Get-RelayHealth -TargetHost $TargetHost -TargetPort $TargetPort
    if (-not $health) {
      if (-not $Quiet) { Write-Host '  waiting for relay health ...' -ForegroundColor DarkGray }
      continue
    }
    if (-not $PreviousStartedAt -or [string]$health.startedAt -ne $PreviousStartedAt) {
      return [pscustomobject]@{ Healthy = $true; Reloaded = ($null -ne $PreviousStartedAt); Health = $health }
    }
  }
  # Timed out waiting for a *different* instance, but the relay is answering:
  # report it as healthy/not-reloaded rather than down.
  $last = Get-RelayHealth -TargetHost $TargetHost -TargetPort $TargetPort
  if ($last) {
    return [pscustomobject]@{ Healthy = $true; Reloaded = $false; Health = $last }
  }
  [pscustomobject]@{ Healthy = $false; Reloaded = $false; Health = $health }
}


<#
  Write the managed id-targeted config override for the bundle-installed plugin.
  Used after a bundle install to keep user settings (name/gateway/...) without a
  hand-written insert. Replaces any existing managed relay block/tick.
#>
function Set-RelayConfigOverride {
  param(
    [string]$PatchFile,
    [string]$Name,
    [switch]$GatewayEnabled,
    [string]$GatewayUrl,
    [string]$GatewayName,
    [string]$GatewaySecret,
    [string]$GatewayToken,
    [string]$GatewayFingerprint,
    [switch]$NoBackup
  )
  if (-not (Test-Path -LiteralPath $PatchFile)) { throw "Profile patch file not found: $PatchFile" }
  $backup = $null
  if (-not $NoBackup) {
    $backup = "$PatchFile.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $PatchFile -Destination $backup -Force
  }
  $original = [System.IO.File]::ReadAllText($PatchFile)
  $stripped = Remove-RelayBlockFromText -Text $original
  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add('')
  $lines.Add('# ---- dsh-remote-relay config override (managed by scripts/install-relay-bundle.ps1; do not edit by hand) ----')
  $lines.Add('- id: remote-relay')
  $lines.Add('  config:')
  if ($Name) { $lines.Add("    name: '$Name'") }
  if ($GatewayEnabled -or $GatewayUrl) {
    $lines.Add('    gateway:')
    $lines.Add('      enabled: true')
    if ($GatewayUrl) { $lines.Add("      url: '$GatewayUrl'") }
    if ($GatewayName) { $lines.Add("      name: '$GatewayName'") }
    if ($GatewaySecret) { $lines.Add("      secret: '$GatewaySecret'") }
    if ($GatewayToken) { $lines.Add("      token: '$GatewayToken'") }
    if ($GatewayFingerprint) { $lines.Add("      fingerprint: '$GatewayFingerprint'") }
  }
  $lines.Add('# ---- end dsh-remote-relay ----')
  $lines.Add('')
  $lines.Add('# dsh-remote-relay reload tick: ' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
  $text = ($stripped.Text.TrimEnd() + "`r`n" + ($lines -join "`r`n") + "`r`n")
  Write-RelayPatchFile -Path $PatchFile -Text $text
  [pscustomobject]@{ Backup = $backup; PatchFile = $PatchFile; Changed = ($text -ne $original) }
}
