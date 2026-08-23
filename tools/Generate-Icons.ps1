<#
.SYNOPSIS
    Generates the application icon.

.DESCRIPTION
    One rhodonea rose, r = cos(3*theta/2), drawn at two levels of detail because 16px and 256px
    want different things:

      16 - 32   the rose alone. At that size the bars and the gaps between them are sub-pixel, and
                anything more than the flower turns to mush.
      48 +      the full monogram: the rose as the bowl of an R, with a detached stem and a skewed
                leg at 38 degrees.

    Both use the same rotation, so the small mark is visibly the large one with the detail dropped
    rather than a different logo.

    Frames are stored as DIB up to 64 and PNG above it. That is the usual convention and it matters
    here: System.Drawing.Icon is what the tray uses to load the file, and it is happiest with DIB
    for the sizes it actually asks for.
#>
[CmdletBinding()]
param(
    [string] $IcoPath = "$PSScriptRoot/../src/RoslynMcp.Tray/Assets/roslyn-mcp.ico",
    [string] $PreviewPath = "$PSScriptRoot/../artifacts/icon-final.png"
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Rose.ps1"

$MonogramFrom = 48
$Sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

function New-AppIcon
{
    param([int] $Size)

    if ($Size -ge $MonogramFrom)
    {
        return New-RoseMonogram -Size $Size -RoseRadius 0.320 -StemGap -0.005 `
            -LeanDegrees 38 -HaloWidth 0.050
    }

    return New-RoseIcon -Size $Size -N 3 -D 2 -Mode Alternate -RadiusFraction 0.36
}

function Get-DibFrame
{
    <# 32bpp bottom-up DIB plus the all-zero AND mask an ICO entry still requires. #>
    param([System.Drawing.Bitmap] $Bitmap)

    $w = $Bitmap.Width; $h = $Bitmap.Height
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([int]40); $writer.Write([int]$w); $writer.Write([int]($h * 2))
    $writer.Write([int16]1); $writer.Write([int16]32)
    $writer.Write([int]0); $writer.Write([int]($w * $h * 4))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)

    for ($y = $h - 1; $y -ge 0; $y--)
    {
        for ($x = 0; $x -lt $w; $x++)
        {
            $c = $Bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B); $writer.Write([byte]$c.G)
            $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
        }
    }

    $maskStride = [Math]::Floor(($w + 31) / 32) * 4
    $blank = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) { $writer.Write($blank) }

    $writer.Flush()
    # Comma operator: without it PowerShell unrolls the byte[] into Object[] on return,
    # and BinaryWriter has no overload for that.
    return ,$stream.ToArray()
}

function Get-PngFrame
{
    param([System.Drawing.Bitmap] $Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

$frames = foreach ($size in $Sizes)
{
    $bmp = New-AppIcon -Size $size
    [byte[]] $bytes = if ($size -le 64) { Get-DibFrame $bmp } else { Get-PngFrame $bmp }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$dir = Split-Path $IcoPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames)
{
    $w.Write([byte]$(if ($f.Size -ge 256) { 0 } else { $f.Size }))
    $w.Write([byte]$(if ($f.Size -ge 256) { 0 } else { $f.Size }))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$f.Bytes.Length); $w.Write([int]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()

[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath $dir).Path + '/' + (Split-Path $IcoPath -Leaf), $out.ToArray())
$w.Dispose()

"wrote $IcoPath ($([Math]::Round((Get-Item $IcoPath).Length / 1KB)) KB, $($frames.Count) frames: $($Sizes -join ', '))"
