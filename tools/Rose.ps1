<#
.SYNOPSIS
    Draws rhodonea rose marks with GDI+.

.DESCRIPTION
    r = cos(n/d * theta)

    GDI+ over the earlier PIL attempt for three reasons that matter here: it anti-aliases natively
    rather than needing everything drawn at 8x and shrunk, its round line joins avoid the lumps a
    thick polyline leaves at every vertex, and it offers both fill rules. That last one is the whole
    point -- Alternate (even-odd) cancels where the curve crosses itself and gives the spirograph
    look, while Winding fills the union and gives a solid bloom. The same curve, two very different
    marks.

    Petal count depends on parity: n petals when n and d are both odd, otherwise 2n.
#>

Add-Type -AssemblyName System.Drawing

$script:Tile = [System.Drawing.Color]::FromArgb(255, 0xC2, 0x18, 0x5B)
$script:Ink = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)

function Get-ReducedRose
{
    <#
        n/d must be in lowest terms before anything is derived from it. Left unreduced, 4/6 has a
        period of 12*pi while the curve it describes closes at 6*pi, so the trace walks the same
        path twice. Even-odd then sees every region covered an even number of times and cancels the
        whole mark to nothing -- which is exactly what 4/6 rendered as.
    #>
    param([int] $N, [int] $D)

    $a = [Math]::Abs($N); $b = [Math]::Abs($D)
    while ($b -ne 0) { $t = $b; $b = $a % $b; $a = $t }
    if ($a -eq 0) { $a = 1 }

    return @{ N = $N / $a; D = $D / $a }
}

function Get-RosePetalCount
{
    param([int] $N, [int] $D)

    $r = Get-ReducedRose -N $N -D $D; $N = $r.N; $D = $r.D

    if (($N % 2 -eq 1) -and ($D % 2 -eq 1)) { return $N }
    return 2 * $N
}

function Get-RosePeriod
{
    param([int] $N, [int] $D)

    $r = Get-ReducedRose -N $N -D $D; $N = $r.N; $D = $r.D

    if (($N % 2 -eq 1) -and ($D % 2 -eq 1)) { return $D * [Math]::PI }
    return 2 * $D * [Math]::PI
}

function Get-RosePoints
{
    param([int] $N, [int] $D, [double] $Cx, [double] $Cy, [double] $Radius, [int] $Steps = 2400,
          [double] $RotationDegrees = 0)

    $k = $N / $D
    $end = Get-RosePeriod -N $N -D $D
    $phi = $RotationDegrees * [Math]::PI / 180
    $points = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'

    for ($i = 0; $i -le $Steps; $i++)
    {
        $t = $end * $i / $Steps
        $r = $Radius * [Math]::Cos($k * $t)
        $points.Add([System.Drawing.PointF]::new(
            [float]($Cx + $r * [Math]::Cos($t + $phi)),
            [float]($Cy + $r * [Math]::Sin($t + $phi))))
    }

    return $points.ToArray()
}

function New-RoseTile
{
    param([int] $Size, [double] $CornerFraction = 0.22)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $r = [Math]::Max(2.0, $Size * $CornerFraction)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($Size - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($Size - 2 * $r, $Size - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $Size - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.SolidBrush($script:Tile)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()

    return @{ Bitmap = $bmp; Graphics = $g }
}

function New-RoseIcon
{
    param(
        [int] $Size,
        [int] $N,
        [int] $D,
        [ValidateSet('Alternate', 'Winding', 'Line')] [string] $Mode = 'Alternate',
        [double] $RadiusFraction = 0.36,
        [double] $StrokeFraction = 0.045,
        # Matches the monogram, so the small and large marks read as the same flower.
        [double] $Rotation = 90
    )

    $tile = New-RoseTile -Size $Size
    $g = $tile.Graphics
    # Cast on receipt: PowerShell unrolls a typed array on return, and GDI+ will not take Object[].
    [System.Drawing.PointF[]] $pts = Get-RosePoints -N $N -D $D -Cx ($Size / 2) -Cy ($Size / 2) `
        -Radius ($Size * $RadiusFraction) -RotationDegrees $Rotation

    if ($Mode -eq 'Line')
    {
        $pen = New-Object System.Drawing.Pen($script:Ink, [float]([Math]::Max(1.0, $Size * $StrokeFraction)))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLines($pen, $pts)
        $pen.Dispose()
    }
    else
    {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.FillMode = [System.Drawing.Drawing2D.FillMode]::$Mode
        $path.AddPolygon($pts)
        $brush = New-Object System.Drawing.SolidBrush($script:Ink)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    $g.Dispose()
    return $tile.Bitmap
}

function Add-SlantedBar
{
    <#
        A rectangle along an arbitrary axis, built from its four corners. GDI+ could do this with a
        rotation transform, but corners keep the geometry explicit -- and the clearance between these
        bars and the rose is the whole point of the mark, so it wants to be legible in the code.
    #>
    param(
        [System.Drawing.Drawing2D.GraphicsPath] $Path,
        [double] $X1, [double] $Y1, [double] $X2, [double] $Y2, [double] $Width
    )

    $dx = $X2 - $X1; $dy = $Y2 - $Y1
    $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if ($len -lt 1e-6) { return }

    $px = -($dy / $len) * $Width / 2
    $py = ($dx / $len) * $Width / 2

    $Path.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float]($X1 + $px), [float]($Y1 + $py)),
        [System.Drawing.PointF]::new([float]($X2 + $px), [float]($Y2 + $py)),
        [System.Drawing.PointF]::new([float]($X2 - $px), [float]($Y2 - $py)),
        [System.Drawing.PointF]::new([float]($X1 - $px), [float]($Y1 - $py))))
}

function Add-SkewedBar
{
    <#
        A parallelogram whose top and bottom edges stay horizontal while the sides lean. Different
        from a rotated rectangle, whose ends are square to its own axis and so cut at an angle --
        this is the italic-bar shape, which is what the leg of an R actually wants.
    #>
    param(
        [System.Drawing.Drawing2D.GraphicsPath] $Path,
        [double] $TopX, [double] $TopY, [double] $BottomX, [double] $BottomY, [double] $Width
    )

    $h = $Width / 2
    $Path.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float]($TopX - $h), [float]$TopY),
        [System.Drawing.PointF]::new([float]($TopX + $h), [float]$TopY),
        [System.Drawing.PointF]::new([float]($BottomX + $h), [float]$BottomY),
        [System.Drawing.PointF]::new([float]($BottomX - $h), [float]$BottomY)))
}

function New-RoseMonogram
{
    <#
        The rose as the bowl of an R, over a detached stem and leg.

        Draw order matters and is the whole trick: tile, then bars, then the rose filled, then the
        rose's own path stroked in the tile colour. That last stroke eats a channel out of the bars
        wherever the rose passes over them, so the separation is optical rather than geometric --
        which means the leg can run up underneath the bowl and the composition can be tight, instead
        of everything being held apart to avoid collisions.

        Two things that have to be computed rather than eyeballed:

        Thickness. A skewed bar of horizontal width W presents a perpendicular thickness of
        W*cos(lean). Give both bars the same W and the leg looks visibly thinner. The leg's
        horizontal width is therefore divided by cos(lean) so the two read as one weight.

        Centring. The ink is the union of the bars and the rose, and that union is not centred on
        the rose. The bounding box is measured and the whole composition shifted, or the mark sits
        off to one side -- which is exactly how it looked.
    #>
    param(
        [int] $Size,
        [int] $N = 3, [int] $D = 2,
        [double] $Rotation = 90,
        [double] $RoseRadius = 0.300,
        [double] $RoseCy = 0.400,
        # Crimson distance from the stem's edge to the rose's white edge. Below HaloWidth the
        # halo bites into the stem, which reads as the bar passing behind the flower.
        [double] $StemGap = 0.045,
        [double] $BarThickness = 0.095,      # perpendicular, honoured by both bars
        [double] $StemX = 0.150,
        [double] $Top = 0.075, [double] $Bottom = 0.925,
        [double] $LeanDegrees = 46,
        [double] $LegBottomX = 0.815,
        [double] $HaloWidth = 0.055,         # tile-coloured stroke that separates rose from bars
        [switch] $NoCentre
    )

    $lean = $LeanDegrees * [Math]::PI / 180
    $legW = $BarThickness / [Math]::Cos($lean)

    # Measured at rotation 90, in units of R. Taller than wide -- rotating swaps these, and they
    # were the unrotated pair for as long as the rotation was silently a no-op.
    $ex = 0.9079; $ey = 1.0

    # Place the rose off the stem rather than absolutely, so changing its size or the bar weight
    # cannot silently change the spacing.
    $RoseCx = $StemX + $BarThickness / 2 + $StemGap + $ex * $RoseRadius

    # Bury the leg's top at the rose's centre line. Running it to RoseCy instead let a steep lean
    # carry the hidden end all the way left into the stem, which showed as a notch in the bar.
    $legTopX = $RoseCx
    $legTopY = $Bottom - ($LegBottomX - $RoseCx) / [Math]::Tan($lean)

    $left = [Math]::Min($StemX - $BarThickness / 2, $RoseCx - $ex * $RoseRadius)
    $right = [Math]::Max($LegBottomX + $legW / 2, $RoseCx + $ex * $RoseRadius)
    $top = [Math]::Min($Top, $RoseCy - $ey * $RoseRadius)
    $bottom = [Math]::Max($Bottom, $RoseCy + $ey * $RoseRadius)

    $ox = 0.0; $oy = 0.0
    if (-not $NoCentre)
    {
        $ox = 0.5 - ($left + $right) / 2
        $oy = 0.5 - ($top + $bottom) / 2
    }

    $tile = New-RoseTile -Size $Size
    $g = $tile.Graphics
    $S = $Size

    $bars = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-SkewedBar -Path $bars -TopX (($StemX + $ox) * $S) -TopY (($Top + $oy) * $S) `
        -BottomX (($StemX + $ox) * $S) -BottomY (($Bottom + $oy) * $S) -Width ($BarThickness * $S)
    Add-SkewedBar -Path $bars -TopX (($legTopX + $ox) * $S) -TopY (($legTopY + $oy) * $S) `
        -BottomX (($LegBottomX + $ox) * $S) -BottomY (($Bottom + $oy) * $S) -Width ($legW * $S)

    $white = New-Object System.Drawing.SolidBrush($script:Ink)
    $g.FillPath($white, $bars)

    [System.Drawing.PointF[]] $pts = Get-RosePoints -N $N -D $D -Cx (($RoseCx + $ox) * $S) `
        -Cy (($RoseCy + $oy) * $S) -Radius ($RoseRadius * $S) -RotationDegrees $Rotation

    $rose = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rose.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
    $rose.AddPolygon($pts)
    # Stroke first, fill second. Stroking afterwards widens every internal petal separation as
    # well as the silhouette, and the rose falls apart into loose blobs. Stroking underneath lets
    # the fill restore the interior exactly, so only the outward half of the stroke survives -- a
    # clean halo against the bars and an untouched flower.
    $halo = New-Object System.Drawing.Pen($script:Tile, [float]($HaloWidth * 2 * $S))
    $halo.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($halo, $rose)

    $g.FillPath($white, $rose)

    $halo.Dispose(); $white.Dispose(); $rose.Dispose(); $bars.Dispose(); $g.Dispose()
    return $tile.Bitmap
}
