<#
.SYNOPSIS
    Run the repository's xUnit test projects, falling back to an in-process runner when `dotnet test` cannot
    start in the current environment.

.DESCRIPTION
    `dotnet test` (VSTest) starts a testhost that opens the parent process handle to watch for runner exit.
    In parent-managed/confined shells that can fail with Win32Exception 5 ("access denied") before any test
    runs. This script builds each test project and executes its assembly with scripts/test-fallback
    (xunit.runner.utility, in-process) instead; the same pass/fail count is printed.

    With -PreferDotnetTest the script first tries the normal `dotnet test` path and only falls back when it
    aborts. Build output (test DLLs, fallback runner copy) stays under the ignored bin/ directories.

.EXAMPLE
    pwsh -File scripts/run-tests.ps1
    pwsh -File scripts/run-tests.ps1 -Filter Compaction -Configuration Debug
    pwsh -File scripts/run-tests.ps1 -Project DshRemote.Core.Tests
    pwsh -File scripts/run-tests.ps1 -PreferDotnetTest
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$Filter,

    [ValidateSet('DshRemote.Core.Tests', 'DshRemote.App.Tests')]
    [string]$Project,

    [switch]$PreferDotnetTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$testProjects = @(
    @{ Name = 'DshRemote.Core.Tests'; Project = 'app/tests/DshRemote.Core.Tests/DshRemote.Core.Tests.csproj'; Assembly = 'DshRemote.Core.Tests.dll' },
    @{ Name = 'DshRemote.App.Tests'; Project = 'app/tests/DshRemote.App.Tests/DshRemote.App.Tests.csproj'; Assembly = 'DshRemote.App.Tests.dll' }
)
if ($Project) {
    $testProjects = @($testProjects | Where-Object { $_.Name -eq $Project })
}

$runnerProject = Join-Path $repoRoot 'scripts/test-fallback/DshTestFallback.csproj'
$runnerOut = Join-Path $repoRoot "scripts/test-fallback/bin/$Configuration/net10.0"

if ($PreferDotnetTest) {
    Write-Host '[*] Trying the normal dotnet test path first...' -ForegroundColor Cyan
    $failed = $false
    foreach ($test in $testProjects) {
        $project = Join-Path $repoRoot $test.Project
        $arguments = @('test', $project, '-c', $Configuration, '--nologo', '-v', 'minimal')
        if ($Filter) {
            $arguments += @('--filter', $Filter)
        }

        $output = & dotnet @arguments 2>&1 | ForEach-Object { "$_" }
        $output | Select-Object -Last 8 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            $failed = $true
            break
        }
    }

    if (-not $failed) {
        Write-Host '[+] dotnet test completed successfully.' -ForegroundColor Green
        exit 0
    }

    Write-Host '[!] dotnet test failed/aborted; switching to the in-process fallback runner.' -ForegroundColor Yellow
}

Write-Host "[*] Building fallback runner ($Configuration)..." -ForegroundColor Cyan
& dotnet build $runnerProject -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'failed to build scripts/test-fallback' }

$runnerDll = Join-Path $runnerOut 'DshTestFallback.dll'
$runtimeConfig = Join-Path $runnerOut 'DshTestFallback.runtimeconfig.json'
if (-not (Test-Path $runnerDll)) { throw "fallback runner not found: $runnerDll" }

$exitCode = 0
foreach ($test in $testProjects) {
    $projectPath = Join-Path $repoRoot $test.Project
    Write-Host ""
    Write-Host "[*] Building $($test.Name) ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $projectPath -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "failed to build $($test.Name)" }

    $testBin = Join-Path $repoRoot "app/tests/$($test.Name)/bin/$Configuration/net10.0"
    $testAssembly = Join-Path $testBin $test.Assembly
    if (-not (Test-Path $testAssembly)) { throw "test assembly not found: $testAssembly" }

    # The fallback runner executes inside the test output directory so the test assembly's own dependencies
    # (DshRemote.Core/.App, Avalonia, xunit) resolve exactly like they do for the normal testhost.
    Copy-Item $runnerDll $testBin -Force
    Copy-Item $runtimeConfig $testBin -Force
    Get-ChildItem $runnerOut -Filter 'xunit*.dll' | Copy-Item -Destination $testBin -Force

    Write-Host "[*] Running $($test.Name)..." -ForegroundColor Cyan
    Push-Location $testBin
    try {
        $arguments = @('exec', 'DshTestFallback.dll', $test.Assembly)
        if ($Filter) { $arguments += $Filter }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE }
    }
    finally {
        Pop-Location
    }
}

if ($exitCode -eq 0) {
    Write-Host ''
    Write-Host '[+] All test projects passed.' -ForegroundColor Green
}

exit $exitCode
