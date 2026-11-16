<#
.SYNOPSIS
  Upload / list / delete resources on the public DSH Remote gateway.

.DESCRIPTION
  Drives the gateway resource-centre API (v0.4.0, organised by resource
  category id) over HTTPS (self-signed cert, so curl runs with -k):

    PUT    /__gateway/resources/<id>/<name>?keepVersions=1|0  upload   (Authorization: Bearer <token>)
    GET    /__gateway/resources[?id=<id>]                     JSON index
    DELETE /__gateway/resources/<id>/<name>                   delete   (Authorization: Bearer <token>)

  Category ids: dsh-remote, smartbill, relay, docs (CONTRACT.md §2). Each id
  maps to <RESOURCE_DIR>/<id> on the VPS (default /opt/dsh-remote/resources);
  the legacy /opt/dsh-remote/apk directory is only a download fallback now.

  keepVersions:
    -KeepVersions   keep the newest 3 uploads of the id (?keepVersions=1)
    (default)       keep only the file just uploaded (?keepVersions=0)

  Upload token resolution order:
    1. -Token
    2. $env:DSH_GATEWAY_UPLOAD_TOKEN
    3. /etc/dsh-remote/gateway.env on the VPS over SSH (grep UPLOAD_TOKEN)

  Upload verifies:
    - the sha256 returned by the gateway equals the local file sha256
    - (unless -SkipVerify) a fresh HTTPS download of the file equals the local
      file byte-for-byte (sha256)

  scripts/sync-apk.ps1 is a thin wrapper that calls this script over the upload
  API (it no longer uses scp).

.EXAMPLE
  pwsh -File scripts/upload-resource.ps1 -List

.EXAMPLE
  pwsh -File scripts/upload-resource.ps1 -List -Id docs

.EXAMPLE
  pwsh -File scripts/upload-resource.ps1 -Id docs -File README.md

.EXAMPLE
  pwsh -File scripts/upload-resource.ps1 -Id relay -File dist\dsh-remote-relay-0.4.0.tgz -KeepVersions

.EXAMPLE
  pwsh -File scripts/upload-resource.ps1 -Id docs -File vps\README.zh.md -Name gateway-readme.md

.EXAMPLE
  pwsh -File scripts/upload-resource.ps1 -Delete -Id docs -Name gateway-readme.md

.EXAMPLE
  # 用显式令牌（不读取 VPS 上的 gateway.env）
  pwsh -File scripts/upload-resource.ps1 -Id docs -File .\notes.txt -Token $env:MY_TOKEN

.EXAMPLE
  # 本地冒烟测试：对着 TLS_ENABLED=0 的本地网关（HTTP）跑完整上传/校验/删除
  pwsh -File scripts/upload-resource.ps1 -Id docs -File vps\README.zh.md -Scheme http -PublicHost 127.0.0.1 -PublicPort 19443 -Token local-test-token
#>
[CmdletBinding()]
param(
  [ValidateSet('dsh-remote', 'smartbill', 'relay', 'docs')]
  [string]$Id = '',
  [switch]$KeepVersions,
  [string]$File = '',
  [string]$Name = '',
  [switch]$Delete,
  [switch]$List,
  [string]$Token = '',
  [string]$SshHost = '',
  [int]$SshPort = 0,
  [string]$User = '',
  [string]$KeyFile = '',
  [string]$PublicHost = '',
  [int]$PublicPort = 0,
  [ValidateSet('https', 'http')]
  [string]$Scheme = 'https',
  [string]$EnvFile = '/etc/dsh-remote/gateway.env',
  [int]$TimeoutSec = 300,
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
if (-not $SshHost) { throw 'Missing SSH host: set deploy.sshHost in config/dshremote.settings.json or pass -SshHost.' }

$publicBase = "${Scheme}://${PublicHost}:${PublicPort}"
$tempFiles = New-Object System.Collections.Generic.List[string]

function Write-Head([string]$text) {
  Write-Host ''
  Write-Host "==> $text" -ForegroundColor Cyan
}
function Write-Info([string]$text) { Write-Host "    $text" }
function Fail([string]$message) {
  Write-Host "ERROR: $message" -ForegroundColor Red
  exit 1
}
function New-TempFile([string]$suffix) {
  $path = Join-Path ([System.IO.Path]::GetTempPath()) ("gw-resource-{0}{1}" -f [guid]::NewGuid().ToString('n'), $suffix)
  $tempFiles.Add($path)
  return $path
}
function Remove-TempFiles {
  foreach ($path in $tempFiles) {
    try { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue } catch {}
  }
}
function Format-Size([long]$bytes) {
  if ($bytes -lt 1KB) { return "$bytes B" }
  if ($bytes -lt 1MB) { return ('{0:N1} KiB' -f ($bytes / 1KB)) }
  if ($bytes -lt 1GB) { return ('{0:N2} MiB' -f ($bytes / 1MB)) }
  return ('{0:N2} GiB' -f ($bytes / 1GB))
}

function Resolve-UploadToken {
  if ($Token -ne '') { return $Token.Trim() }
  if ($env:DSH_GATEWAY_UPLOAD_TOKEN) { return $env:DSH_GATEWAY_UPLOAD_TOKEN.Trim() }
  if (-not (Test-Path -LiteralPath $KeyFile)) {
    Fail "未提供 -Token，且找不到 SSH 私钥 $KeyFile（无法从 VPS 读取 UPLOAD_TOKEN）"
  }
  Write-Info "upload token: reading UPLOAD_TOKEN from ${User}@${SshHost}:$EnvFile over SSH"
  $output = & ssh.exe -i $KeyFile -p "$SshPort" -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o ConnectTimeout=15 "$User@$SshHost" "grep -h '^UPLOAD_TOKEN=' '$EnvFile' 2>/dev/null | cut -d= -f2-" 2>&1
  $code = $LASTEXITCODE
  $value = (($output | ForEach-Object { "$_" }) -join "`n").Trim()
  if ($code -ne 0 -or $value -eq '') {
    Fail "无法从 VPS 读取 UPLOAD_TOKEN（$($value)）。请用 -Token 显式传入，或先运行 scripts/deploy-gateway.ps1。"
  }
  return $value
}

function Invoke-CurlStatus {
  param(
    [Parameter(Mandatory)][string[]]$Arguments,
    [Parameter(Mandatory)][string]$OutFile
  )
  $all = @('-k', '-sS', '--max-time', "$TimeoutSec", '-o', $OutFile, '-w', '%{http_code}') + $Arguments
  $raw = @(& curl.exe @all 2>&1 | ForEach-Object { "$_" })
  $exitCode = $LASTEXITCODE
  $status = ''
  foreach ($line in $raw) {
    $text = "$line".Trim()
    if ($text -match '^\d{3}$') { $status = $text }
  }
  return [pscustomobject]@{
    ExitCode = $exitCode
    Status   = $status
    Output   = (($raw | ForEach-Object { "$_" }) -join "`n").Trim()
  }
}

function Get-Curl {
  param(
    [Parameter(Mandatory)][string[]]$Arguments,
    [Parameter(Mandatory)][string]$Description
  )
  $bodyFile = New-TempFile '.body'
  $result = Invoke-CurlStatus -Arguments $Arguments -OutFile $bodyFile
  $body = ''
  if (Test-Path -LiteralPath $bodyFile) { $body = Get-Content -LiteralPath $bodyFile -Raw }
  if ($result.ExitCode -ne 0) {
    Fail "$Description 失败：curl exit $($result.ExitCode)（$($result.Output)）"
  }
  if ($result.Status -eq '') {
    Fail "$Description 失败：无法解析 HTTP 状态码（$($result.Output)）"
  }
  return [pscustomobject]@{ Status = $result.Status; Body = $body }
}

try {
  # -------------------------------------------------------------------------
  # List mode
  # -------------------------------------------------------------------------
  if ($List) {
    $url = "$publicBase/__gateway/resources"
    if ($Id -ne '') { $url += "?id=$Id" }
    Write-Head 'Resource index'
    Write-Info "GET $url"
    $response = Get-Curl -Arguments @($url) -Description '读取资源索引'
    if ($response.Status -ne '200') { Fail "资源索引返回 HTTP $($response.Status)：$($response.Body)" }
    $index = $response.Body | ConvertFrom-Json
    Write-Info "gateway $($index.gateway.name) v$($index.gateway.version) · total=$($index.total) · $($index.generatedAt)"
    foreach ($category in $index.categories) {
      Write-Host ''
      Write-Host ("  [{0}] {1} — {2} 个文件" -f $category.id, $category.title, $category.count) -ForegroundColor Yellow
      foreach ($item in $category.files) {
        Write-Info ("  {0,-44} {1,10}  {2}  {3}" -f $item.name, (Format-Size $item.sizeBytes), $item.sha256, $item.url)
      }
    }
    return
  }

  # -------------------------------------------------------------------------
  # Delete mode
  # -------------------------------------------------------------------------
  if ($Delete) {
    if ($Id -eq '') { Fail '-Delete 需要 -Id（dsh-remote|smartbill|relay|docs）' }
    if ($Name -eq '') {
      if ($File -eq '') { Fail '-Delete 需要 -Name（远端文件名），或用 -File 指定本地文件（取文件名）' }
      $Name = Split-Path -Leaf $File
    }
    $token = Resolve-UploadToken
    $encoded = [uri]::EscapeDataString($Name)
    $url = "$publicBase/__gateway/resources/$Id/$encoded"
    Write-Head "Delete $Id/$Name"
    Write-Info "DELETE $url"
    $response = Get-Curl -Arguments @('-X', 'DELETE', '-H', "Authorization: Bearer $token", $url) -Description '删除资源'
    Write-Info "HTTP $($response.Status)"
    if ($response.Status -ne '200') { Fail "删除失败：HTTP $($response.Status) $($response.Body)" }
    Write-Info "deleted: $Id/$Name"
    return
  }

  # -------------------------------------------------------------------------
  # Upload mode
  # -------------------------------------------------------------------------
  if ($Id -eq '') { Fail '上传需要 -Id（dsh-remote|smartbill|relay|docs）' }
  if ($File -eq '') { Fail '上传需要 -File（本地文件路径）' }
  if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { Fail "本地文件不存在：$File" }
  if ($Name -eq '') { $Name = Split-Path -Leaf $File }
  if ($Name -match '[\\/]') { Fail "文件名不能包含路径分隔符：$Name" }

  $item = Get-Item -LiteralPath $File
  $localHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $token = Resolve-UploadToken
  $encoded = [uri]::EscapeDataString($Name)
  $keepVersionsFlag = if ($KeepVersions) { 1 } else { 0 }
  $url = "$publicBase/__gateway/resources/$Id/$encoded" + "?keepVersions=$keepVersionsFlag"

  Write-Head "Upload $($item.Name) -> $Id/$Name"
  Write-Info "local file : $($item.FullName)"
  Write-Info "size       : $(Format-Size $item.Length) ($($item.Length) bytes)"
  Write-Info "local sha256: $localHash"
  Write-Info "keepVersions: $keepVersionsFlag $(if ($KeepVersions) { '(保留最近 3 个版本)' } else { '(只保留本次上传的版本)' })"
  Write-Info "PUT        : $url"

  $response = Get-Curl -Arguments @(
    '-X', 'PUT',
    '-H', "Authorization: Bearer $token",
    '-H', 'Content-Type: application/octet-stream',
    '--data-binary', "@$($item.FullName)",
    $url
  ) -Description '上传资源'

  Write-Info "HTTP       : $($response.Status)"
  if ($response.Status -notin @('200', '201')) {
    Fail "上传失败：HTTP $($response.Status) $($response.Body)"
  }
  $result = $response.Body | ConvertFrom-Json
  $retained = @()
  $pruned = @()
  if ($result.PSObject.Properties['retained']) { $retained = @($result.retained) }
  if ($result.PSObject.Properties['pruned']) { $pruned = @($result.pruned) }
  Write-Info "remote sha256: $($result.sha256) (replaced=$($result.replaced))"
  if ($result.sha256 -ne $localHash) {
    Fail "sha256 不一致：本地 $localHash / 网关 $($result.sha256)"
  }
  Write-Info "retained   : $(if ($retained.Count -gt 0) { $retained -join ', ' } else { '(none)' })"
  Write-Info "pruned     : $(if ($pruned.Count -gt 0) { $pruned -join ', ' } else { '(none)' })"
  Write-Info "direct URL : $($result.url)"

  if (-not $SkipVerify) {
    Write-Head 'Verify download'
    $downloadFile = New-TempFile '.download'
    $verify = Invoke-CurlStatus -Arguments @($result.url) -OutFile $downloadFile
    if ($verify.ExitCode -ne 0) { Fail "回读下载失败：curl exit $($verify.ExitCode)（$($verify.Output)）" }
    if ($verify.Status -ne '200') { Fail "回读失败：HTTP $($verify.Status)" }
    $remoteHash = (Get-FileHash -LiteralPath $downloadFile -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Info "download   : HTTP $($verify.Status), $(Format-Size (Get-Item -LiteralPath $downloadFile).Length)"
    Write-Info "download sha256: $remoteHash"
    if ($remoteHash -ne $localHash) { Fail '回读 sha256 与本地文件不一致' }
    Write-Info 'sha256 OK（本地 = 网关响应 = HTTPS 回读）'
  }

  Write-Head 'Done'
  Write-Info "page        : $publicBase/__gateway/download"
  Write-Info "index       : $publicBase/__gateway/resources?id=$Id"
  Write-Info "direct URL  : $($result.url)"
} finally {
  Remove-TempFiles
}
