<#
.SYNOPSIS
  Load machine-local DSH Remote settings (dot-source this file).

.DESCRIPTION
  Scripts call `Get-DshRemoteSettings` after dot-sourcing this file. It reads
  `config/dshremote.settings.json` (git-ignored, real values) and falls back to
  `config/dshremote.settings.example.json` (committed placeholders) so a fresh
  clone still runs read-only/check commands.

  Relative `~` / `~/...` paths and `%VARIABLE%` / `$env:VARIABLE` expansions are
  resolved here, so JSON can stay portable across machines.

.PARAMETER Root
  Repository root. Defaults to the parent of the `scripts` directory.

.EXAMPLE
  . (Join-Path $PSScriptRoot 'config.ps1')
  $settings = Get-DshRemoteSettings
  $settings.Deploy.SshHost
#>
Set-StrictMode -Version Latest

function Expand-DshRemotePath {
  param([AllowNull()][AllowEmptyString()][string]$Path)

  if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
  $value = [Environment]::ExpandEnvironmentVariables($Path.Trim())
  if ($value -eq '~') { return $env:USERPROFILE }
  if ($value.StartsWith('~/') -or $value.StartsWith('~\')) {
    return (Join-Path $env:USERPROFILE $value.Substring(2))
  }
  return $value
}

function Get-DshRemoteJsonField {
  param(
    [AllowNull()]$Object,
    [Parameter(Mandatory)][string]$Name,
    [AllowNull()]$Default = $null
  )

  if ($null -eq $Object) { return $Default }
  $property = $Object.PSObject.Properties[$Name]
  if ($null -eq $property -or $null -eq $property.Value) { return $Default }
  if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value)) { return $Default }
  return $property.Value
}

function Get-DshRemoteSettings {
  [CmdletBinding()]
  param([string]$Root)

  if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
  $localPath = Join-Path $Root 'config/dshremote.settings.json'
  $examplePath = Join-Path $Root 'config/dshremote.settings.example.json'
  $path = if (Test-Path -LiteralPath $localPath) {
    $localPath
  } elseif (Test-Path -LiteralPath $examplePath) {
    $examplePath
  } else {
    throw "Missing DSH Remote settings: expected '$localPath' or '$examplePath'."
  }

  try {
    $document = (Get-Content -LiteralPath $path -Raw -Encoding UTF8) | ConvertFrom-Json
  } catch {
    throw "Cannot parse DSH Remote settings '$path': $($_.Exception.Message)"
  }

  $deploy = Get-DshRemoteJsonField $document 'deploy'
  $network = Get-DshRemoteJsonField $document 'network'
  $tools = Get-DshRemoteJsonField $document 'tools'
  $extraSan = @(Get-DshRemoteJsonField $deploy 'extraSan' @()) |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
    ForEach-Object { [string]$_ }

  [pscustomobject]@{
    Path    = $path
    IsLocal = (Test-Path -LiteralPath $localPath)
    Deploy  = [pscustomobject]@{
      SshHost    = [string](Get-DshRemoteJsonField $deploy 'sshHost' '203.0.113.10')
      SshPort    = [int](Get-DshRemoteJsonField $deploy 'sshPort' 22)
      SshUser    = [string](Get-DshRemoteJsonField $deploy 'sshUser' 'root')
      KeyFile    = Expand-DshRemotePath ([string](Get-DshRemoteJsonField $deploy 'keyFile' '~/.ssh/id_ed25519'))
      PublicHost = [string](Get-DshRemoteJsonField $deploy 'publicHost' '203.0.113.10')
      PublicPort = [int](Get-DshRemoteJsonField $deploy 'publicPort' 9443)
      ExtraSan   = @($extraSan)
    }
    Network = [pscustomobject]@{
      LanIp = [string](Get-DshRemoteJsonField $network 'lanIp' '')
    }
    Tools   = [pscustomobject]@{
      OpenSslPath         = Expand-DshRemotePath ([string](Get-DshRemoteJsonField $tools 'openSslPath' ''))
      AndroidSdkDirectory = Expand-DshRemotePath ([string](Get-DshRemoteJsonField $tools 'androidSdkDirectory' ''))
      JavaSdkDirectory    = Expand-DshRemotePath ([string](Get-DshRemoteJsonField $tools 'javaSdkDirectory' ''))
    }
  }
}
