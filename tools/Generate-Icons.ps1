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
    # Which app's mark to draw. The composition is the same either way, and the bowl is what differs:
    # the tray gets the rose, the inspector a lens whose handle is the leg of the R.
    [ValidateSet('Rose', 'Lens')] [string] $Mark = 'Rose',
    # Both apps read their assets out of the shared UI library's output, so both are written there.
    [string] $AssetDirectory = "$PSScriptRoot/../src/RoseMcp.Ui/Assets",
    [string] $BaseName
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Rose.ps1"

$MonogramFrom = 48
$Sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

# The single-frame mark a window draws inside itself. 128 rather than the largest frame: it is what
# the existing mark is, it is drawn at about 16 logical pixels, and a 256 costs four times the bytes
# for pixels nothing asks for.
$MarkSize = 128

if (-not $BaseName) { $BaseName = if ($Mark -eq 'Lens') { 'rose-inspector' } else { 'rose-mcp' } }

$IcoPath = Join-Path $AssetDirectory "$BaseName.ico"
$PngPath = Join-Path $AssetDirectory "$BaseName.png"

function New-AppIcon
{
    param([int] $Size)

    if ($Size -ge $MonogramFrom)
    {
        # The same numbers for both, so the two marks are the same composition with a different bowl.
        # The lens sits where the rose sits: its extents put its left edge in the same place, so the
        # channel the halo cuts out of the stem is the same channel.
        $bowl = @{ RoseRadius = 0.320; StemGap = -0.005; LeanDegrees = 38; HaloWidth = 0.050 }

        if ($Mark -eq 'Lens') { return New-RoseMonogram -Size $Size -Bowl Lens @bowl }

        return New-RoseMonogram -Size $Size @bowl
    }

    if ($Mark -eq 'Lens') { return New-LensIcon -Size $Size }

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

# The PNG beside the icon, from the same draw. A window's own mark is an Image, and an image decoder
# handed a multi-frame .ico picks its own frame -- so the mark is a single 256 rather than whatever
# the decoder settled on. Written here rather than by hand, or the two drift the first time either
# is regenerated and nothing says which is current.
$markBitmap = New-AppIcon -Size $MarkSize
$markBitmap.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$markBitmap.Dispose()

"wrote $IcoPath ($([Math]::Round((Get-Item $IcoPath).Length / 1KB)) KB, $($frames.Count) frames: $($Sizes -join ', '))"
"wrote $PngPath ($([Math]::Round((Get-Item $PngPath).Length / 1KB)) KB, $MarkSize)"
