<#
.SYNOPSIS
  Resolve the Android toolchain used by the DSH Remote scripts (dot-source).

.DESCRIPTION
  This machine keeps the Android SDK outside the default location
  (D:\SmartBillAndroidSdk). Resolution order per item:

    SDK : -AndroidSdkDirectory / DSHREMOTE_ANDROID_SDK / app/Directory.Build.props
          default / D:\SmartBillAndroidSdk / ANDROID_HOME / ANDROID_SDK_ROOT
    JDK : -JavaSdkDirectory / JAVA_HOME / C:\Program Files\Java\jdk-21

  Returns an object with Sdk, Jdk, Adb, Keytool, BuildToolsVersion, ApkSigner.
#>
function Resolve-AndroidToolchain {
  param(
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory
  )

  $repoRoot = Split-Path -Parent $PSScriptRoot
  $sdkCandidates = @()
  if ($AndroidSdkDirectory) { $sdkCandidates += $AndroidSdkDirectory }
  if ($env:DSHREMOTE_ANDROID_SDK) { $sdkCandidates += $env:DSHREMOTE_ANDROID_SDK }
  $propsPath = Join-Path $repoRoot 'app\Directory.Build.props'
  if (Test-Path -LiteralPath $propsPath) {
    $match = [regex]::Match([System.IO.File]::ReadAllText($propsPath), '<AndroidSdkDirectory Condition="''\$\(AndroidSdkDirectory\)'' == ''''">([^<]+)</AndroidSdkDirectory>')
    if ($match.Success) { $sdkCandidates += $match.Groups[1].Value.Trim() }
  }
  $sdkCandidates += 'D:\SmartBillAndroidSdk'
  if ($env:ANDROID_HOME) { $sdkCandidates += $env:ANDROID_HOME }
  if ($env:ANDROID_SDK_ROOT) { $sdkCandidates += $env:ANDROID_SDK_ROOT }
  $sdk = $sdkCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
  if (-not $sdk) {
    throw "Android SDK not found. Tried: $($sdkCandidates -join '; '). Pass -AndroidSdkDirectory or set DSHREMOTE_ANDROID_SDK."
  }
  if ($sdk -match '[^\x00-\x7F]') {
    Write-Warning "AndroidSdkDirectory contains non-ASCII characters; zipalign may fail: $sdk"
  }

  $jdkCandidates = @()
  if ($JavaSdkDirectory) { $jdkCandidates += $JavaSdkDirectory }
  if ($env:JAVA_HOME) { $jdkCandidates += $env:JAVA_HOME }
  $jdkCandidates += 'C:\Program Files\Java\jdk-21'
  $jdk = $jdkCandidates | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin\keytool.exe')) } | Select-Object -First 1
  if (-not $jdk) {
    throw "JDK not found. Tried: $($jdkCandidates -join '; '). Pass -JavaSdkDirectory or set JAVA_HOME."
  }

  $adb = Join-Path $sdk 'platform-tools\adb.exe'
  $buildToolsRoot = Join-Path $sdk 'build-tools'
  $buildTools = $null
  if (Test-Path -LiteralPath $buildToolsRoot) {
    $buildTools = Get-ChildItem -LiteralPath $buildToolsRoot -Directory |
      Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '0') } -Descending |
      Select-Object -First 1
  }
  $apkSigner = if ($buildTools) { Join-Path $buildTools.FullName 'apksigner.bat' } else { $null }

  [pscustomobject]@{
    Sdk               = (Resolve-Path -LiteralPath $sdk).Path.TrimEnd('\')
    Jdk               = (Resolve-Path -LiteralPath $jdk).Path.TrimEnd('\')
    Adb               = if (Test-Path -LiteralPath $adb) { $adb } else { $null }
    Keytool           = Join-Path $jdk 'bin\keytool.exe'
    BuildToolsVersion = if ($buildTools) { $buildTools.Name } else { $null }
    ApkSigner         = if ($apkSigner -and (Test-Path -LiteralPath $apkSigner)) { $apkSigner } else { $null }
  }
}
