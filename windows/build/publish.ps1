#requires -Version 5.1
<#
.SYNOPSIS
    Publish Local Caption (SPEC-WINDOWS.md §11) for x64 or ARM64 Windows.

.DESCRIPTION
    Self-contained, ReadyToRun, .NET 10. win-x64 by default; -Runtime win-arm64 builds the
    native ARM64 app (Snapdragon and other Windows-on-ARM PCs: Windows 10 on ARM cannot run
    x64 at all, and on Windows 11 a native build avoids emulating whisper.cpp). See
    specs/SPEC-16-windows-parity.md, "Compatibility target".

    The pruning below is not cosmetic. Whisper.net's runtime packages copy *every* platform's
    native libraries into the output rather than only the ones the chosen RID needs, so a
    plain publish carries Linux .so files, macOS .dylibs and ARM builds that this application
    cannot load on any machine it will ever run on. On this project that is most of the
    payload.

    What stays: runtimes/<rid>, and inside the `cuda` and `noavx` folders (where Whisper.net
    looks for the GPU backend, and for the CPU build used on processors without AVX2) only
    <rid>. CUDA is x64-only: there is no CUDA for Windows on ARM, so an ARM64 build has no
    `cuda` folder, never carries CUDA DLLs, and runs on the CPU (NEON).

.PARAMETER Runtime
    win-x64 (default) or win-arm64.

.PARAMETER Output
    Where to publish. Defaults to `windows/publish` for win-x64 and `windows/publish-arm64`
    for win-arm64, so the two can sit side by side.

.PARAMETER KeepAllRuntimes
    Skip the pruning, to compare against an untouched publish.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Output,
    [switch] $KeepAllRuntimes
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'cuda.ps1')

$windows = Split-Path -Parent $PSScriptRoot
$withCuda = $Runtime -eq 'win-x64'
if (-not $Output) { $Output = Join-Path $windows $(if ($withCuda) { 'publish' } else { 'publish-arm64' }) }

# The .NET 10 SDK is not always on PATH here: the G15 shipped with the runtime only, and the
# SDK was installed per-user into $HOME\.dotnet (README). Look there before giving up.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -or -not (& $dotnet --list-sdks 2>$null)) {
    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local }
}
if (-not $dotnet) { throw "No .NET SDK found. Install the .NET 10 SDK — see windows/README.md." }

Write-Host "Publishing $Runtime to $Output"

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
# x64 only: an ARM64 build cannot load them, so any found there are simply dropped.
$stash = $null
if (Test-Path $Output) {
    $cuda = if ($withCuda) { Get-CudaRuntimeDll $Output } else { @() }
    if ($cuda) {
        $stash = Join-Path ([System.IO.Path]::GetDirectoryName($Output)) ('.cuda-stash-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $stash | Out-Null
        $cuda | Move-Item -Destination $stash
        Write-Host ("  kept {0} CUDA runtime DLL(s) aside" -f $cuda.Count)
    }
    Remove-Item $Output -Recurse -Force
}

& $dotnet publish (Join-Path $windows 'src/LocalCaption.App') `
    -c Release -r $Runtime --self-contained -p:PublishReadyToRun=true -o $Output
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
        # Whisper.net's variant folders hold one subfolder per RID: keep ours, drop the rest.
        # cuda is kept on x64 only; on ARM64 it holds nothing this build can load.
        $variants = @('noavx')
        if ($withCuda) { $variants += 'cuda' }
        foreach ($dir in @(Get-ChildItem $runtimes -Directory)) {
            if ($dir.Name -eq $Runtime) { continue }
            if ($dir.Name -in $variants) {
                foreach ($sub in @(Get-ChildItem $dir.FullName -Directory | Where-Object { $_.Name -ne $Runtime })) {
                    Write-Host "  dropping runtimes/$($dir.Name)/$($sub.Name)"
                    Remove-Item $sub.FullName -Recurse -Force
                }
                if (-not @(Get-ChildItem $dir.FullName -Force).Count) { Remove-Item $dir.FullName -Recurse -Force }
                continue
            }
            Write-Host "  dropping runtimes/$($dir.Name)"
            Remove-Item $dir.FullName -Recurse -Force
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

# whisper.cpp's native DLLs link the Visual C++ runtime dynamically (MSVCP140, VCRUNTIME140,
# and on x64 also VCRUNTIME140_1 and VCOMP140). A self-contained .NET publish does not carry
# them, so on a PC without the Microsoft Visual C++ 2015-2022 Redistributable no speech model
# can load. The installer built by package.ps1 installs it as a prerequisite; a copied folder
# does not, and the app then says so in its status line (BackendProbe.MissingNativeRuntime).
$arch = $Runtime.Substring(4)
Write-Host "  The PC it runs on needs the Microsoft Visual C++ 2015-2022 Redistributable ($arch)."
Write-Host '  The installer from package.ps1 installs it; a copied folder does not.'
Write-Host ''

Write-Host "Done. Run $Output\LocalCaption.exe"
