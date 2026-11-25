# Regenerates Assets\FluentClip.ico (multi-size, PNG-compressed entries) and Assets\AppIcon.png.
# Same design as the in-memory tray icon: blue gradient squircle with a white clipboard.
#   pwsh -File tools\gen-icon.ps1
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [single]($s * 0.04)
    $bg = New-RoundedPath $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.23)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 14, 165, 233)), ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
    $g.FillPath($grad, $bg)

    # clipboard board
    $board = New-RoundedPath ($s * 0.27) ($s * 0.25) ($s * 0.46) ($s * 0.56) ($s * 0.08)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 255, 255, 255))), $board)

    # clasp
    $clasp = New-RoundedPath ($s * 0.37) ($s * 0.17) ($s * 0.26) ($s * 0.15) ($s * 0.05)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))), $clasp)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([single][Math]::Max(1, $s * 0.025))), $clasp)

    # text lines (skipped at 16px where they turn into mush)
    if ($s -ge 20) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([single][Math]::Max(1.2, $s * 0.045))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        foreach ($f in 0.45, 0.57, 0.69) {
            $right = if ($f -eq 0.69) { 0.53 } else { 0.63 }
            $g.DrawLine($pen, [single]($s * 0.37), [single]($s * $f), [single]($s * $right), [single]($s * $f))
        }
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO container: ICONDIR + ICONDIRENTRY[] + PNG payloads
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'FluentClip.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.png'), (New-IconPng 64))
"wrote $(Join-Path $assets 'FluentClip.ico') ($($out.Length) bytes) and AppIcon.png"
