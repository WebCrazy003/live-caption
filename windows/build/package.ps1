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
    [string] $Output,
    # Pack what is already in publish/ instead of rebuilding it first.
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'cuda.ps1')

$windows = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $windows 'publish'
if (-not $Output) { $Output = Join-Path $windows 'artifacts' }

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $staging 'LocalCaption.exe'))) { throw "Nothing to pack: $staging has no LocalCaption.exe" }
    Write-Host "Packing the existing $staging"
}
else {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $staging
}

if ($CudaDirectory) {
    # Beside the executable, which is where BackendProbe looks first and where the loader
    # will find them without reaching into a system-wide CUDA install. §11 is explicit that
    # nothing should load from outside the install directory.
    Copy-CudaRuntime -From $CudaDirectory -To $staging
}
elseif (Get-CudaRuntimeDll $staging) {
    # publish.ps1 carries CUDA DLLs it finds in the output across the rebuild, so a machine
    # that has packaged once does not need to be told where they are again.
    Write-Host '  using the CUDA runtime DLLs already in publish/'
}
else { Write-NoCudaWarning }

# vpk refuses to overwrite a release of the same version, which turns an ordinary rebuild
# into an error. Clearing the output is what "build the installer" means here — the releases
# that matter live wherever they are deployed, not in the build directory.
if (Test-Path $Output) { Get-ChildItem $Output -File | Remove-Item -Force }

# THE PACK ID IS NOT "LocalCaption", AND MUST NOT BECOME IT.
#
# Velopack installs into %LOCALAPPDATA%\<packId>, and owns that folder outright: uninstall
# deletes it, and so does Setup when it finds one already there. %LOCALAPPDATA%\LocalCaption is
# where the app keeps config.json, the models and — by default — every transcript (§9). With
# the two names the same, uninstalling the app deleted the interviews. "LocalCaptionApp" puts
# the program next door, so the installer can do what it likes to its own folder and the
# data outlives it, which is what §11 ("an uninstall can optionally leave them") intended.
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    $vpk = Get-Command (Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe') -ErrorAction SilentlyContinue
}
if (-not $vpk) { throw "Velopack CLI not found. Install it with: dotnet tool install -g vpk" }

New-Item -ItemType Directory -Force $Output | Out-Null

& $vpk.Source pack `
    --packId LocalCaptionApp `
    --packTitle 'Local Caption' `
    --packAuthors 'Richard &amp; Stevitech' `
    --packVersion $Version `
    --packDir $staging `
    --mainExe LocalCaption.exe `
    --icon (Join-Path $PSScriptRoot '..\src\LocalCaption.App\Assets\app.ico') `
    --outputDir $Output
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with $LASTEXITCODE" }

# vpk names its output after the pack ID, and the pack ID is an implementation detail (see
# above). The file someone is actually sent should say what it is and which version.
$setup = Join-Path $Output 'LocalCaptionApp-win-Setup.exe'
$friendly = Join-Path $Output "LocalCaption-Setup-$Version.exe"
if (Test-Path $setup) {
    if (Test-Path $friendly) { Remove-Item $friendly -Force }
    Move-Item $setup $friendly
}

Write-Host ''
Get-ChildItem $Output | ForEach-Object { Write-Host ("  {0,-46} {1,8:N0} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ''
Write-Host "Installer written to $Output"
