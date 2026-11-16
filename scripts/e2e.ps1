<#
.SYNOPSIS
  One-command end-to-end check for DSH Remote (M4).

.DESCRIPTION
  Runs the whole chain and prints a PASS/FAIL summary. Any failed step exits
  non-zero. Each step writes a full log under _scratch/e2e/<timestamp>/.

    [1] relay smoke        scripts/relay-smoke.ps1 -IncludeWs
                           (TLS /connect + session/list + WSS mux 101 + $events ready
                            + pairing active + relay self-check capabilities)
    [2] Core unit tests    dotnet test app/tests/DshRemote.Core.Tests
    [3] protocol smoke    DshRemote.Smoke --loopback --sessions N --history N
    [4] Android (optional, default on)
        a. APK: default Debug (scripts/build-apk.ps1, good for emulator/device dev);
           -Release builds the signed Release APK; -Apk reuses any APK
        b. adb -s <serial> install (auto-uninstalls on signature change)
        c. launch app + screenshot pulled into the log directory
        d. best-effort push of pairing.json into the app's external files dir

.PARAMETER SkipAndroid
  Skip all Android steps (build/install/launch/screenshot).

.PARAMETER Serial
  Target adb device serial (equivalent to `adb -s <serial> ...`), e.g.
  `emulator-5556` or a wireless-debugging serial. Alias: -Device.
  Default: the first online device reported by `adb devices`.

.PARAMETER Apk
  Reuse an existing APK (any build). Default: Debug APK, built if missing.
  Alias of -ApkPath.

.PARAMETER Release
  Build the signed Release APK (scripts/build-release-apk.ps1) instead of Debug.
  Uses -DisplayVersion/-VersionCode and the app/keys keystore/password.

.PARAMETER SkipApkBuild
  Do not build; use -Apk or the default Debug APK (fallback: newest dist/apk/*.apk).

.PARAMETER SkipApkInstall
  Build/locate the APK but do not install/launch/screenshot.

.PARAMETER SkipRelay / SkipTests / SkipSmokeTool
  Skip individual non-Android steps.

.PARAMETER LogDir
  Where step logs and the screenshot go (default _scratch/e2e/<timestamp>).

.EXAMPLE
  pwsh -File scripts/e2e.ps1 -SkipAndroid

.EXAMPLE
  pwsh -File scripts/e2e.ps1
#>
[CmdletBinding()]
param(
  [switch]$SkipAndroid,
  [switch]$SkipRelay,
  [switch]$SkipTests,
  [switch]$SkipSmokeTool,
  [switch]$SkipApkBuild,
  [switch]$SkipApkInstall,
  [Alias('Apk')]
  [string]$ApkPath,
  [Alias('Device')]
  [string]$Serial,
  [switch]$Release,
  [string]$LogDir,
  [string]$PairingFile,
  [string]$KeystorePath,
  [string]$StorePass = $env:DSHREMOTE_KEYSTORE_PASS,
  [string]$DisplayVersion = '0.2.0',
  [string]$VersionCode = '2',
  [string]$AndroidSdkDirectory = $env:DSHREMOTE_ANDROID_SDK,
  [string]$JavaSdkDirectory = $env:JAVA_HOME,
  [string]$AdbPath,
  [int]$SessionsToShow = 5,
  [int]$HistoryEvents = 5
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
# Resolve the pwsh executable for child scripts. $PID's own process may be a
# dotnet host (e.g. DSH's pwsh tool), so prefer the registered pwsh command.
$pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
if (-not $pwshCommand) { $pwshCommand = Get-Command pwsh -ErrorAction SilentlyContinue }
$pwshExe = if ($pwshCommand) { $pwshCommand.Source } else { 'pwsh' }

if (-not $LogDir) {
  $LogDir = Join-Path $repoRoot ("_scratch\e2e\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$LogDir = (Resolve-Path -LiteralPath $LogDir).Path

# Stable English output for machine parsing (dotnet test/run are localized).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$results = New-Object System.Collections.Generic.List[object]
$failures = 0

function Add-Result {
  param([string]$Name, [string]$Status, [double]$Seconds, [string]$Detail)
  $results.Add([pscustomobject]@{ Step = $Name; Status = $Status; Seconds = [math]::Round($Seconds, 1); Detail = $Detail }) | Out-Null
  $color = if ($Status -eq 'PASS') { 'Green' } elseif ($Status -eq 'SKIP') { 'DarkGray' } else { 'Red' }
  Write-Host ("[{0}] {1} ({2}s) {3}" -f $Status, $Name, [math]::Round($Seconds, 1), $Detail) -ForegroundColor $color
}

function Save-Lines {
  param([string]$Name, [object[]]$Lines)
  $file = Join-Path $LogDir "$Name.log"
  $Lines | Set-Content -LiteralPath $file -Encoding utf8
  return $file
}

# ---------------------------------------------------------------------------
# [1] relay smoke
# ---------------------------------------------------------------------------
if ($SkipRelay) {
  Add-Result 'relay-smoke' 'SKIP' 0 '-SkipRelay'
} else {
  $start = Get-Date
  $smokeArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'relay-smoke.ps1'), '-IncludeWs', '-Loopback')
  if ($PairingFile) { $smokeArgs += @('-PairingFile', $PairingFile) }
  $output = & $pwshExe @smokeArgs *>&1 | ForEach-Object { "$_" }
  $exit = $LASTEXITCODE
  $log = Save-Lines 'relay-smoke' $output
  $summary = ($output | Where-Object { $_ -match '^SMOKE:' } | Select-Object -Last 1)
  if ($exit -eq 0 -and $summary -match 'PASS') {
    Add-Result 'relay-smoke' 'PASS' ((Get-Date) - $start).TotalSeconds "log: $log"
  } else {
    Add-Result 'relay-smoke' 'FAIL' ((Get-Date) - $start).TotalSeconds "exit=$exit log: $log"
    $failures++
  }
}

# ---------------------------------------------------------------------------
# [2] Core unit tests
# ---------------------------------------------------------------------------
if ($SkipTests) {
  Add-Result 'core-tests' 'SKIP' 0 '-SkipTests'
} else {
  $start = Get-Date
  $testProject = Join-Path $repoRoot 'app/tests/DshRemote.Core.Tests/DshRemote.Core.Tests.csproj'
  if (-not (Test-Path -LiteralPath $testProject)) {
    Add-Result 'core-tests' 'FAIL' 0 "project not found: $testProject"
    $failures++
  } else {
    $output = & dotnet test $testProject --nologo -v minimal *>&1 | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE

    # VSTest cannot start its testhost in parent-managed/confined shells: the host aborts before running any
    # test with Win32Exception 5 from SetParentProcessExitCallback. Fall back to the in-process runner so the
    # end-to-end flow still verifies the unit tests (see docs/build-verification-vs2026.md).
    if ($exit -ne 0 -and (($output -join "`n") -match 'Test Run Aborted|SetParentProcessExitCallback')) {
      $fallbackOutput = & $pwshExe -NoProfile -File (Join-Path $PSScriptRoot 'run-tests.ps1') `
        -Configuration 'Debug' -Project 'DshRemote.Core.Tests' *>&1 | ForEach-Object { "$_" }
      $exit = $LASTEXITCODE
      $output = @($output) + @('--- fallback runner (-Project DshRemote.Core.Tests) ---') + @($fallbackOutput)
    }

    $log = Save-Lines 'core-tests' $output
    $summaryLine = ($output | Where-Object { $_ -match 'Failed:\s+\d+' -or $_ -match '^passed=\d+ failed=\d+' } | Select-Object -Last 1)
    $failedCount = 0
    $passedCount = 0
    if ($summaryLine -match 'Failed:\s+(\d+)') { $failedCount = [int]$Matches[1] }
    if ($summaryLine -match 'Passed:\s+(\d+)') { $passedCount = [int]$Matches[1] }
    if ($summaryLine -match '^passed=(\d+) failed=(\d+)') {
      $passedCount = [int]$Matches[1]
      $failedCount = [int]$Matches[2]
    }
    $detail = if ($exit -eq 0 -and $failedCount -eq 0) {
      if ($passedCount -gt 0) { "$passedCount test(s) passed" } else { 'unit tests passed' }
    } elseif ($summaryLine) {
      $summaryLine.Trim()
    } else {
      "exit=$exit"
    }
    if ($exit -eq 0) {
      Add-Result 'core-tests' 'PASS' ((Get-Date) - $start).TotalSeconds "$detail; log: $log"
    } else {
      Add-Result 'core-tests' 'FAIL' ((Get-Date) - $start).TotalSeconds "$detail; log: $log"
      $failures++
    }
  }
}

# ---------------------------------------------------------------------------
# [3] protocol smoke tool (loopback)
# ---------------------------------------------------------------------------
if ($SkipSmokeTool) {
  Add-Result 'smoke-tool' 'SKIP' 0 '-SkipSmokeTool'
} else {
  $start = Get-Date
  $smokeProject = Join-Path $repoRoot 'app/tools/DshRemote.Smoke/DshRemote.Smoke.csproj'
  if (-not (Test-Path -LiteralPath $smokeProject)) {
    Add-Result 'smoke-tool' 'FAIL' 0 "project not found: $smokeProject"
    $failures++
  } else {
    $toolArgs = @('run', '--project', $smokeProject, '--', '--loopback', '--sessions', "$SessionsToShow", '--history', "$HistoryEvents")
    if ($PairingFile) { $toolArgs += @('--pairing', $PairingFile) }
    $output = & dotnet @toolArgs *>&1 | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE
    $log = Save-Lines 'smoke-tool' $output
    $sessionLine = ($output | Where-Object { $_ -match '^sessions:\s+\d+' } | Select-Object -Last 1)
    $invalid = ($output | Where-Object { $_ -match '^-+\s*$' } | Measure-Object).Count
    if ($exit -eq 0 -and $sessionLine) {
      Add-Result 'smoke-tool' 'PASS' ((Get-Date) - $start).TotalSeconds "$($sessionLine.Trim()); log: $log"
    } else {
      $tail = ($output | Select-Object -Last 3) -join ' / '
      Add-Result 'smoke-tool' 'FAIL' ((Get-Date) - $start).TotalSeconds "exit=$exit $tail; log: $log"
      $failures++
    }
  }
}

# ---------------------------------------------------------------------------
# [4] Android: build/install/launch/screenshot
# ---------------------------------------------------------------------------
$apk = $null
if ($SkipAndroid) {
  Add-Result 'android' 'SKIP' 0 '-SkipAndroid'
} else {
  . (Join-Path $PSScriptRoot 'android-env.ps1')
  try {
    $toolchain = Resolve-AndroidToolchain -AndroidSdkDirectory $AndroidSdkDirectory -JavaSdkDirectory $JavaSdkDirectory
  } catch {
    Add-Result 'android-toolchain' 'FAIL' 0 $_.Exception.Message
    $failures++
    $toolchain = $null
  }

  # [4a] APK (default Debug; -Release builds signed release; -Apk reuses any)
  $debugApk = Join-Path $repoRoot 'app/src/DshRemote.App.Android/bin/Debug/net10.0-android/dev.dshremote.app-Signed.apk'
  if ($toolchain) {
    if ($ApkPath) {
      if (-not (Test-Path -LiteralPath $ApkPath)) {
        Add-Result 'apk' 'FAIL' 0 "APK not found: $ApkPath"
        $failures++
      } else {
        $apk = (Resolve-Path -LiteralPath $ApkPath).Path
        Add-Result 'apk' 'PASS' 0 "reused $apk"
      }
    } elseif ($SkipApkBuild) {
      $candidate = if (Test-Path -LiteralPath $debugApk) {
        Get-Item -LiteralPath $debugApk
      } else {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist\apk') -Filter '*.apk' -File -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
      }
      if ($candidate) { $apk = $candidate.FullName; Add-Result 'apk' 'PASS' 0 "reused $apk" }
      else { Add-Result 'apk' 'FAIL' 0 'no Debug/dist APK (run without -SkipApkBuild or pass -Apk)'; $failures++ }
    } elseif ($Release) {
      # Signed Release build (M4 distribution path).
      $start = Get-Date
      $buildArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'build-release-apk.ps1'),
        '-DisplayVersion', $DisplayVersion, '-VersionCode', $VersionCode)
      if ($KeystorePath) { $buildArgs += @('-KeystorePath', $KeystorePath) }
      if (-not $StorePass) {
        $localPasswordFile = Join-Path $repoRoot 'app\keys\local-password.txt'
        if (Test-Path -LiteralPath $localPasswordFile) { $StorePass = (Get-Content -LiteralPath $localPasswordFile -Raw).Trim() }
      }
      if (-not $StorePass) {
        Add-Result 'apk' 'FAIL' 0 'no keystore password (set DSHREMOTE_KEYSTORE_PASS or run gen-signing-key.ps1)'
        $failures++
      } else {
        $env:DSHREMOTE_KEYSTORE_PASS = $StorePass
        $output = & $pwshExe @buildArgs *>&1 | ForEach-Object { "$_" }
        $exit = $LASTEXITCODE
        $log = Save-Lines 'apk-build' $output
        $apkLine = ($output | Where-Object { $_ -match '^\s*path\s*:' } | Select-Object -Last 1)
        $sizeLine = ($output | Where-Object { $_ -match '^\s*size\s*:' } | Select-Object -Last 1)
        if ($exit -eq 0) {
          $apk = if ($apkLine) { ($apkLine -replace '^\s*path\s*:\s*', '').Trim() } else { $null }
          $apk = if ($apk -and (Test-Path -LiteralPath $apk)) { $apk } else {
            (Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist\apk') -Filter '*.apk' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
          }
          Add-Result 'apk' 'PASS' ((Get-Date) - $start).TotalSeconds "$(($sizeLine ?? '').Trim()) log: $log"
        } else {
          Add-Result 'apk' 'FAIL' ((Get-Date) - $start).TotalSeconds "exit=$exit log: $log"
          $failures++
        }
      }
    } else {
      # Default for device/emulator development: Debug APK.
      $start = Get-Date
      $buildArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'build-apk.ps1'))
      if ($AndroidSdkDirectory) { $buildArgs += @('-AndroidSdkDirectory', $AndroidSdkDirectory) }
      if ($JavaSdkDirectory) { $buildArgs += @('-JavaSdkDirectory', $JavaSdkDirectory) }
      $output = & $pwshExe @buildArgs *>&1 | ForEach-Object { "$_" }
      $exit = $LASTEXITCODE
      $log = Save-Lines 'apk-build' $output
      $apkLine = ($output | Where-Object { $_ -match '^APK:\s*(.+?)\s+\(' } | Select-Object -Last 1)
      if ($exit -eq 0) {
        if ($apkLine -match '^APK:\s*(.+?)\s+\(') { $apk = $Matches[1].Trim() }
        if (-not ($apk -and (Test-Path -LiteralPath $apk)) -and (Test-Path -LiteralPath $debugApk)) { $apk = $debugApk }
        if ($apk) {
          Add-Result 'apk' 'PASS' ((Get-Date) - $start).TotalSeconds "Debug $([math]::Round((Get-Item -LiteralPath $apk).Length / 1MB, 1)) MB log: $log"
        } else {
          Add-Result 'apk' 'FAIL' ((Get-Date) - $start).TotalSeconds "build succeeded but no Debug APK found; log: $log"
          $failures++
        }
      } else {
        Add-Result 'apk' 'FAIL' ((Get-Date) - $start).TotalSeconds "exit=$exit log: $log"
        $failures++
      }
    }
  }

  # [4b..4d] install/launch/screenshot
  if ($toolchain -and $apk -and -not $SkipApkInstall) {
    $adb = if ($AdbPath) { $AdbPath } elseif ($toolchain.Adb) { $toolchain.Adb } else { $null }
    if (-not $adb -or -not (Test-Path -LiteralPath $adb)) {
      Add-Result 'adb' 'FAIL' 0 'adb not found (pass -AdbPath)'
      $failures++
    } else {
      # Target device: explicit -Serial/-Device wins, otherwise first online serial.
      $onlineSerials = @(& $adb devices 2>&1 | ForEach-Object {
        if ($_ -match '^(\S+)\s+device(\s|$)') { $Matches[1] }
      })
      $serial = $Serial
      if (-not $serial) { $serial = $onlineSerials | Select-Object -First 1 }
      if (-not $serial) {
        Add-Result 'adb' 'FAIL' 0 "no device online ($adb devices)"
        $failures++
      } elseif ($onlineSerials -notcontains $serial) {
        Add-Result 'adb' 'FAIL' 0 "serial '$serial' is not an online device (online: $($onlineSerials -join ', '))"
        $failures++
      } else {
        $adbArgs = @('-s', $serial)
        Add-Result 'adb' 'PASS' 0 "serial=$serial (online: $($onlineSerials -join ', '))"

        # install (auto-uninstall when the signing key changed)
        $start = Get-Date
        $installOut = & $adb @adbArgs install -r -t $apk 2>&1 | ForEach-Object { "$_" }
        $installExit = $LASTEXITCODE
        if ($installExit -ne 0 -and ($installOut -join "`n") -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures do not match') {
          Write-Host 'Signature changed: uninstalling the old app once, then reinstalling ...' -ForegroundColor Yellow
          & $adb @adbArgs uninstall dev.dshremote.app 2>&1 | Out-Null
          $installOut = & $adb @adbArgs install -r -t $apk 2>&1 | ForEach-Object { "$_" }
          $installExit = $LASTEXITCODE
        }
        $log = Save-Lines 'adb-install' $installOut
        $installText = $installOut -join "`n"
        $installOk = $installExit -eq 0 -and $installText -match 'Success'
        if ($installOk) {
          Add-Result 'apk-install' 'PASS' ((Get-Date) - $start).TotalSeconds "log: $log"
        } elseif ($installText -match 'Failure \[-99\]|OPLUS_ADB_INSTALL_CANCEL|PC install attack|INSTALL_FAILED_USER_RESTRICTED') {
          # Vendor anti-fraud guards (ColorOS/OPPO/OnePlus etc.) can block adb
          # installs even for a valid, correctly signed APK. This is a device
          # policy, not an APK problem: install the APK from the phone itself.
          Add-Result 'apk-install' 'FAIL' ((Get-Date) - $start).TotalSeconds "device blocked adb install (vendor guard, Failure [-99]); install the APK manually; log: $log"
          $failures++
        } else {
          Add-Result 'apk-install' 'FAIL' ((Get-Date) - $start).TotalSeconds "exit=$installExit; log: $log"
          $failures++
        }

        # push pairing (best effort; App still needs the manual "load pairing" action)
        $pairingPath = if ($PairingFile) { $PairingFile } else { Join-Path $env:USERPROFILE '.dsh\remote\pairing.json' }
        if (Test-Path -LiteralPath $pairingPath) {
          $remoteDir = '/sdcard/Android/data/dev.dshremote.app/files'
          & $adb @adbArgs shell mkdir -p $remoteDir 2>&1 | Out-Null
          $pushOut = & $adb @adbArgs push $pairingPath "$remoteDir/pairing.json" 2>&1 | ForEach-Object { "$_" }
          if (($pushOut -join "`n") -match '1 file pushed|pushed') {
            Add-Result 'pairing-push' 'PASS' 0 "$pairingPath -> $remoteDir/pairing.json"
          } else {
            Add-Result 'pairing-push' 'SKIP' 0 "adb push failed: $($pushOut -join ' ')"
          }
        } else {
          Add-Result 'pairing-push' 'SKIP' 0 'pairing.json not found'
        }

        # Also drop the APK where the phone's file manager can reach it, so a
        # vendor-blocked adb install has a manual path.
        & $adb @adbArgs push $apk /sdcard/Download/dshremote.apk 2>&1 | Out-Null

        # Pre-grant the notification permission (Android 13+) so the first-run
        # permission dialog cannot hide the app under the permission controller.
        & $adb @adbArgs shell pm grant dev.dshremote.app android.permission.POST_NOTIFICATIONS 2>&1 | Out-Null

        # launch + screenshot (only meaningful when the install succeeded)
        if (-not $installOk) {
          Add-Result 'app-launch' 'SKIP' 0 'APK not installed; install it on the device then rerun'
        } else {
          $start = Get-Date
          & $adb @adbArgs shell am force-stop dev.dshremote.app 2>&1 | Out-Null
          # Close any stale first-run permission dialog left on top of the app.
          & $adb @adbArgs shell am force-stop com.google.android.permissioncontroller 2>&1 | Out-Null
          Start-Sleep -Milliseconds 500
          & $adb @adbArgs shell monkey -p dev.dshremote.app -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
          # Emulators can need several seconds for the first JIT/AOT start; poll
          # instead of a single fixed sleep.
          $pidOf = $null
          $launchDeadline = (Get-Date).AddSeconds(25)
          while ((Get-Date) -lt $launchDeadline) {
            $pidOf = & $adb @adbArgs shell pidof dev.dshremote.app 2>&1
            if ($pidOf -match '\d+') { break }
            Start-Sleep -Milliseconds 1000
          }
          # Wait for the app window to own focus (splash -> first frame) so the
          # screenshot is evidence of the real UI, not the launcher splash.
          $focusDeadline = (Get-Date).AddSeconds(15)
          while ((Get-Date) -lt $focusDeadline) {
            $focusLine = & $adb @adbArgs shell dumpsys window 2>&1 | Select-String -Pattern 'mCurrentFocus' | Select-Object -First 1
            if ($focusLine -and $focusLine -match 'dev\.dshremote\.app') { break }
            Start-Sleep -Milliseconds 750
          }
          Start-Sleep -Milliseconds 1200
          $screenshot = Join-Path $LogDir 'app-launch.png'
          & $adb @adbArgs shell screencap -p /sdcard/dshremote-e2e.png 2>&1 | Out-Null
          & $adb @adbArgs pull /sdcard/dshremote-e2e.png $screenshot 2>&1 | Out-Null
          & $adb @adbArgs shell rm -f /sdcard/dshremote-e2e.png 2>&1 | Out-Null
          $hasShot = Test-Path -LiteralPath $screenshot
          if ($pidOf -match '\d+') {
            Add-Result 'app-launch' 'PASS' ((Get-Date) - $start).TotalSeconds "serial=$serial pid=$($pidOf.Trim()) screenshot: $screenshot"
          } else {
            # Keep the crash evidence: filtered logcat tail for the app/runtime.
            $logcatPath = Save-Lines 'logcat-launch' @(& $adb @adbArgs logcat -d -t 400 2>&1 | ForEach-Object { "$_" })
            $keyLines = @(Get-Content -LiteralPath $logcatPath -ErrorAction SilentlyContinue |
              Where-Object { $_ -match 'FATAL EXCEPTION|AndroidRuntime|dshremote|DshRemote|MONO|monodroid|dotnet|ActivityManager: Start proc|Force finishing' } |
              Select-Object -First 8)
            if ($keyLines.Count -gt 0) {
              Write-Host '--- logcat (key lines) ---' -ForegroundColor Red
              $keyLines | ForEach-Object { Write-Host "  $_" }
            }
            Add-Result 'app-launch' 'FAIL' ((Get-Date) - $start).TotalSeconds "pid not running after launch (crash?); logcat: $logcatPath; screenshot exists: $hasShot"
            $failures++
          }
        }
      }
    }
  } elseif ($toolchain -and $apk -and $SkipApkInstall) {
    Add-Result 'android-install' 'SKIP' 0 '-SkipApkInstall'
  }
}

# ---------------------------------------------------------------------------
# summary
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '========== E2E summary ==========' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "logs: $LogDir"
if ($failures -eq 0) {
  Write-Host 'E2E: PASS' -ForegroundColor Green
  exit 0
}
Write-Host "E2E: FAIL ($failures failed step(s))" -ForegroundColor Red
exit 1

