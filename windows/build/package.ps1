#requires -Version 5.1
<#
.SYNOPSIS
    Build the Local Caption installer (SPEC-WINDOWS.md §11).

.DESCRIPTION
    Publishes, then packs with Velopack — a per-user install needing no admin rights, with
    delta updates and a clean handling of the native DLL payload.

    Not code-signed. §11 settled that: for one ASUS used by its owner, no certificate is
    needed — click through the SmartScreen interstitial once and add a Defender exclusion for
    the install folder. An OV certificate now requires a hardware token or cloud HSM and still
    builds SmartScreen reputation slowly, which buys nothing here.

.PARAMETER Version
    Package version, e.g. 1.0.0. Velopack requires SemVer.

.PARAMETER CudaDirectory
    Folder holding the CUDA redistributable DLLs (cublas64_*, cublasLt64_*, cudart64_*).
    §5.2 bundles the CUDA runtime with the installer, but those files are in no NuGet package
    — they come from NVIDIA's redist archives. Without them the GPU backend cannot load and
    Whisper.net drops to the CPU in silence (BENCH-RESULTS.md §1). Omit at your peril.

.EXAMPLE
    ./package.ps1 -Version 1.0.0 -CudaDirectory C:\cuda-redist\bin
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $CudaDirectory,
    [string] $Output
)

$ErrorActionPreference = 'Stop'

$windows = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $windows 'publish'
if (-not $Output) { $Output = Join-Path $windows 'artifacts' }

& (Join-Path $PSScriptRoot 'publish.ps1') -Output $staging

if ($CudaDirectory) {
    if (-not (Test-Path $CudaDirectory)) { throw "CUDA directory not found: $CudaDirectory" }
    $dlls = Get-ChildItem $CudaDirectory -Filter '*.dll' -File |
        Where-Object { $_.Name -match '^(cublas64_|cublasLt64_|cudart64_|nvcudart)' }
    if (-not $dlls) { throw "No CUDA runtime DLLs in $CudaDirectory" }

    # nvcudart_hybrid64.dll ships with the display driver, not in any CUDA redistributable
    # archive, and it lives in the DriverStore where the loader will not find it. Confirmed
    # against driver 616.92 — see BENCH-RESULTS.md §1.
    if (-not ($dlls.Name -contains 'nvcudart_hybrid64.dll')) {
        $hybrid = Get-ChildItem "$env:SystemRoot\System32\DriverStore\FileRepository" `
                    -Filter 'nvcudart_hybrid64.dll' -Recurse -ErrorAction SilentlyContinue |
                  Select-Object -First 1
        if ($hybrid) {
            Copy-Item $hybrid.FullName -Destination $staging -Force
            Write-Host '  took nvcudart_hybrid64.dll from the driver store'
        }
        else { Write-Warning 'nvcudart_hybrid64.dll not found; the CUDA backend will not load.' }
    }

    # Beside the executable, which is where BackendProbe looks first and where the loader
    # will find them without reaching into a system-wide CUDA install. §11 is explicit that
    # nothing should load from outside the install directory.
    $dlls | Copy-Item -Destination $staging -Force
    Write-Host ("  bundled {0} CUDA runtime DLL(s)" -f $dlls.Count)
}
else {
    Write-Warning ("No -CudaDirectory given. This installer will run on the CPU only, " +
                   "which BENCH-RESULTS.md measures at 17 s per window for large-v3-turbo — " +
                   "so §5.8 will fall back to small.en and captions will lag.")
}

# vpk refuses to overwrite a release of the same version, which turns an ordinary rebuild
# into an error. Clearing the output is what "build the installer" means here — the releases
# that matter live wherever they are deployed, not in the build directory.
if (Test-Path $Output) { Get-ChildItem $Output -File | Remove-Item -Force }

$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    $vpk = Get-Command (Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe') -ErrorAction SilentlyContinue
}
if (-not $vpk) { throw "Velopack CLI not found. Install it with: dotnet tool install -g vpk" }

New-Item -ItemType Directory -Force $Output | Out-Null

& $vpk.Source pack `
    --packId LocalCaption `
    --packTitle 'Local Caption' `
    --packVersion $Version `
    --packDir $staging `
    --mainExe LocalCaption.exe `
    --outputDir $Output
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with $LASTEXITCODE" }

Write-Host ''
Get-ChildItem $Output | ForEach-Object { Write-Host ("  {0,-46} {1,8:N0} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ''
Write-Host "Installer written to $Output"
