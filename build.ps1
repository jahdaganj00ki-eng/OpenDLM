#requires -Version 7.2
<#
.SYNOPSIS
    Builds, tests and optionally publishes OpenDLM.

.DESCRIPTION
    This script is the single definition of "how OpenDLM is built". GitHub Actions
    calls it, so a local run and a CI run cannot drift apart.

    OpenDLM has no NuGet dependencies, so restore works with no network access.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER Runtime
    The .NET runtime identifier to publish for. Defaults to win-x64.

.PARAMETER OutputDirectory
    Where published output is staged. Defaults to artifacts.

.PARAMETER SkipTests
    Build only, without running the engine test suite.

.PARAMETER Publish
    Also publish the application and the native messaging host into a single folder.

.PARAMETER SelfContained
    When publishing, bundle the .NET runtime so no prerequisite is needed.

.EXAMPLE
    ./build.ps1
    Restores, builds and tests the whole solution.

.EXAMPLE
    ./build.ps1 -Publish -SelfContained
    Produces a runnable bundle in artifacts/OpenDLM.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Runtime = 'win-x64',

    [string] $OutputDirectory = 'artifacts',

    [switch] $SkipTests,

    [switch] $Publish,

    [switch] $SelfContained
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Get-Location).Path
}

$solution = Join-Path $repoRoot 'OpenDLM.sln'
if (-not (Test-Path $solution)) {
    throw "Could not find OpenDLM.sln next to build.ps1 (looked in $repoRoot)."
}

$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkGray
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkGray
}

function Invoke-DotNet {
    param([string[]] $Arguments)

    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Step "Restoring ($Configuration)"
Invoke-DotNet @('restore', $solution)

Write-Step "Building ($Configuration)"
Invoke-DotNet @('build', $solution, '-c', $Configuration, '--no-restore')

if (-not $SkipTests) {
    Write-Step 'Running the engine test suite'
    Invoke-DotNet @(
        'run',
        '--project', (Join-Path $repoRoot 'tests/OpenDLM.Tests/OpenDLM.Tests.csproj'),
        '-c', $Configuration,
        '--no-build'
    )
}
else {
    Write-Host 'Tests were skipped at your request.' -ForegroundColor Yellow
}

if ($Publish) {
    $bundle = Join-Path $repoRoot (Join-Path $OutputDirectory 'OpenDLM')

    Write-Step "Publishing to $bundle"
    if (Test-Path $bundle) {
        Remove-Item -Recurse -Force $bundle
    }
    New-Item -ItemType Directory -Force -Path $bundle | Out-Null

    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

    # The application and the native messaging host must live side by side: the
    # host is launched by the browser and relays to the application over a pipe.
    foreach ($project in @('src/OpenDLM.App/OpenDLM.App.csproj', 'src/OpenDLM.NativeHost/OpenDLM.NativeHost.csproj')) {
        Invoke-DotNet @(
            'publish', (Join-Path $repoRoot $project),
            '-c', $Configuration,
            '-r', $Runtime,
            "--self-contained=$selfContainedValue",
            '-o', $bundle
        )
    }

    Copy-Item -Recurse -Force (Join-Path $repoRoot 'extension') $bundle
    Copy-Item -Recurse -Force (Join-Path $repoRoot 'scripts') $bundle
    Copy-Item -Force (Join-Path $repoRoot 'README.md') $bundle

    Write-Host ''
    Write-Host "Bundle ready: $bundle" -ForegroundColor Green
    Get-ChildItem -Path $bundle -File | Select-Object Name, Length | Format-Table -AutoSize
}

$stopwatch.Stop()
Write-Host ''
Write-Host ("Done in {0:0.0}s" -f $stopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
