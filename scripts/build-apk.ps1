<#
.SYNOPSIS
    Build the DSH Remote Android APK (and optionally install it on the connected phone).

.DESCRIPTION
    Wraps `dotnet build` with the two properties this machine needs:
      -p:AndroidSdkDirectory=<SDK with API 36>   (default: app/Directory.Build.props value / D:\SmartBillAndroidSdk)
      -p:JavaSdkDirectory=<JDK 21>              (default: C:\Program Files\Java\jdk-21)
    The SDK path must stay ASCII: zipalign fails on non-ASCII paths.

.EXAMPLE
    pwsh -File scripts/build-apk.ps1
    pwsh -File scripts/build-apk.ps1 -Install
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$AndroidSdkDirectory = $env:DSHREMOTE_ANDROID_SDK,

    [string]$JavaSdkDirectory = $env:JAVA_HOME,

    [switch]$Install,

    [string]$AdbPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'app/src/DshRemote.App.Android/DshRemote.App.Android.csproj'

if (-not (Test-Path $project)) { throw "Android project not found: $project" }

if (-not $AndroidSdkDirectory) { $AndroidSdkDirectory = 'D:\SmartBillAndroidSdk' }
if (-not $JavaSdkDirectory) {
    $candidate = 'C:\Program Files\Java\jdk-21'
    if (Test-Path $candidate) { $JavaSdkDirectory = $candidate }
}

if (-not (Test-Path $AndroidSdkDirectory)) { throw "Android SDK not found: $AndroidSdkDirectory (pass -AndroidSdkDirectory)" }
if ($JavaSdkDirectory -and -not (Test-Path $JavaSdkDirectory)) { throw "JDK not found: $JavaSdkDirectory (pass -JavaSdkDirectory)" }

if ($AndroidSdkDirectory -match '[^\x00-\x7F]') {
    Write-Warning "AndroidSdkDirectory contains non-ASCII characters; zipalign may fail: $AndroidSdkDirectory"
}

Write-Host "Building $project ($Configuration)" -ForegroundColor Cyan
Write-Host "  AndroidSdkDirectory = $AndroidSdkDirectory"
Write-Host "  JavaSdkDirectory    = $JavaSdkDirectory"

$arguments = @(
    'build', $project,
    '-c', $Configuration,
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory"
)
if ($JavaSdkDirectory) { $arguments += "-p:JavaSdkDirectory=$JavaSdkDirectory" }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$apk = Get-ChildItem (Join-Path $repoRoot "app/src/DshRemote.App.Android/bin/$Configuration/net10.0-android") -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $apk) { throw "Build succeeded but no signed APK was found under bin/$Configuration/net10.0-android" }

Write-Host ""
Write-Host "APK: $($apk.FullName) ($([math]::Round($apk.Length / 1MB, 1)) MB)" -ForegroundColor Green

if ($Install) {
    if (-not $AdbPath) {
        $AdbPath = Join-Path $AndroidSdkDirectory 'platform-tools/adb.exe'
    }

    if (-not (Test-Path $AdbPath)) { throw "adb not found: $AdbPath (pass -AdbPath)" }

    $devices = & $AdbPath devices
    Write-Host ($devices -join [Environment]::NewLine)
    Write-Host "Installing..." -ForegroundColor Cyan
    & $AdbPath install -r -t $apk.FullName
    if ($LASTEXITCODE -ne 0) { throw "adb install failed with exit code $LASTEXITCODE" }
}
