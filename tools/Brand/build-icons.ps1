# Renders the TidyWindow mark (a glowing window with a sparkle bursting from its corner) to PNGs and ICOs.
# Run from anywhere: pwsh tools/Brand/build-icons.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '../../app/Assets/Brand'
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path

function Rgb([string]$hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
$tileFrom = Rgb '#1E1B4B'; $tileTo = Rgb '#7C3AED'
$windowFrom = Rgb '#5EEAD4'; $windowTo = Rgb '#7DD3FC'
$sparkFrom = Rgb '#FFF4B0'; $sparkTo = Rgb '#FFB347'

# All geometry is in a 256 unit square. "Bold" is the small-size variant: heavier strokes, bigger sparkle.
$shapes = @{
    Normal = @{ Tile = 8; Radius = 56; L = 48; T = 80; R = 198; B = 208; Corner = 30; Stroke = 17; Bar = 42; Star = @(186, 84, 50); Halo = 10 }
    Bold   = @{ Tile = 0; Radius = 60; L = 34; T = 78; R = 200; B = 224; Corner = 34; Stroke = 28; Bar = 58; Star = @(182, 82, 58); Halo = 11 }
}

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $x2 = $x + $w; $y2 = $y + $h
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x2 - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x2 - 2 * $r, $y2 - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y2 - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-SparklePath([float]$cx, [float]$cy, [float]$r) {
    # Four-point star with concave sides: each side is a cubic curve pulled in toward the centre.
    $c = $r * 0.13
    $n = [Drawing.PointF]::new($cx, $cy - $r); $e = [Drawing.PointF]::new($cx + $r, $cy)
    $s = [Drawing.PointF]::new($cx, $cy + $r); $w = [Drawing.PointF]::new($cx - $r, $cy)
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddBezier($n, [Drawing.PointF]::new($cx + $c, $cy - $c), [Drawing.PointF]::new($cx + $c, $cy - $c), $e)
    $p.AddBezier($e, [Drawing.PointF]::new($cx + $c, $cy + $c), [Drawing.PointF]::new($cx + $c, $cy + $c), $s)
    $p.AddBezier($s, [Drawing.PointF]::new($cx - $c, $cy + $c), [Drawing.PointF]::new($cx - $c, $cy + $c), $w)
    $p.AddBezier($w, [Drawing.PointF]::new($cx - $c, $cy - $c), [Drawing.PointF]::new($cx - $c, $cy - $c), $n)
    $p.CloseFigure()
    return $p
}

function Render([int]$size, [string]$variant) {
    $s = $shapes[$variant]
    $work = $size * 4
    $bmp = New-Object Drawing.Bitmap $work, $work, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($work / 256, $work / 256)

    $tile = $s.Tile
    $tileBrush = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new($tile, $tile)), ([Drawing.PointF]::new(256 - $tile, 256 - $tile)), $tileFrom, $tileTo
    $tilePath = New-RoundedPath $tile $tile (256 - 2 * $tile) (256 - 2 * $tile) $s.Radius
    $g.FillPath($tileBrush, $tilePath)
    $g.SetClip($tilePath)

    # Window: a rounded outline with a title bar divider.
    $w = $s.R - $s.L; $h = $s.B - $s.T
    $windowBrush = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new($s.L, $s.T)), ([Drawing.PointF]::new($s.R, $s.B)), $windowFrom, $windowTo
    $half = $s.Stroke / 2
    $ringPen = New-Object Drawing.Pen $windowBrush, $s.Stroke
    $g.DrawPath($ringPen, (New-RoundedPath ($s.L + $half) ($s.T + $half) ($w - $s.Stroke) ($h - $s.Stroke) ($s.Corner - $half)))
    $divider = $s.Stroke * 0.8
    $g.FillRectangle($windowBrush, $s.L + $half, $s.T + $s.Bar - $divider / 2, $w - $s.Stroke, $divider)

    # Sparkle: a halo in the tile gradient cuts it free of the window, then the warm star on top.
    $cx, $cy, $r = $s.Star
    $star = New-SparklePath $cx $cy $r
    $haloPen = New-Object Drawing.Pen $tileBrush, ($s.Halo * 2)
    $haloPen.LineJoin = 'Round'
    $g.DrawPath($haloPen, $star)
    $g.FillPath($tileBrush, $star)
    $sparkBrush = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new($cx - $r, $cy - $r)), ([Drawing.PointF]::new($cx + $r, $cy + $r)), $sparkFrom, $sparkTo
    $g.FillPath($sparkBrush, $star)
    $g.Dispose()

    $final = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $f = [Drawing.Graphics]::FromImage($final)
    $f.InterpolationMode = 'HighQualityBicubic'; $f.PixelOffsetMode = 'HighQuality'; $f.CompositingQuality = 'HighQuality'
    $f.DrawImage($bmp, 0, 0, $size, $size)
    $f.Dispose(); $bmp.Dispose()
    return $final
}

function Get-Png([Drawing.Bitmap]$bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $frames = foreach ($size in $sizes) {
        $variant = if ($size -le 32) { 'Bold' } else { 'Normal' }
        $bmp = Render $size $variant
        [pscustomobject]@{ Size = $size; Data = (Get-Png $bmp) }
        $bmp.Dispose()
    }
    $stream = [IO.File]::Create($path)
    $bw = New-Object IO.BinaryWriter $stream
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$f.Data.Length); $bw.Write([uint32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $bw.Write([byte[]]$f.Data) }
    $bw.Dispose(); $stream.Dispose()
}

function Write-PngFile([string]$name, [int]$size, [string]$variant) {
    $bmp = Render $size $variant
    $bmp.Save((Join-Path $out $name), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Write-Ico (Join-Path $out 'tidywindow.ico') @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Write-Ico (Join-Path $out 'tray.ico') @(16, 20, 24, 32, 48)
Write-PngFile 'logo-64.png' 64 'Normal'
Write-PngFile 'logo-512.png' 512 'Normal'
Get-ChildItem $out | Select-Object Name, Length
