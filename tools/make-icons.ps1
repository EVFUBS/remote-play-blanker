# Generates Assets\app.ico (idle) and Assets\active.ico (streaming) for RemotePlayBlanker.
# Design: rounded badge, white monitor outline, crescent moon on the screen.
# Run: pwsh tools\make-icons.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\src\RemotePlayBlanker\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-Frame([int]$size, [hashtable]$c) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)   # draw in a 256x256 space

    # Badge
    $badge = New-RoundedPath 8 8 240 240 52
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, 256), $c.Top, $c.Bottom)
    $g.FillPath($grad, $badge)

    # Thicker strokes at small sizes so the glyph survives downscaling
    $stroke = if ($size -le 24) { 22 } elseif ($size -le 48) { 18 } else { 14 }
    $white = [System.Drawing.Color]::FromArgb(255, 245, 248, 252)

    # Screen fill + moon
    $screen = New-RoundedPath 44 56 168 116 14
    $g.FillPath([System.Drawing.SolidBrush]::new($c.Screen), $screen)
    $g.FillEllipse([System.Drawing.SolidBrush]::new($c.Moon), 92, 76, 76, 76)        # moon disc
    $g.FillEllipse([System.Drawing.SolidBrush]::new($c.Screen), 116, 64, 70, 70)     # bite -> crescent

    # Monitor outline + stand
    $pen = [System.Drawing.Pen]::new($white, $stroke)
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $screen)
    $wb = [System.Drawing.SolidBrush]::new($white)
    $g.FillRectangle($wb, 116, 172, 24, 26)
    $g.FillPath($wb, (New-RoundedPath 78 192 100 18 9))

    $g.Dispose()
    $bmp
}

# Classic DIB entry (32bpp BGRA + AND mask): readable by every Windows/.NET icon loader.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int]($n * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {          # bottom-up rows
        for ($x = 0; $x -lt $n; $x++) {
            $px = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$px.B); $bw.Write([byte]$px.G); $bw.Write([byte]$px.R); $bw.Write([byte]$px.A)
        }
    }
    $maskRow = [int]([math]::Ceiling($n / 32.0) * 4)
    $mask = [byte[]]::new($maskRow * $n)         # AND mask unused with alpha
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()
    , $ms.ToArray()
}

function Save-Ico([string]$path, [hashtable]$colors) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $images = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($s in $sizes) {
        $bmp = New-Frame $s $colors
        if ($s -eq 256) {
            $ms = [System.IO.MemoryStream]::new()
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($ms.ToArray())
        } else { $images.Add([byte[]](Get-DibBytes $bmp)) }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = [System.IO.BinaryWriter]::new($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $images[$i].Length
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$len); $bw.Write([int]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img, 0, $img.Length) }
    $bw.Dispose()
    Write-Host "Wrote $path"
}

$C = [System.Drawing.Color]
Save-Ico (Join-Path $outDir 'app.ico') @{
    Top = $C::FromArgb(255, 58, 70, 90); Bottom = $C::FromArgb(255, 30, 38, 52)
    Screen = $C::FromArgb(255, 22, 28, 40); Moon = $C::FromArgb(255, 150, 168, 196)
}
Save-Ico (Join-Path $outDir 'active.ico') @{
    Top = $C::FromArgb(255, 36, 132, 214); Bottom = $C::FromArgb(255, 16, 72, 140)
    Screen = $C::FromArgb(255, 10, 30, 60); Moon = $C::FromArgb(255, 255, 211, 74)
}

# Preview PNG for checking the design (written next to this script, not shipped)
$outDir = $PSScriptRoot
$prev = [System.Drawing.Bitmap]::new(560, 280)
$pg = [System.Drawing.Graphics]::FromImage($prev); $pg.Clear($C::FromArgb(255, 200, 200, 200))
$pg.DrawImage((New-Frame 256 @{ Top = $C::FromArgb(255, 58, 70, 90); Bottom = $C::FromArgb(255, 30, 38, 52); Screen = $C::FromArgb(255, 22, 28, 40); Moon = $C::FromArgb(255, 150, 168, 196) }), 12, 12)
$pg.DrawImage((New-Frame 256 @{ Top = $C::FromArgb(255, 36, 132, 214); Bottom = $C::FromArgb(255, 16, 72, 140); Screen = $C::FromArgb(255, 10, 30, 60); Moon = $C::FromArgb(255, 255, 211, 74) }), 292, 12)
$pg.Dispose(); $prev.Save((Join-Path $outDir 'preview.png')); $prev.Dispose()
