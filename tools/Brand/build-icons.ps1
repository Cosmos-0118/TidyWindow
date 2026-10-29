# Renders the TidyWindow mark (three window panes snapped into a tidy layout) to PNGs and ICOs.
# Run from anywhere: pwsh tools/Brand/build-icons.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '../../app/Assets/Brand'
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path

function Rgb([string]$hex) { [Drawing.ColorTranslator]::FromHtml($hex) }
$tileFrom = Rgb '#1E1B4B'; $tileTo = Rgb '#7C3AED'
$mintFrom = Rgb '#5EEAD4'; $mintTo = Rgb '#7DD3FC'
$goldFrom = Rgb '#FFE08A'; $goldTo = Rgb '#FFB347'
$paneWhite = Rgb '#F5F3FF'

# All geometry is in a 256 unit square. "Bold" is the small-size variant: wider gaps and fewer details.
# Panes are tall-left, short-top-right, tall-bottom-right: a tiled window layout.
$shapes = @{
    Normal = @{ Tile = 8; Radius = 56; X = 50; Y = 50; W = 156; H = 156; Gap = 12; LeftW = 70; TopH = 64; Corner = 22; Dots = $true }
    Bold   = @{ Tile = 0; Radius = 60; X = 34; Y = 34; W = 188; H = 188; Gap = 16; LeftW = 84; TopH = 76; Corner = 26; Dots = $false }
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

function New-Gradient([float]$x, [float]$y, [float]$w, [float]$h, $from, $to) {
    New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new($x, $y)), ([Drawing.PointF]::new($x + $w, $y + $h)), $from, $to
}

function Render([int]$size, [string]$variant) {
    $s = $shapes[$variant]
    $work = $size * 4
    $bmp = New-Object Drawing.Bitmap $work, $work, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($work / 256, $work / 256)

    $tile = $s.Tile
    $tileBrush = New-Gradient $tile $tile (256 - 2 * $tile) (256 - 2 * $tile) $tileFrom $tileTo
    $tilePath = New-RoundedPath $tile $tile (256 - 2 * $tile) (256 - 2 * $tile) $s.Radius
    $g.FillPath($tileBrush, $tilePath)
    $g.SetClip($tilePath)

    $c = $s.Corner
    $rightX = $s.X + $s.LeftW + $s.Gap; $rightW = $s.W - $s.LeftW - $s.Gap
    $bottomY = $s.Y + $s.TopH + $s.Gap; $bottomH = $s.H - $s.TopH - $s.Gap

    # Left pane: the "main window", with title bar dots cut out of it at larger sizes.
    $leftBrush = New-Gradient $s.X $s.Y $s.LeftW $s.H $mintFrom $mintTo
    $g.FillPath($leftBrush, (New-RoundedPath $s.X $s.Y $s.LeftW $s.H $c))
    if ($s.Dots -and $size -ge 64) {
        foreach ($i in 0..2) { $g.FillEllipse($tileBrush, $s.X + 15 + $i * 16, $s.Y + 15, 9, 9) }
    }
    # Top right: the warm accent pane.
    $goldBrush = New-Gradient $rightX $s.Y $rightW $s.TopH $goldFrom $goldTo
    $g.FillPath($goldBrush, (New-RoundedPath $rightX $s.Y $rightW $s.TopH $c))
    # Bottom right: a quiet pane.
    $g.FillPath((New-Object Drawing.SolidBrush $paneWhite), (New-RoundedPath $rightX $bottomY $rightW $bottomH $c))
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
