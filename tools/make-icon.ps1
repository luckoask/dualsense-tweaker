# Generates src/DualSenseTweaker/Assets/app.ico (PNG frames 16–256 px). Run with Windows PowerShell:
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot "..\src\DualSenseTweaker\Assets\app.ico"))

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Color([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

# Everything is drawn on a 256×256 canvas and scaled, so every size comes from the same shapes.
function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Tile
    $tile = New-RoundedRect 8 8 240 240 56
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 8), (New-Object System.Drawing.PointF 0, 248), (Color "#1F2638"), (Color "#0A0D14"))
    $g.FillPath($tileBrush, $tile)

    # Lightbar glow behind the controller
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(46, 58, 164, 70)
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
    $glowBrush.CenterColor = Color "#4F82F7" 150
    $glowBrush.SurroundColors = @((Color "#4F82F7" 0))
    $g.FillPath($glowBrush, $glow)

    # Controller body: rounded top plus two grips
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath([System.Drawing.Drawing2D.FillMode]::Winding)
    $body.AddPath((New-RoundedRect 34 84 188 88 44), $false)
    $body.AddEllipse(28, 116, 80, 100)
    $body.AddEllipse(148, 116, 80, 100)
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color "#E8EAED")), $body)

    # Touchpad with the lightbar strip along its top edge
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color "#1B1E26")), (New-RoundedRect 88 90 80 46 10))
    $strip = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 88, 0), (New-Object System.Drawing.PointF 168, 0), (Color "#4F82F7"), (Color "#F2884A"))
    # LCKS brand gradient: blue → purple → orange
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @((Color "#4F82F7"), (Color "#8E6CF0"), (Color "#F2884A"))
    $blend.Positions = @([single]0, [single]0.5, [single]1)
    $strip.InterpolationColors = $blend
    $g.FillPath($strip, (New-RoundedRect 92 94 72 9 4.5))

    # Sticks
    $dark = New-Object System.Drawing.SolidBrush (Color "#1B1E26")
    $g.FillEllipse($dark, 74, 132, 30, 30)
    $g.FillEllipse($dark, 152, 132, 30, 30)

    $g.Dispose()
    return $bmp
}

# PNG frame (only for 256 px, the usual ICO layout).
function Encode-Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# Classic 32-bit DIB frame: BITMAPINFOHEADER, bottom-up BGRA rows, empty AND mask.
# System.Drawing.Icon (used for the tray icon) cannot read PNG frames.
function Encode-Dib($bmp) {
    $size = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($size * $size * 4)
    for ($row = 0; $row -lt $size; $row++) {
        # DIB rows go bottom-up.
        $source = [IntPtr]($data.Scan0.ToInt64() + $row * $data.Stride)
        [System.Runtime.InteropServices.Marshal]::Copy($source, $pixels, ($size - 1 - $row) * $size * 4, $size * 4)
    }
    $bmp.UnlockBits($data)

    $maskRow = [int]([Math]::Floor(($size + 31) / 32)) * 4
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($pixels.Length + $maskRow * $size))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write($pixels)
    $bw.Write((New-Object byte[] ($maskRow * $size)))
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @($sizes | ForEach-Object {
    $bmp = Render $_
    $bytes = if ($_ -ge 256) { Encode-Png $bmp } else { Encode-Dib $bmp }
    $bmp.Dispose()
    , $bytes
})

$icoStream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($icoStream)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $Out)).Path + "\" + (Split-Path $Out -Leaf), $icoStream.ToArray())
"Ikona: $Out ($($icoStream.Length) B, $($sizes.Count) veľkostí)"
