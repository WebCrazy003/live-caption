#requires -Version 5.1
<#
.SYNOPSIS
    What "the CUDA runtime" means to this app, in one place. Dot-source it.

.DESCRIPTION
    Three build scripts need the same two facts — which DLLs make up the GPU payload, and how
    to get them beside the executable — and getting either one wrong fails the same silent
    way: Whisper.net cannot load the CUDA backend, drops to the CPU without a word, and the
    only symptom is captions that lag (BENCH-RESULTS.md §1). One definition, three callers.

.EXAMPLE
    . (Join-Path $PSScriptRoot 'cuda.ps1')
    Copy-CudaRuntime -From C:\cuda-redist\bin -To $staging
#>

# cublas64_*, cublasLt64_* and cudart64_* come from NVIDIA's redistributable archives;
# nvcudart_hybrid64.dll comes from the display driver (see Copy-CudaRuntime).
$CudaDllPattern = '^(cublas64_|cublasLt64_|cudart64_|nvcudart)'

<#
.SYNOPSIS
    The CUDA runtime DLLs in a folder, or an empty array. Never throws on a missing folder —
    callers use it to ask whether a build already carries the payload.
#>
function Get-CudaRuntimeDll {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Directory)

    if (-not (Test-Path $Directory)) { return @() }
    @(Get-ChildItem $Directory -Filter '*.dll' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $CudaDllPattern })
}

<#
.SYNOPSIS
    Copy the CUDA runtime beside the executable.

.DESCRIPTION
    Beside the executable, and nowhere else: that is where BackendProbe looks first and where
    the loader will find them without reaching into a system-wide CUDA install, which §11 is
    explicit about.

.PARAMETER From
    A folder holding the NVIDIA redistributables. Throws if it holds none — being handed the
    wrong folder should not look like success.

.PARAMETER To
    The publish or install folder.
#>
function Copy-CudaRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $From,
        [Parameter(Mandatory)] [string] $To
    )

    if (-not (Test-Path $From)) { throw "CUDA directory not found: $From" }
    $dlls = Get-CudaRuntimeDll $From
    if (-not $dlls) { throw "No CUDA runtime DLLs in $From" }

    # nvcudart_hybrid64.dll ships with the display driver, not in any CUDA redistributable
    # archive, and it lives in the DriverStore where the loader will not find it. Confirmed
    # against driver 616.92 — see BENCH-RESULTS.md §1.
    if (-not ($dlls.Name -contains 'nvcudart_hybrid64.dll')) {
        $hybrid = Get-ChildItem "$env:SystemRoot\System32\DriverStore\FileRepository" `
                    -Filter 'nvcudart_hybrid64.dll' -Recurse -ErrorAction SilentlyContinue |
                  Select-Object -First 1
        if ($hybrid) {
            Copy-Item $hybrid.FullName -Destination $To -Force
            Write-Host '  took nvcudart_hybrid64.dll from the driver store'
        }
        else { Write-Warning 'nvcudart_hybrid64.dll not found; the CUDA backend will not load.' }
    }

    $dlls | Copy-Item -Destination $To -Force
    Write-Host ("  bundled {0} CUDA runtime DLL(s)" -f $dlls.Count)
}

<#
.SYNOPSIS
    The warning to print when a build is about to go out with no GPU payload at all.
#>
function Write-NoCudaWarning {
    Write-Warning ("No CUDA runtime DLLs. This build will run on the CPU only, which " +
                   "BENCH-RESULTS.md measures at 17 s per window for large-v3-turbo — so " +
                   "§5.8 will fall back to small.en and captions will lag.")
}
