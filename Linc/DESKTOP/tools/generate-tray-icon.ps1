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
$size = 32
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$scale = $size / 24.0
$g.ScaleTransform($scale, $scale)

$bgPath = New-RoundedRectPath 0 0 24 24 (24 * 0.5)  # full circle for the tray (matches the original circular linc.png)
$g.FillPath((New-Object System.Drawing.SolidBrush($purple)), $bgPath)

$ring = New-RoundedRectPath -6.5 -3.75 13 7.5 3.75
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 3.0)
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

$saved = $g.Save()
$g.TranslateTransform(12, 12); $g.RotateTransform(-35); $g.TranslateTransform(-3.4, 0)
$g.DrawPath($pen, $ring)
$g.Restore($saved)

$saved = $g.Save()
$g.TranslateTransform(12, 12); $g.RotateTransform(-35); $g.TranslateTransform(3.4, 0)
$g.DrawPath($pen, $ring)
$g.Restore($saved)

$g.Dispose()
$bmp.Save('Linc/DESKTOP\Linc.Desktop\Assets/linc.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Wrote linc.png"
