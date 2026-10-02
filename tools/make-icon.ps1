# Generates the Nexus app icon (PNG + multi-size ICO) from vector shapes.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\Nexus.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Folder back with tab
    $back = New-RoundedPath 20 44 216 168 22
    $tab = New-RoundedPath 20 32 96 48 18
    $backBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 84, 214))
    $g.FillPath($backBrush, $back)
    $g.FillPath($backBrush, $tab)

    # Folder front with vertical gradient
    $front = New-RoundedPath 20 78 216 146 22
    $frontRect = New-Object System.Drawing.RectangleF 20, 78, 216, 146
    $frontBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $frontRect, ([System.Drawing.Color]::FromArgb(255, 92, 154, 255)), ([System.Drawing.Color]::FromArgb(255, 47, 104, 240)), 90
    $g.FillPath($frontBrush, $front)

    # Subtle top highlight on the front panel
    $highlight = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), 3
    $g.DrawLine($highlight, 42, 80, 214, 80)

    # Nexus emblem: a hub connected to three nodes
    $cx = 128; $cy = 152; $radius = 40
    $nodes = foreach ($angle in -90, 30, 150) {
        $rad = $angle * [Math]::PI / 180
        [pscustomobject]@{ X = $cx + $radius * [Math]::Cos($rad); Y = $cy + $radius * [Math]::Sin($rad) }
    }
    $linePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 255, 255)), 9
    $linePen.StartCap = 'Round'; $linePen.EndCap = 'Round'
    foreach ($node in $nodes) { $g.DrawLine($linePen, $cx, $cy, [float]$node.X, [float]$node.Y) }
    $white = [System.Drawing.Brushes]::White
    $g.FillEllipse($white, $cx - 17, $cy - 17, 34, 34)
    foreach ($node in $nodes) { $g.FillEllipse($white, [float]($node.X - 12), [float]($node.Y - 12), 24, 24) }

    $g.Dispose()
    return $bitmap
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

$png = New-IconBitmap 256
$png.Save((Join-Path $OutputDirectory 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

# ICO with PNG-compressed frames
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

$icoPath = Join-Path $OutputDirectory 'AppIcon.ico'
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($icoPath))
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frame.Bytes.Length); $writer.Write([UInt32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
$writer.Close()

Write-Output "Icon written to $OutputDirectory"
