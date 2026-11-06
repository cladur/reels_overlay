# Regenerates app.ico: a blue portrait "phone" with a play triangle on a transparent
# background. Each size is drawn separately (not scaled); small sizes are stored as 32-bit
# bitmaps and 256 px as PNG inside the .ico.
#   powershell -ExecutionPolicy Bypass -File make_icon.ps1
Add-Type -AssemblyName System.Drawing

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function DrawIcon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Portrait "phone" filling the icon's height on a transparent background, snapped to
    # whole pixels so small sizes stay sharp.
    $pw = [math]::Round($s * 0.66); $ph = [math]::Round($s * 0.96)
    $px = [math]::Round(($s - $pw) / 2); $py = [math]::Round(($s - $ph) / 2)
    $phone = RoundedRect $px $py $pw $ph ([math]::Max(1.5, $s * 0.09))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, $py), (New-Object System.Drawing.PointF 0, ($py + $ph)), ([System.Drawing.Color]::FromArgb(255, 110, 155, 255)), ([System.Drawing.Color]::FromArgb(255, 60, 98, 220))
    $g.FillPath($grad, $phone)

    # Play triangle, optically centred (nudged right).
    $th = $ph * 0.36; $tw = $th * 0.87
    $cx = $px + $pw / 2 + $tw * 0.12; $cy = $py + $ph / 2
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($cx - $tw / 2), ($cy - $th / 2)),
        (New-Object System.Drawing.PointF ($cx + $tw / 2), $cy),
        (New-Object System.Drawing.PointF ($cx - $tw / 2), ($cy + $th / 2)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 246, 250))), $tri)

    $g.Dispose()
    $bmp
}

# Classic ICO image: BITMAPINFOHEADER (height doubled for the mask), 32-bit BGRA rows bottom-up,
# then a 1-bit AND mask (all zero: alpha decides). Every Windows icon reader handles this.
function DibBytes($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $maskStride = [math]::Ceiling($s / 32) * 4
    $w.Write([uint32]($s * $s * 4 + $maskStride * $s))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $s)))
    $w.Flush()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = DrawIcon $s
    if ($s -ge 256) {
        # Large sizes are stored as PNG, as Windows itself does.
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = DibBytes $bmp
    }
    $bmp.Dispose()
    , $bytes
}

# ICO: 6-byte header, 16-byte directory entry per image, then the image payloads.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)          # palette, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)     # planes, bits per pixel
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
"wrote app.ico ($($sizes -join ', ') px)"
