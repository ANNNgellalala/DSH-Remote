<#
.SYNOPSIS
  Keep a reverse SSH tunnel from this Windows desktop to the VPS, so the DSH Remote
  relay (desktop-side, 127.0.0.1:8443) is reachable on the public VPS address.

.DESCRIPTION
  The DSH relay has to stay on the desktop: the launch token is process-local and the
  desktop web server only listens on 127.0.0.1. The VPS therefore runs no relay code;
  it only forwards a public TCP port through an SSH remote forward back to this machine.

    phone -> https://<vps>:<publicPort> -> sshd on VPS -> reverse tunnel -> 127.0.0.1:<localPort> (desktop relay)

  Actions:
    start      Launch the supervisor in the background (idempotent).
    stop       Stop the supervisor and its ssh child.
    status     Show supervisor/ssh state and probe the public health endpoint.
    restart    stop + start.
    supervise  Foreground reconnect loop (used by start and the logon task).
    install    Register a logon task that keeps the tunnel running after reboot.
    uninstall  Remove the logon task and stop the tunnel.

.EXAMPLE
  pwsh -File scripts/vps-tunnel.ps1 -Action start
  pwsh -File scripts/vps-tunnel.ps1 -Action status
  pwsh -File scripts/vps-tunnel.ps1 -Action install
#>
[CmdletBinding()]
param(
  [ValidateSet('start', 'stop', 'status', 'restart', 'supervise', 'install', 'uninstall')]
  [string]$Action = 'status',
  [string]$SshHost = '',
  [int]$SshPort = 0,
  [string]$User = '',
  [string]$KeyFile = '',
  [int]$PublicPort = 0,
  [int]$LocalPort = 8443,
  [string]$TaskName = 'DSHRemoteVpsTunnel',
  [int]$RetrySeconds = 5
)

$ErrorActionPreference = 'Stop'

# Machine-local deployment values live in config/dshremote.settings.json (git-ignored).
. (Join-Path $PSScriptRoot 'config.ps1')
$settings = Get-DshRemoteSettings
if (-not $SshHost) { $SshHost = $settings.Deploy.SshHost }
if ($SshPort -le 0) { $SshPort = $settings.Deploy.SshPort }
if (-not $User) { $User = $settings.Deploy.SshUser }
if (-not $KeyFile) { $KeyFile = $settings.Deploy.KeyFile }
if ($PublicPort -le 0) { $PublicPort = $settings.Deploy.PublicPort }
if (-not $SshHost) { throw 'Missing SSH host: set deploy.sshHost in config/dshremote.settings.json or pass -SshHost.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $env:USERPROFILE '.dsh\remote'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$pidFile = Join-Path $stateDir 'vps-tunnel.pid'
$logFile = Join-Path $stateDir 'vps-tunnel.log'
$scriptPath = $PSCommandPath

function Get-TunnelSshProcesses {
  @(Get-CimInstance Win32_Process -Filter "Name = 'ssh.exe'" -ErrorAction SilentlyContinue | Where-Object {
      $_.CommandLine -and $_.CommandLine -match '-R' -and
      $_.CommandLine -match [regex]::Escape(":$PublicPort") -and
      $_.CommandLine -match [regex]::Escape(":$LocalPort")
    })
}

function Get-SupervisorProcess {
  if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
  $id = (Get-Content -LiteralPath $pidFile -Raw).Trim()
  if ($id -notmatch '^\d+$') { return $null }
  Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
}

function Wait-Healthy([int]$TimeoutSeconds = 15) {
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    try {
      $health = Invoke-RestMethod -Uri "https://${SshHost}:${PublicPort}/__relay/health" -SkipCertificateCheck -TimeoutSec 8
      if ($health.ok -eq $true) { return $health }
    } catch { }
    Start-Sleep -Seconds 1
  }
  return $null
}

function Start-Supervisor {
  $running = Get-SupervisorProcess
  $ssh = Get-TunnelSshProcesses
  if ($running -or $ssh.Count -gt 0) {
    Write-Host "Tunnel already running (supervisor $(if ($running) { $running.Id } else { '-' }), ssh $(if ($ssh.Count) { ($ssh.Id -join ',') } else { '-' }))."
    return $true
  }

  $arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', "`"$scriptPath`"",
    '-Action', 'supervise',
    '-SshHost', $SshHost,
    '-SshPort', $SshPort,
    '-User', $User,
    '-KeyFile', "`"$KeyFile`"",
    '-PublicPort', $PublicPort,
    '-LocalPort', $LocalPort,
    '-RetrySeconds', $RetrySeconds
  )
  $process = Start-Process -FilePath (Get-Command powershell.exe).Source -ArgumentList $arguments -WindowStyle Hidden -PassThru
  Set-Content -LiteralPath $pidFile -Value $process.Id -Encoding ascii
  Write-Host "Supervisor started (pid $($process.Id)); log: $logFile"
  Start-Sleep -Seconds 4
  return $true
}

function Stop-Tunnel {
  $stopped = $false
  $supervisor = Get-SupervisorProcess
  if ($supervisor) {
    Stop-Process -Id $supervisor.Id -Force -ErrorAction SilentlyContinue
    $stopped = $true
  }
  foreach ($ssh in Get-TunnelSshProcesses) {
    Stop-Process -Id $ssh.ProcessId -Force -ErrorAction SilentlyContinue
    $stopped = $true
  }
  Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
  if ($stopped) { Write-Host 'Tunnel stopped.' } else { Write-Host 'Tunnel was not running.' }
}

function Show-Status {
  $supervisor = Get-SupervisorProcess
  $ssh = Get-TunnelSshProcesses
  Write-Host "VPS        : ${User}@${SshHost}:${SshPort}"
  Write-Host "Forward    : 0.0.0.0:$PublicPort -> 127.0.0.1:$LocalPort"
  Write-Host "Supervisor : $(if ($supervisor) { "running (pid $($supervisor.Id))" } else { 'not running' })"
  Write-Host "ssh child  : $(if ($ssh.Count) { 'running (pid ' + ($ssh.ProcessId -join ',') + ')' } else { 'not running' })"
  Write-Host "Log        : $logFile"
  $health = Wait-Healthy 10
  if ($health) {
    Write-Host "Public URL : https://${SshHost}:${PublicPort}  (health ok, relay $($health.relayVersion), publicHost=$($health.publicHost))" -ForegroundColor Green
    Write-Host "Download   : https://${SshHost}:${PublicPort}/__relay/download"
    return 0
  }

  Write-Warning "Public health probe failed: https://${SshHost}:${PublicPort}/__relay/health"
  return 1
}

function Install-Autostart {
  $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scriptPath`" -Action supervise -SshHost $SshHost -SshPort $SshPort -User $User -KeyFile `"$KeyFile`" -PublicPort $PublicPort -LocalPort $LocalPort"
  $created = $false
  try {
    schtasks.exe /Create /F /SC ONLOGON /TN $TaskName /TR $command /RL LIMITED 2>&1 | Out-Null
    $created = ($LASTEXITCODE -eq 0)
  } catch { $created = $false }

  if ($created) {
    Write-Host "Logon task '$TaskName' registered." -ForegroundColor Green
    return
  }

  # Fallback: Startup folder launcher (no admin required).
  $startup = [Environment]::GetFolderPath('Startup')
  $launcher = Join-Path $startup "$TaskName.cmd"
  @(
    '@echo off',
    "start `"`" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scriptPath`" -Action supervise -SshHost $SshHost -SshPort $SshPort -User $User -KeyFile `"$KeyFile`" -PublicPort $PublicPort -LocalPort $LocalPort"
  ) | Set-Content -LiteralPath $launcher -Encoding ascii
  Write-Host "Startup launcher written: $launcher (schtasks unavailable)" -ForegroundColor Yellow
}

function Uninstall-Autostart {
  schtasks.exe /Delete /F /TN $TaskName 2>&1 | Out-Null
  $launcher = Join-Path ([Environment]::GetFolderPath('Startup')) "$TaskName.cmd"
  Remove-Item -LiteralPath $launcher -Force -ErrorAction SilentlyContinue
  Write-Host 'Autostart removed.'
}

function Invoke-Supervise {
  if (-not (Test-Path -LiteralPath $KeyFile)) { throw "SSH key not found: $KeyFile" }
  Set-Content -LiteralPath $pidFile -Value $PID -Encoding ascii
  Write-Host "Supervising reverse tunnel $SshHost`:$SshPort -> 127.0.0.1:$LocalPort"
  while ($true) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -LiteralPath $logFile -Value "[$stamp] connecting 0.0.0.0:$PublicPort -> 127.0.0.1:$LocalPort via ${User}@${SshHost}:${SshPort}"
    & ssh.exe -N -T `
      -o ExitOnForwardFailure=yes `
      -o ServerAliveInterval=20 `
      -o ServerAliveCountMax=3 `
      -o StrictHostKeyChecking=accept-new `
      -o BatchMode=yes `
      -R "0.0.0.0:${PublicPort}:127.0.0.1:${LocalPort}" `
      -p $SshPort -i $KeyFile "$User@$SshHost" 2>> $logFile
    $code = $LASTEXITCODE
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -LiteralPath $logFile -Value "[$stamp] tunnel exited ($code); retrying in ${RetrySeconds}s"
    Start-Sleep -Seconds $RetrySeconds
  }
}

switch ($Action) {
  'start' { Start-Supervisor | Out-Null; exit (Show-Status) }
  'stop' { Stop-Tunnel; exit 0 }
  'status' { exit (Show-Status) }
  'restart' { Stop-Tunnel; Start-Supervisor | Out-Null; Start-Sleep -Seconds 2; exit (Show-Status) }
  'supervise' { Invoke-Supervise }
  'install' { Install-Autostart; Start-Supervisor | Out-Null; exit (Show-Status) }
  'uninstall' { Uninstall-Autostart; Stop-Tunnel; exit 0 }
}

