# Generates the installer artwork (installer/Assets/dialog.bmp 493×312, banner.bmp 493×58) in the
# LCKS look. Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File tools\make-installer-art.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot "..\installer\Assets"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Color([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function BrandBrush([float]$x, [float]$width) {
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF $x, 0), (New-Object System.Drawing.PointF ($x + $width), 0), (Color "#4F82F7"), (Color "#F2884A"))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @((Color "#4F82F7"), (Color "#8E6CF0"), (Color "#F2884A"))
    $blend.Positions = @([single]0, [single]0.5, [single]1)
    $brush.InterpolationColors = $blend
    return $brush
}

# Letter-spaced text drawn glyph by glyph; returns the width.
function Draw-Tracked($g, [string]$text, $font, $brush, [float]$x, [float]$y, [float]$spacing) {
    $format = [System.Drawing.StringFormat]::GenericTypographic
    $cursor = $x
    foreach ($ch in $text.ToCharArray()) {
        $s = [string]$ch
        $g.DrawString($s, $font, $brush, $cursor, $y, $format)
        $cursor += $g.MeasureString($s, $font, 1000, $format).Width + $spacing
    }
    return $cursor - $x - $spacing
}

function Measure-Tracked($g, [string]$text, $font, [float]$spacing) {
    $format = [System.Drawing.StringFormat]::GenericTypographic
    $width = 0
    foreach ($ch in $text.ToCharArray()) { $width += $g.MeasureString([string]$ch, $font, 1000, $format).Width + $spacing }
    return $width - $spacing
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    return @($bmp, $g)
}

$icon = New-Object System.Drawing.Icon((Join-Path $PSScriptRoot "..\src\DualSenseTweaker\Assets\app.ico"), 128, 128)
$logo = $icon.ToBitmap()

# ── Welcome / finish dialog: dark LCKS panel on the left (the dialog text sits on the white right side).
$canvas = New-Canvas 493 312; $bmp = $canvas[0]; $g = $canvas[1]
$g.Clear([System.Drawing.Color]::White)
$panel = New-Object System.Drawing.Rectangle 0, 0, 164, 312
$g.FillRectangle((New-Object System.Drawing.SolidBrush (Color "#0A0D14")), $panel)
$g.SetClip($panel)
$gridPen = New-Object System.Drawing.Pen (Color "#FFFFFF" 14), 1
for ($x = 0; $x -lt 164; $x += 22) { $g.DrawLine($gridPen, $x, 0, $x, 312) }
for ($y = 0; $y -lt 312; $y += 22) { $g.DrawLine($gridPen, 0, $y, 164, $y) }
foreach ($glow in @(@(-60, -70, "#4F82F7", 70), @(60, 200, "#F2884A", 55))) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse([float]$glow[0], [float]$glow[1], 220, 220)
    $brush = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $brush.CenterColor = Color $glow[2] $glow[3]
    $brush.SurroundColors = @((Color $glow[2] 0))
    $g.FillPath($brush, $path)
}
$g.DrawImage($logo, 46, 58, 72, 72)
$title = New-Object System.Drawing.Font("Bahnschrift SemiBold", 15, [System.Drawing.GraphicsUnit]::Pixel)
$small = New-Object System.Drawing.Font("Bahnschrift", 9, [System.Drawing.GraphicsUnit]::Pixel)
# Centered, letter-spaced captions under the logo.
foreach ($line in @(
        @("DUALSENSE", $title, 4, 152, $null),
        @("TWEAKER", $small, 3.5, 176, (New-Object System.Drawing.SolidBrush (Color "#7C8597"))),
        @("OD LCKS", $small, 3, 282, (New-Object System.Drawing.SolidBrush (Color "#E9EDF5"))))) {
    $w = Measure-Tracked $g $line[0] $line[1] $line[2]
    $x = (164 - $w) / 2
    $brush = if ($line[4]) { $line[4] } else { BrandBrush $x $w }
    $null = Draw-Tracked $g $line[0] $line[1] $brush $x $line[3] $line[2]
}
$g.ResetClip()
$g.FillRectangle((BrandBrush 0 312), 162, 0, 2, 312)
$g.Dispose()
$bmp.Save((Join-Path $OutDir "dialog.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()

# ── Top banner: white (the dialog titles are black text on the left), logo on the right, brand line below.
$canvas = New-Canvas 493 58; $bmp = $canvas[0]; $g = $canvas[1]
$g.Clear([System.Drawing.Color]::White)
$g.DrawImage($logo, 437, 9, 40, 40)
$g.FillRectangle((BrandBrush 0 493), 0, 55, 493, 3)
$g.Dispose()
$bmp.Save((Join-Path $OutDir "banner.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()

"Grafika inštalátora: $OutDir"
