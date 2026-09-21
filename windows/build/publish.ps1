#requires -Version 5.1
<#
.SYNOPSIS
    Publish Local Caption for the G15 (SPEC-WINDOWS.md §11).

.DESCRIPTION
    Self-contained, ReadyToRun, win-x64 — .NET 10, no ARM64 target (W1 settled on x64).

    The pruning below is not cosmetic. Whisper.net's runtime packages copy *every* platform's
    native libraries into the output rather than only the ones the chosen RID needs, so a
    plain publish carries Linux .so files, macOS .dylibs and ARM builds that this application
    cannot load on any machine it will ever run on. On this project that is most of the
    payload.

    What stays: win-x64, and the `cuda` folder — which is where Whisper.net looks for the GPU
    backend, and which is not RID-named.

.PARAMETER Output
    Where to publish. Defaults to `windows/publish`.

.PARAMETER KeepAllRuntimes
    Skip the pruning, to compare against an untouched publish.
#>
[CmdletBinding()]
param(
    [string] $Output,
    [switch] $KeepAllRuntimes
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'cuda.ps1')

$windows = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $windows 'publish' }

# The .NET 10 SDK is not always on PATH here: the G15 shipped with the runtime only, and the
# SDK was installed per-user into $HOME\.dotnet (README). Look there before giving up.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -or -not (& $dotnet --list-sdks 2>$null)) {
    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local }
}
if (-not $dotnet) { throw "No .NET SDK found. Install the .NET 10 SDK — see windows/README.md." }

Write-Host "Publishing to $Output"

# Publishing clears the output folder first. If Local Caption is running FROM that folder,
# that means deleting an open app out from under a session that may be recording: some files
# go, the locked ones stay, and what is left neither runs nor records. Refuse, and say why.
$full = [System.IO.Path]::GetFullPath($Output).TrimEnd('\')
$inUse = Get-Process LocalCaption -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith($full + '\', [System.StringComparison]::OrdinalIgnoreCase) }
if ($inUse) {
    throw "Local Caption is running from $full (PID $($inUse.Id -join ', ')). Close it first - if it is recording, Stop and save - or publish somewhere else with -Output."
}

# The CUDA redistributables are in no NuGet package — someone downloaded them from NVIDIA and
# put them beside the executable by hand (README, "Packaging"). Clearing the output used to
# take them with it, and the next run fell back to the CPU without a word. Carry them over.
$stash = $null
if (Test-Path $Output) {
    $cuda = Get-CudaRuntimeDll $Output
    if ($cuda) {
        $stash = Join-Path ([System.IO.Path]::GetDirectoryName($Output)) ('.cuda-stash-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $stash | Out-Null
        $cuda | Move-Item -Destination $stash
        Write-Host ("  kept {0} CUDA runtime DLL(s) aside" -f $cuda.Count)
    }
    Remove-Item $Output -Recurse -Force
}

& $dotnet publish (Join-Path $windows 'src/LocalCaption.App') `
    -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE" }

if ($stash) {
    Get-ChildItem $stash -File | Move-Item -Destination $Output -Force
    Remove-Item $stash -Recurse -Force
    Write-Host '  restored the CUDA runtime DLLs'
}

$before = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum

if (-not $KeepAllRuntimes) {
    $runtimes = Join-Path $Output 'runtimes'
    if (Test-Path $runtimes) {
        Get-ChildItem $runtimes -Directory |
            Where-Object { $_.Name -notin @('win-x64', 'cuda') } |
            ForEach-Object {
                Write-Host "  dropping runtimes/$($_.Name)"
                Remove-Item $_.FullName -Recurse -Force
            }
    }

    # Apple's Metal shader source, copied in by the same packages. Harmless, and pointless.
    Get-ChildItem $Output -Filter '*.metal' -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

$after = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host ''
Write-Host ("  before pruning  {0:N0} MB" -f ($before / 1MB))
Write-Host ("  after pruning   {0:N0} MB" -f ($after / 1MB))
Write-Host ''

Write-Host "Done. Run $Output\LocalCaption.exe"
