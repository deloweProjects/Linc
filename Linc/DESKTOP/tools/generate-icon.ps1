Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath {
    param([single]$x, [single]$y, [single]$w, [single]$h, [single]$radius)
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $gp.AddArc($x, $y, $d, $d, 180, 90)
    $gp.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $gp.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $gp.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $gp.CloseFigure()
    return $gp
}

$purple = [System.Drawing.Color]::FromArgb(255, 0x67, 0x50, 0xA4)
$sizes = 16,32,48,256
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # All drawing below happens in a 24x24 design space, scaled once to the
    # actual pixel size — matches the rest of the app's 24dp icon convention.
    $scale = $size / 24.0
    $g.ScaleTransform($scale, $scale)

    # Rounded-square background (Material "product icon" shape), radius ~22%.
    $bgPath = New-RoundedRectPath 0 0 24 24 (24 * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush($purple)), $bgPath)

    # Two overlapping chain rings — the classic "link" glyph — as a thick
    # stadium-ring outline, drawn twice at the same tilt, offset along its own
    # axis so they overlap in the middle.
    $ring = New-RoundedRectPath -6.5 -3.75 13 7.5 3.75
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 3.0)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $saved = $g.Save()
    $g.TranslateTransform(12, 12)
    $g.RotateTransform(-35)
    $g.TranslateTransform(-3.4, 0)
    $g.DrawPath($pen, $ring)
    $g.Restore($saved)

    $saved = $g.Save()
    $g.TranslateTransform(12, 12)
    $g.RotateTransform(-35)
    $g.TranslateTransform(3.4, 0)
    $g.DrawPath($pen, $ring)
    $g.Restore($saved)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [PSCustomObject]@{ Size = $size; Png = $ms.ToArray() }
    $bmp.Dispose()
}

$outPath = 'Linc/DESKTOP\Linc.Desktop\Assets/AppIcon.ico'
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$frames.Count)

$headerSize = 6 + 16 * $frames.Count
$offset = $headerSize
foreach ($f in $frames) {
    $wh = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$wh)
    $bw.Write([byte]$wh)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$f.Png.Length)
    $bw.Write([uint32]$offset)
    $offset += $f.Png.Length
}
foreach ($f in $frames) {
    $bw.Write($f.Png)
}
$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output "Wrote $outPath ($((Get-Item $outPath).Length) bytes)"
