<#
.SYNOPSIS
    Draws the Local Caption icon and writes src/LocalCaption.App/Assets/app.ico.

.DESCRIPTION
    The mark is the app's own idea in three strokes: two committed caption lines, and a
    third that is still being written — shorter, and ending in the live cursor. It sits on
    a brass tile so it reads on a dark taskbar and a light one alike.

    Drawn rather than exported so it can be regenerated without a design tool, and at every
    size natively: a 16 px icon scaled down from 256 turns three bars into grey mush, so
    each size snaps its geometry to whole pixels instead.

    Run from anywhere:  powershell -File build\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Output
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than as the parameter's default: Windows PowerShell 5.1 has not set
# $PSScriptRoot yet when param() defaults are evaluated under -File.
if (-not $Output) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Output = Join-Path $here '..\src\LocalCaption.App\Assets\app.ico'
}
$Output = [System.IO.Path]::GetFullPath($Output)

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.01, $r * 2)
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # The tile: brass, lit from the top.
    $inset = [Math]::Max(0, [Math]::Round($size * 0.03))
    $tileSize = $size - 2 * $inset
    $tile = New-RoundedRect $inset $inset $tileSize $tileSize ($tileSize * 0.235)
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, $tileSize, $tileSize
    $top = [System.Drawing.Color]::FromArgb(255, 0xE2, 0xBC, 0x7C)
    $foot = [System.Drawing.Color]::FromArgb(255, 0xA8, 0x7C, 0x3C)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $foot, 90.0
    $g.FillPath($fill, $tile)

    # Three caption lines, snapped to whole pixels so small sizes stay crisp.
    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x12, 0x14, 0x19))
    $bar = [Math]::Max(2, [Math]::Round($size * 0.105))
    $gap = [Math]::Max(1, [Math]::Round($size * 0.085))
    $left = [Math]::Round($size * 0.22)
    $full = $size - 2 * $left
    $stack = 3 * $bar + 2 * $gap
    $y = [Math]::Round(($size - $stack) / 2)

    $widths = @($full, [Math]::Round($full * 0.82), [Math]::Round($full * 0.46))
    for ($i = 0; $i -lt 3; $i++) {
        $line = New-RoundedRect $left $y $widths[$i] $bar ($bar / 2)
        $g.FillPath($ink, $line)
        $line.Dispose()

        if ($i -eq 2) {
            # The live cursor: where the next word will land.
            $dotX = $left + $widths[$i] + [Math]::Max(1, [Math]::Round($size * 0.06))
            $g.FillEllipse($ink, [single]$dotX, [single]$y, [single]$bar, [single]$bar)
        }
        $y += $bar + $gap
    }

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose(); $fill.Dispose(); $ink.Dispose(); $tile.Dispose()
    return ,$stream.ToArray()
}

# An .ico is a directory of images; Vista and later accept PNG payloads for every entry.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = $sizes | ForEach-Object { ,(New-IconPng $_) }

$directory = Split-Path -Parent $Output
New-Item -ItemType Directory -Force $directory | Out-Null

$file = [System.IO.File]::Create($Output)
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
}
finally {
    $writer.Dispose(); $file.Dispose()
}

# The 256 px frame on its own, for the window's title bar and anywhere else a PNG is easier.
[System.IO.File]::WriteAllBytes((Join-Path $directory 'app.png'), $images[$images.Count - 1])

Write-Host ("Wrote {0} ({1:N0} bytes, {2} sizes)" -f $Output, (Get-Item $Output).Length, $sizes.Count)
