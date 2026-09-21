#requires -Version 5.1
<#
.SYNOPSIS
    Build Local Caption for use on this PC, install it somewhere stable, and put it on the
    desktop.

.DESCRIPTION
    One command between a checkout and an icon that works: publish (SPEC-WINDOWS.md §11, via
    publish.ps1), bundle the CUDA payload, mirror the result into a folder outside the
    repository, and point "Local Caption" on the desktop at it.

    This is the script for running the app you are building. For the file you hand to someone
    else, build the installer instead:

        build\package.ps1 -Version 1.2.2 -CudaDirectory <folder with cublas64_*, cudart64_*>

    which writes artifacts\LocalCaption-Setup-<version>.exe, needs no admin rights, and makes
    its own desktop shortcut when it installs.

    WHY THE BUILD IS NOT LEFT IN publish/
    A shortcut into the repository is a shortcut that breaks. publish.ps1 clears its output
    folder at the start of every build, so between two rebuilds the desktop icon points at
    nothing; and while the app is running from there, the rebuild has to refuse outright
    (locked files) - which means closing the app, mid-call if that is when you noticed.
    Copied out, the icon keeps working while the repository rebuilds behind it, and a
    recording is never the thing standing between you and a build.

    THE THREE FOLDERS, AND WHY THIS ONE NEEDS A THIRD NAME
      %LOCALAPPDATA%\LocalCaption       the data - transcripts, models, config (AppPaths.Root)
      %LOCALAPPDATA%\LocalCaptionApp    owned outright by the Velopack installer, which
                                        deletes it on install and on uninstall
      %LOCALAPPDATA%\LocalCaptionBuild  this, the default -Destination
    Sharing the first would put a program in the transcript folder; sharing the second would
    have Setup delete a local build, and a local build confuse an install. All three read the
    same data, so a build and an installed copy see the same transcripts and models - which
    is the point, and means the ~1.7 GB of models is downloaded once.

.PARAMETER Destination
    Where to install. Defaults to %LOCALAPPDATA%\LocalCaptionBuild. Mirrored, not merged:
    files no longer in the build are removed, so a stale native DLL cannot be loaded in
    preference to the one that belongs there.

.PARAMETER CudaDirectory
    Folder holding the CUDA redistributables (cublas64_*, cublasLt64_*, cudart64_*). Needed
    once: publish.ps1 carries them across later rebuilds. Without them the app runs on the
    CPU, which BENCH-RESULTS.md §1 measures at 17 s per window for large-v3-turbo.

.PARAMETER SkipPublish
    Install what is already in publish/ rather than rebuilding it first.

.PARAMETER NoShortcut
    Install, but leave the desktop alone.

.PARAMETER Run
    Start the app once it is installed.

.EXAMPLE
    build\release.ps1

.EXAMPLE
    build\release.ps1 -CudaDirectory C:\cuda-redist\bin -Run
#>
[CmdletBinding()]
param(
    [string] $Destination,
    [string] $CudaDirectory,
    [switch] $SkipPublish,
    [switch] $NoShortcut,
    [switch] $Run
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'cuda.ps1')

$windows = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $windows 'publish'
if (-not $Destination) { $Destination = Join-Path $env:LOCALAPPDATA 'LocalCaptionBuild' }
$Destination = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')

$exeName = 'LocalCaption.exe'
$installed = Join-Path $Destination $exeName

# ---------------------------------------------------------------------------------------
# Refuse before building, not after
# ---------------------------------------------------------------------------------------

# Mirroring into a folder deletes whatever else is in it. Every folder this script creates
# has LocalCaption.exe in it, so anything else that is not empty was not made by this script
# and is not ours to clear - a mistyped -Destination should cost an error message, not a
# directory.
if ((Test-Path $Destination) -and -not (Test-Path $installed)) {
    $existing = @(Get-ChildItem $Destination -Force -ErrorAction SilentlyContinue)
    if ($existing.Count) {
        throw ("$Destination already holds $($existing.Count) item(s) and no $exeName, so it " +
               "is not a Local Caption build. Installing here would delete them. Pick an " +
               "empty or new folder with -Destination.")
    }
}

# Copying over a running app leaves it half replaced: the locked files stay at the old build,
# the rest arrive from the new one, and nothing says which you are running. So the
# destination is checked always. The staging folder is checked only when there is going to be
# a publish, which deletes it - publish.ps1 refuses that itself, but by then the destination
# has been cleared and the desktop icon points at a folder being rebuilt. Under -SkipPublish
# staging is only read from, and reading a build while it runs is no one's problem; that is
# the case worth allowing, because on a machine that has never run this script the app people
# have open is the one in publish/.
$watched = @($Destination)
if (-not $SkipPublish) { $watched += [System.IO.Path]::GetFullPath($staging).TrimEnd('\') }
$running = @(Get-Process LocalCaption -ErrorAction SilentlyContinue | Where-Object {
    $proc = $_
    $proc.Path -and @($watched | Where-Object {
        $proc.Path.StartsWith($_ + '\', [System.StringComparison]::OrdinalIgnoreCase) }).Count
})
if ($running) {
    throw ("Local Caption is running from $($running[0].Path) (PID $($running.Id -join ', ')). " +
           "Close it first - if it is recording, Stop and save.")
}

# ---------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $staging $exeName))) { throw "Nothing to install: $staging has no $exeName" }
    Write-Host "Installing the existing $staging"
}
else {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $staging
}

if ($CudaDirectory) {
    Copy-CudaRuntime -From $CudaDirectory -To $staging
}
elseif (Get-CudaRuntimeDll $staging) {
    # publish.ps1 carries CUDA DLLs it finds in the output across the rebuild, so a machine
    # that has built once does not need to be told where they are again.
    Write-Host '  using the CUDA runtime DLLs already in publish/'
}
else { Write-NoCudaWarning }

# ---------------------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------------------

Write-Host "Installing to $Destination"
New-Item -ItemType Directory -Force $Destination | Out-Null

# /MIR so a file dropped from the build is dropped from the install too. robocopy answers
# with a bit field rather than a pass/fail code - 1 copied, 2 extra removed, 4 mismatched -
# and only 8 and above is a failure.
$log = & robocopy $staging $Destination /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) {
    $log | Write-Host
    throw "robocopy failed with $LASTEXITCODE while mirroring into $Destination"
}
$global:LASTEXITCODE = 0

if (-not (Test-Path $installed)) { throw "The install finished but $installed is not there." }

# ---------------------------------------------------------------------------------------
# Desktop shortcut
# ---------------------------------------------------------------------------------------

# The same file name the app's own Settings > Appearance button writes (DesktopShortcut.cs),
# so between them they cannot leave two icons for one app on the desktop.
$linkPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Local Caption.lnk'

if (-not $NoShortcut) {
    $shell = $null
    $link = $null
    try {
        $type = [Type]::GetTypeFromProgID('WScript.Shell')
        if (-not $type) { throw 'Windows Script Host is disabled on this PC.' }

        $shell = [Activator]::CreateInstance($type)
        $link = $shell.CreateShortcut($linkPath)

        # Say what is being repointed. A shortcut of this name is already there on a machine
        # that has run the app from publish/ and pressed "Create desktop shortcut", and
        # moving where someone's icon goes is not a thing to do quietly.
        $was = $link.TargetPath
        if ($was -and $was -ne $installed) { Write-Host "  was pointing at $was" }

        $link.TargetPath = $installed
        $link.WorkingDirectory = $Destination
        $link.IconLocation = "$installed,0"
        $link.Description = 'Live captions for whatever this PC is playing'
        $link.Save()
        Write-Host "  desktop shortcut: $linkPath"
    }
    catch { Write-Warning "Could not write the desktop shortcut: $($_.Exception.Message)" }
    finally {
        if ($link) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}

# ---------------------------------------------------------------------------------------

$size = (Get-ChildItem $Destination -Recurse -File | Measure-Object Length -Sum).Sum
$version = (Get-Item $installed).VersionInfo.FileVersion
$cuda = @(Get-CudaRuntimeDll $Destination).Count

Write-Host ''
Write-Host ("  {0}  version {1}, {2:N0} MB, {3} CUDA DLL(s)" -f $exeName, $version, ($size / 1MB), $cuda)
Write-Host ("  transcripts, models and settings stay in {0}" -f (Join-Path $env:LOCALAPPDATA 'LocalCaption'))
Write-Host ''
Write-Host "Done. Run it from the desktop, or from $installed"

if ($Run) { Start-Process $installed -WorkingDirectory $Destination }
