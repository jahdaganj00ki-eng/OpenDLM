<#
.SYNOPSIS
    Registers (or unregisters) the OpenDLM native messaging host for Chromium browsers.

.DESCRIPTION
    Writes the native messaging manifest com.opendlm.host.json into
    %LOCALAPPDATA%\OpenDLM and points the Chrome, Edge and Brave registry keys at it.

    The manifest tells the browser which executable to launch when the OpenDLM
    extension calls chrome.runtime.sendNativeMessage("com.opendlm.host", ...).
    Chromium refuses wildcards in allowed_origins, so the ID of your unpacked
    extension must be supplied with -ExtensionId.

    The script is idempotent: running it twice produces the same result and
    prints exactly what it changed.

.PARAMETER AppPath
    Optional path to OpenDLM.exe (or to its installation folder). It is stored as
    the InstallPath value under HKCU:\Software\OpenDLM so the native host can
    start the desktop app on demand, and it is used to find a neighbouring
    OpenDLMNativeHost.exe.

.PARAMETER HostPath
    Optional path to OpenDLMNativeHost.exe. Use this when the host does not sit
    next to the app and is not in a local build output folder.

.PARAMETER ExtensionId
    One or more Chromium extension IDs to allow. Repeat the parameter to allow
    several (for example one per browser profile) or pass a comma separated list.

.PARAMETER Unregister
    Removes the registry keys and the manifest file instead of creating them.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -ExtensionId abcdefghijklmnopabcdefghijklmnop

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -Unregister
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AppPath,

    [Parameter(Mandatory = $false)]
    [string]$HostPath,

    [Parameter(Mandatory = $false)]
    [string[]]$ExtensionId,

    [Parameter(Mandatory = $false)]
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$HostName    = 'com.opendlm.host'
$ProductName = 'OpenDLM'
$ManifestFileName = 'com.opendlm.host.json'
$HostExeName = 'OpenDLMNativeHost.exe'
$AppExeName  = 'OpenDLM.exe'

# Chromium looks up native messaging hosts under these per-browser registry keys.
$BrowserKeys = @(
    @{ Name = 'Chrome'; Path = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts' },
    @{ Name = 'Edge';   Path = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts' },
    @{ Name = 'Brave';  Path = 'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts' }
)

$InstallKeyPath = 'HKCU:\Software\OpenDLM'
$InstallValueName = 'InstallPath'

$script:Changes = New-Object System.Collections.Generic.List[string]

function Write-Change {
    param([string]$Message)
    $script:Changes.Add($Message)
    Write-Host "  $Message"
}

function Write-Headline {
    param([string]$Message)
    Write-Host ''
    Write-Host $Message -ForegroundColor Cyan
}

function Get-LocalRoot {
    $base = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($base)) {
        throw 'LOCALAPPDATA is not set, so the OpenDLM configuration folder cannot be located.'
    }
    return (Join-Path $base $ProductName)
}

function Resolve-FullPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }
    try {
        return [System.IO.Path]::GetFullPath($Path)
    } catch {
        return $null
    }
}

function Get-RepoRoot {
    # The script lives in <repo>\scripts, so the repository root is one level up.
    if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return $null
    }
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
}

function Find-HostExecutable {
    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($HostPath)) {
        $candidates.Add($HostPath)
    }

    # 1. Next to the desktop app, which is how a normal installation is laid out.
    if (-not [string]::IsNullOrWhiteSpace($AppPath)) {
        $appFull = Resolve-FullPath $AppPath
        if ($appFull) {
            $appDirectory = $null
            if ([System.IO.Path]::GetExtension($appFull) -ieq '.exe') {
                $appDirectory = [System.IO.Path]::GetDirectoryName($appFull)
            } elseif (Test-Path -LiteralPath $appFull -PathType Container) {
                $appDirectory = $appFull
            }
            if ($appDirectory) {
                $candidates.Add((Join-Path $appDirectory $HostExeName))
            }
        }
    }

    # 2. A build output inside this repository.
    $repoRoot = Get-RepoRoot
    if ($repoRoot) {
        foreach ($configuration in @('Release', 'Debug')) {
            $candidates.Add((Join-Path $repoRoot "src\$ProductName.NativeHost\bin\$configuration\net8.0-windows\$HostExeName"))
        }
    }

    # 3. Beside this script, for a hand-assembled portable copy.
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $candidates.Add((Join-Path $PSScriptRoot $HostExeName))
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }
        $full = Resolve-FullPath $candidate
        if ($full -and (Test-Path -LiteralPath $full -PathType Leaf)) {
            return $full
        }
    }

    return $null
}

function Get-NormalizedExtensionIds {
    $result = New-Object System.Collections.Generic.List[string]

    if (-not $ExtensionId) {
        return $result
    }

    foreach ($raw in $ExtensionId) {
        if ([string]::IsNullOrWhiteSpace($raw)) {
            continue
        }
        foreach ($piece in ($raw -split '[,;]')) {
            $id = $piece.Trim()
            if ([string]::IsNullOrWhiteSpace($id)) {
                continue
            }
            $id = $id -replace '^chrome-extension://', ''
            $id = $id.TrimEnd('/')
            if ($id -and -not $result.Contains($id)) {
                $result.Add($id)
            }
        }
    }

    return $result
}

function Remove-Registration {
    Write-Headline "Unregistering the $HostName native messaging host"

    foreach ($browser in $BrowserKeys) {
        $keyPath = Join-Path $browser.Path $HostName
        if (Test-Path -LiteralPath $keyPath) {
            Remove-Item -LiteralPath $keyPath -Recurse -Force
            Write-Change "Removed registry key $keyPath ($($browser.Name))."
        } else {
            Write-Change "Registry key $keyPath ($($browser.Name)) was already absent."
        }

        # Tidy up an empty NativeMessagingHosts container; leave any other hosts alone.
        if (Test-Path -LiteralPath $browser.Path) {
            $remaining = @(Get-ChildItem -LiteralPath $browser.Path -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) {
                Remove-Item -LiteralPath $browser.Path -Force
                Write-Change "Removed the now empty container $($browser.Path)."
            }
        }
    }

    $manifestPath = Join-Path (Get-LocalRoot) $ManifestFileName
    if (Test-Path -LiteralPath $manifestPath) {
        Remove-Item -LiteralPath $manifestPath -Force
        Write-Change "Removed the manifest $manifestPath."
    } else {
        Write-Change "The manifest $manifestPath was already absent."
    }

    Write-Host ''
    Write-Host "Unregistered. The $InstallValueName value under $InstallKeyPath was left untouched because the app owns it." -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Summary of changes:' -ForegroundColor Cyan
    if ($script:Changes.Count -eq 0) {
        Write-Host '  (nothing to do)'
    }
    return
}

# ---------------------------------------------------------------------- #
# Unregister
# ---------------------------------------------------------------------- #

if ($Unregister) {
    Remove-Registration
    exit 0
}

# ---------------------------------------------------------------------- #
# Register
# ---------------------------------------------------------------------- #

Write-Headline "Registering the $HostName native messaging host"

$hostExecutable = Find-HostExecutable
if (-not $hostExecutable) {
    Write-Host ''
    Write-Host "ERROR: $HostExeName could not be found." -ForegroundColor Red
    Write-Host 'Build the native host first, or pass its location explicitly:'
    Write-Host "  dotnet build `"$((Get-RepoRoot))\src\$ProductName.NativeHost\$ProductName.NativeHost.csproj`""
    Write-Host "  powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -ExtensionId <ID> -HostPath <path to $HostExeName>"
    exit 1
}

$extensionIds = @(Get-NormalizedExtensionIds)
$allowedOrigins = New-Object System.Collections.Generic.List[string]
foreach ($id in $extensionIds) {
    if ($id -notmatch '^[a-p]{32}$') {
        Write-Host "  WARNING: '$id' does not look like a Chromium extension ID (32 characters, a-p). It was still written." -ForegroundColor Yellow
    }
    $allowedOrigins.Add("chrome-extension://$id/")
}

$localRoot = Get-LocalRoot
if (-not (Test-Path -LiteralPath $localRoot)) {
    New-Item -ItemType Directory -Path $localRoot -Force | Out-Null
    Write-Change "Created $localRoot."
} else {
    Write-Change "Using the existing folder $localRoot."
}

# ---------------------------------------------------------------------- #
# Native messaging manifest
# ---------------------------------------------------------------------- #

$manifestPath = Join-Path $localRoot $ManifestFileName
$manifest = [ordered]@{
    name            = $HostName
    description     = "$ProductName native messaging host bridging the browser extension to the $ProductName desktop app."
    path            = $hostExecutable
    type            = 'stdio'
    allowed_origins = @($allowedOrigins)
}

$json = $manifest | ConvertTo-Json -Depth 5
# ConvertTo-Json emits a UTF-8 BOM-free string through Set-Content -Encoding UTF8 in
# PowerShell 5.1 is BOM-bearing, which Chromium tolerates, but ASCII keeps it clean.
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Change "Wrote the manifest $manifestPath."
Write-Change "  host executable: $hostExecutable"
if ($allowedOrigins.Count -gt 0) {
    Write-Change "  allowed origins: $($allowedOrigins -join ', ')"
} else {
    Write-Change '  allowed origins: (none)'
}

# ---------------------------------------------------------------------- #
# Registry keys
# ---------------------------------------------------------------------- #

foreach ($browser in $BrowserKeys) {
    $keyPath = Join-Path $browser.Path $HostName
    $existed = Test-Path -LiteralPath $keyPath

    if (-not (Test-Path -LiteralPath $browser.Path)) {
        New-Item -Path $browser.Path -Force | Out-Null
    }
    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -Path $keyPath -Name '(default)' -Value $manifestPath

    if ($existed) {
        Write-Change "Updated $keyPath ($($browser.Name)) -> $manifestPath"
    } else {
        Write-Change "Created $keyPath ($($browser.Name)) -> $manifestPath"
    }
}

# ---------------------------------------------------------------------- #
# Optional: remember where the desktop app lives so the host can start it
# ---------------------------------------------------------------------- #

if (-not [string]::IsNullOrWhiteSpace($AppPath)) {
    $appFull = Resolve-FullPath $AppPath
    if (-not $appFull) {
        Write-Host "  WARNING: -AppPath '$AppPath' could not be resolved; InstallPath was not written." -ForegroundColor Yellow
    } else {
        $installPath = $appFull
        if ([System.IO.Path]::GetExtension($appFull) -ieq '.exe') {
            $installPath = [System.IO.Path]::GetDirectoryName($appFull)
        }

        if (-not (Test-Path -LiteralPath $InstallKeyPath)) {
            New-Item -Path $InstallKeyPath -Force | Out-Null
        }
        $previous = (Get-ItemProperty -Path $InstallKeyPath -Name $InstallValueName -ErrorAction SilentlyContinue).$InstallValueName
        Set-ItemProperty -Path $InstallKeyPath -Name $InstallValueName -Value $installPath
        if ($previous) {
            Write-Change "Set $InstallKeyPath\$InstallValueName = $installPath (was $previous)."
        } else {
            Write-Change "Set $InstallKeyPath\$InstallValueName = $installPath."
        }
    }
}

# ---------------------------------------------------------------------- #
# Guidance
# ---------------------------------------------------------------------- #

Write-Host ''
if ($allowedOrigins.Count -eq 0) {
    Write-Host 'ACTION REQUIRED: no -ExtensionId was supplied, so allowed_origins is empty.' -ForegroundColor Yellow
    Write-Host 'Chromium rejects wildcards in allowed_origins, so the extension cannot connect yet.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'How to find your unpacked extension ID:'
    Write-Host '  1. Open chrome://extensions (or edge://extensions, brave://extensions).'
    Write-Host '  2. Turn on Developer mode in the top right corner.'
    Write-Host "  3. Find the OpenDLM card and copy the ID shown under the name."
    Write-Host '  4. Re-run this script with that ID:'
    Write-Host "     powershell -ExecutionPolicy Bypass -File scripts\register-native-host.ps1 -ExtensionId <ID>"
} else {
    Write-Host 'Next steps:'
    Write-Host '  1. Reload the OpenDLM extension card in chrome://extensions so it picks up the registration.'
    Write-Host '  2. Open the extension popup; the status row should read "Connected".'
    Write-Host "  3. If it does not, check %LOCALAPPDATA%\$ProductName\logs\nativehost.log"
}

Write-Host ''
Write-Host 'Summary of changes:' -ForegroundColor Cyan
foreach ($change in $script:Changes) {
    Write-Host "  $change"
}
Write-Host ''
