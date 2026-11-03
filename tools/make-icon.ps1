# Draws the MicMix logo: a dark microphone on the brand gradient (green -> teal), the same pair of
# colours as the app's switches and selected tiles. Every size is drawn on its own pixel grid rather
# than scaled down from 256 px, so 16-32 px stay sharp in the tray and on the taskbar.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1 [-PreviewDir <dir>]
#
# Writes MicMix/Assets/MicMix.ico (PNG-compressed frames) and docs/logo.png (256 px).
param([string]$PreviewDir)

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Logo([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([Drawing.Color]::Transparent)

    # Tile: full bleed on small sizes (every pixel counts), a little air from 32 px up.
    $m = if ($s -le 24) { 0 } else { [math]::Round($s * 0.04) }
    $t = $s - 2 * $m
    $tile = New-RoundRect $m $m $t $t ([math]::Max(3, $t * 0.23))
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF($m, $m)), (New-Object Drawing.PointF(($m + $t), ($m + $t))),
        [Drawing.Color]::FromArgb(255, 0x6A, 0xDE, 0xA4), [Drawing.Color]::FromArgb(255, 0x2B, 0xAE, 0xA2))
    $g.FillPath($grad, $tile)
    if ($s -ge 48) {
        # Soft top highlight for depth on the big sizes only.
        # Spans the whole tile (a shorter brush would tile and repeat the band lower down) and fades out by 55%.
        $hl = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.RectangleF($m, ($m - 1), $t, ($t + 2))),
            [Drawing.Color]::White, [Drawing.Color]::White, [Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $blend = New-Object Drawing.Drawing2D.ColorBlend 3
        $blend.Colors = [Drawing.Color]::FromArgb(46, 255, 255, 255), [Drawing.Color]::FromArgb(0, 255, 255, 255), [Drawing.Color]::FromArgb(0, 255, 255, 255)
        $blend.Positions = [single[]](0, 0.55, 1)
        $hl.InterpolationColors = $blend
        $g.FillPath($hl, $tile)
    }

    # Microphone, laid out on the tile in fractions of its size and snapped to whole pixels.
    $ink = [Drawing.Color]::FromArgb(255, 0x0B, 0x22, 0x1A)
    $u = $t / 16.0                                   # one "unit" = 1/16 of the tile
    $cx = $m + $t / 2.0
    $stroke = [math]::Max(1.6, [math]::Round($u * 1.15 * 2) / 2)
    $capW = [math]::Max(4, [math]::Round($u * 4.6)); if (($capW % 2) -ne ($s % 2)) { $capW += 1 }   # keep it centred on the pixel grid
    $capH = [math]::Round($u * 7.2)
    $capX = $cx - $capW / 2.0
    $capY = $m + [math]::Round($u * 2.4)
    $brush = New-Object Drawing.SolidBrush $ink
    $g.FillPath($brush, (New-RoundRect $capX $capY $capW $capH ($capW / 2.0)))

    $pen = New-Object Drawing.Pen $ink, $stroke
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    # Holder: a U around the lower half of the capsule.
    # The U's sides start level with the middle of the capsule; its bottom sits a gap below the capsule.
    $arcW = $capW + 2 * [math]::Round($u * 2.0)
    $arcTop = $capY + $capH * 0.45
    $arcBottom = $capY + $capH + [math]::Round($u * 1.8)
    $depth = $arcBottom - $arcTop
    # DrawArc 0..180 traces the lower half of the ellipse, so the ellipse is centred on $arcTop.
    $g.DrawArc($pen, [float]($cx - $arcW / 2.0), [float]($arcTop - $depth), [float]$arcW, [float](2 * $depth), 0, 180)
    # Stem and base.
    $baseY = [math]::Min($m + $t - [math]::Round($u * 2.4), $arcBottom + [math]::Round($u * 2.2))
    $g.DrawLine($pen, [float]$cx, [float]$arcBottom, [float]$cx, [float]$baseY)
    $half = [math]::Round($u * 2.6)
    $g.DrawLine($pen, [float]($cx - $half), [float]$baseY, [float]($cx + $half), [float]$baseY)

    $g.Dispose()
    return $bmp
}

function Get-Png($bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$frames = foreach ($s in $sizes) { [pscustomobject]@{ Size = $s; Png = (Get-Png (Draw-Logo $s)) } }

# ICO: header, one 16-byte directory entry per frame, then the PNG blobs.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f.Png.Length); $w.Write([uint32]$offset)
    $offset += $f.Png.Length
}
foreach ($f in $frames) { $w.Write($f.Png) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $root "MicMix\Assets\MicMix.ico"), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $root "docs\logo.png"), ($frames | Where-Object Size -eq 256).Png)
"wrote MicMix.ico ({0} frames) and docs/logo.png" -f $frames.Count

if ($PreviewDir) {
    # Contact sheet: every size at 1x on light and dark backgrounds, plus 8x zooms of 16/24/32 px.
    New-Item -ItemType Directory -Force $PreviewDir | Out-Null
    $sheet = New-Object Drawing.Bitmap 900, 520
    $g = [Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 32, 32, 32))), 0, 0, 900, 260)
    $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 243, 243, 243))), 0, 260, 900, 260)
    foreach ($row in 0, 1) {
        $x = 12
        foreach ($f in $frames) {
            $img = [Drawing.Image]::FromStream((New-Object IO.MemoryStream(, $f.Png)))
            $g.DrawImageUnscaled($img, $x, 12 + 260 * $row)
            $x += $f.Size + 12
            if ($f.Size -eq 64) { break }
        }
        $zx = 330
        foreach ($z in 16, 24, 32) {
            $img = [Drawing.Image]::FromStream((New-Object IO.MemoryStream(, ($frames | Where-Object Size -eq $z).Png)))
            $g.DrawImage($img, $zx, 12 + 260 * $row, $z * 7, $z * 7)
            $zx += $z * 7 + 14
        }
    }
    $sheet.Save((Join-Path $PreviewDir "icon-sheet.png"))
    "preview: " + (Join-Path $PreviewDir "icon-sheet.png")
}
