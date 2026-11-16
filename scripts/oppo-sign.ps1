<#
.SYNOPSIS
  用 OPPO 官方 "APK Signature Info"（OPPOSign）工具的输出逻辑，生成 APK 的签名信息报告。

.DESCRIPTION
  OPPOSign（OPPOSign\bin\OPPOSign.jar，Main-Class com.heytap.msp.gui.MainApp）是 OPPO 的
  APK 签名信息查看/校验工具：把 APK 拖进窗口，显示 V1/V2/V3 签名方案的证书 DN 与
  MD5 / SHA-1 / SHA-256，并在有已校验签名时给出 "OPPO Sign" 值（OPPO 平台的应用签名标识）。

  它是一个 GUI 工具、不带命令行，窗口只接受拖放。本脚本用同一个 jar 内的
  ApkSignatureUtils / HashUtils / ApkVerifier 类重放同一套计算与输出格式，因此在 CI/无窗口
  环境也能得到与 GUI 完全一致的结果（包括 OPPO Sign 值）。

  注意：OPPOSign 只做「签名信息校验/查看」，不做「重新签名」。APK 的签名仍由
  scripts/build-release-apk.ps1（release keystore）完成。

.PARAMETER Apk
  要检查的 APK；默认取 dist/apk 下最新的 *-release.apk。

.PARAMETER OutFile
  可选：把报告同时写入该文件（UTF-8）。

.PARAMETER JavaHome
  JDK/JRE 目录；默认 C:\Program Files\Java\jdk-21，其次 PATH 中的 java。

.EXAMPLE
  pwsh -File scripts/oppo-sign.ps1
  pwsh -File scripts/oppo-sign.ps1 -Apk dist/apk/dshremote-1.7.0-38-arm64-release.apk -OutFile docs/evidence/oppo-sign-info.txt
#>
[CmdletBinding()]
param(
  [string]$Apk,
  [string]$OutFile,
  [string]$JavaHome = 'C:\Program Files\Java\jdk-21'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolDir = Join-Path $repoRoot 'OPPOSign'
$toolJar = Join-Path $toolDir 'bin/OPPOSign.jar'
$source = Join-Path $repoRoot 'build/oppo-sign/OppoSignReport.java'
$workDir = Join-Path $repoRoot '_scratch/oppo-sign'
$classesDir = Join-Path $workDir 'classes'

if (-not (Test-Path -LiteralPath $toolJar)) {
  throw "OPPOSign tool not found: $toolJar (expected the OPPO 'APK Signature Info' folder at the repo root)."
}
if (-not (Test-Path -LiteralPath $source)) {
  throw "Report source not found: $source"
}

if (-not $Apk) {
  $latest = Get-ChildItem (Join-Path $repoRoot 'dist/apk') -Filter '*-release.apk' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $latest) {
    throw "No APK given and no dist/apk/*-release.apk found."
  }
  $Apk = $latest.FullName
}
$Apk = (Resolve-Path -LiteralPath $Apk).Path

$java = Join-Path $JavaHome 'bin/java.exe'
$javac = Join-Path $JavaHome 'bin/javac.exe'
if (-not (Test-Path -LiteralPath $java)) { $java = 'java' }
if (-not (Test-Path -LiteralPath $javac)) { $javac = 'javac' }

New-Item -ItemType Directory -Force -Path $workDir | Out-Null
New-Item -ItemType Directory -Force -Path $classesDir | Out-Null

# Compile the harness against the OPPO tool jar (plain-Java classes only; no JavaFX needed).
$classFile = Join-Path $classesDir 'OppoSignReport.class'
if (-not (Test-Path -LiteralPath $classFile) -or
    (Get-Item -LiteralPath $source).LastWriteTimeUtc -gt (Get-Item -LiteralPath $classFile).LastWriteTimeUtc) {
  & $javac -Xlint:-options --release 8 -encoding UTF-8 -cp $toolJar -d $classesDir $source
  if ($LASTEXITCODE -ne 0) { throw "javac failed with exit code $LASTEXITCODE" }
}

Write-Host "OPPO Sign (APK Signature Info) report" -ForegroundColor Cyan
Write-Host "  apk     : $Apk"
Write-Host "  tool    : $toolJar"
Write-Host "  sha256  : $((Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Host "  size    : $((Get-Item -LiteralPath $Apk).Length) bytes"
Write-Host ''

$lines = & $java -cp "$toolJar;$classesDir" OppoSignReport $Apk
$exit = $LASTEXITCODE
$text = ($lines -join [Environment]::NewLine)
Write-Output $text
if ($OutFile) {
  $outPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutFile))
  $dir = Split-Path -Parent $outPath
  if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  @(
    "OPPO Sign (APK Signature Info) report"
    "apk: $Apk"
    "apk sha256: $((Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash.ToLowerInvariant())"
    "apk size: $((Get-Item -LiteralPath $Apk).Length) bytes"
    "tool: $toolJar (OPPOSign v1.1.0, same ApkVerifier/HashUtils classes as the GUI)"
    "generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
    ''
    $text
  ) | Set-Content -LiteralPath $outPath -Encoding UTF8
  Write-Host ''
  Write-Host "report written: $outPath" -ForegroundColor Green
}

exit $exit
