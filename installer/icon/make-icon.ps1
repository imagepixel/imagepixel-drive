# Draws the icon of Imagepixel Drive: the logo of Imagepixel with a small drive in
# the corner, so that the application can be told apart from Imagepixel Backup in the
# notification area and in the Dock. Writes the files the application, the setup
# program and the disk image for macOS are built from. Run it again after a change.
#
#   logo.png        the logo as it is, for Windows and for the pages of the application
#   logo-macos.png  the logo on a tile, the way icons of macOS look

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$accent = [System.Drawing.Color]::FromArgb(0x8E, 0x24, 0xAA)

function New-Canvas([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    return $bitmap, $graphics
}

function New-RoundedRectangle([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $width - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $width - $d, $y + $height - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# The logo a little smaller in the upper left, and in the lower right a drive: a
# flat box with a light at its right end, on a disc in the colour of the application.
function New-Icon([string]$logoFile, [int]$size, [double]$logoShare) {
    $logo = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot $logoFile))
    $bitmap, $g = New-Canvas $size
    $g.DrawImage($logo, 0, 0, [int]($size * $logoShare), [int]($size * $logoShare))
    $logo.Dispose()

    $diameter = [single]($size * 0.38)
    $left = [single]($size - $diameter - $size * 0.004)
    $ring = [single]($diameter * 0.05)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $colour = New-Object System.Drawing.SolidBrush $accent
    $g.FillEllipse($white, $left, $left, $diameter, $diameter)
    $g.FillEllipse($colour, $left + $ring, $left + $ring, $diameter - 2 * $ring, $diameter - 2 * $ring)

    $boxWidth = [single]($diameter * 0.56)
    $boxHeight = [single]($diameter * 0.30)
    $boxLeft = [single]($left + ($diameter - $boxWidth) / 2)
    $boxTop = [single]($left + ($diameter - $boxHeight) / 2)
    $g.FillPath($white, (New-RoundedRectangle $boxLeft $boxTop $boxWidth $boxHeight ([single]($diameter * 0.06))))
    $light = [single]($diameter * 0.09)
    $g.FillEllipse($colour, $boxLeft + $boxWidth - $light * 1.9, $boxTop + ($boxHeight - $light) / 2, $light, $light)
    $g.Dispose()
    return $bitmap
}

function Get-Scaled($picture, [int]$target) {
    $bitmap, $graphics = New-Canvas $target
    $graphics.DrawImage($picture, 0, 0, $target, $target)
    $graphics.Dispose()
    return $bitmap
}

function Get-PngBytes($bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

# macOS: one large picture, which build-macos.sh turns into the sizes the system wants
$mac = New-Icon 'logo-macos.png' 1024 0.90
[System.IO.File]::WriteAllBytes((Join-Path $root 'installer\macos\app-icon-1024.png'), (Get-PngBytes $mac))

# Windows and the pages of the application
$assets = Join-Path $root 'src\ImagepixelDrive.App\Assets'
$windows = New-Icon 'logo.png' 256 0.80
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app-icon.png'), (Get-PngBytes $windows))

# an icon file holds one picture per size, each stored as PNG
$sizes = 256, 128, 64, 48, 32, 24, 16
$pictures = $sizes | ForEach-Object { , (Get-PngBytes (Get-Scaled $windows $_)) }
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$pictures[$i].Length); $writer.Write([uint32]$offset)
    $offset += $pictures[$i].Length
}
foreach ($picture in $pictures) { $writer.Write($picture) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $file.ToArray())

Write-Host "Icon written: app.ico, app-icon.png and installer\macos\app-icon-1024.png"
