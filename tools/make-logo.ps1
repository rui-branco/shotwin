# Renders docs/logo.png from the app's own icon, so the README's mark and the icon
# Windows shows can never drift apart. No external tools, no image editor.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ico  = Join-Path $root "src\Shotwin\Assets\app.ico"
$docs = Join-Path $root "docs"

if (-not (Test-Path $ico)) { throw "Icon not found at $ico" }
if (-not (Test-Path $docs)) { New-Item -ItemType Directory -Path $docs | Out-Null }

$out = Join-Path $docs "logo.png"
$size = 208   # Twice the 104 the README asks for, so it stays sharp on a dense screen.

# The largest frame in the .ico, read out of the file directly.
#
# Not through System.Drawing.Icon: a frame of 128px or more is usually stored as a whole
# PNG rather than as a bitmap, and Icon.ToBitmap cannot read those — it walks off the end
# of the array and throws. The directory is eight bytes of header and sixteen per entry,
# which is less work than working around that.
$bytes = [System.IO.File]::ReadAllBytes($ico)
$count = [BitConverter]::ToUInt16($bytes, 4)

$bestOffset = 0
$bestLength = 0
$bestSide = 0

for ($i = 0; $i -lt $count; $i++) {
    $entry = 6 + ($i * 16)

    # Zero means 256 in an icon directory, which is the one size that matters most here.
    $side = $bytes[$entry]
    if ($side -eq 0) { $side = 256 }

    if ($side -gt $bestSide) {
        $bestSide = $side
        $bestLength = [BitConverter]::ToInt32($bytes, $entry + 8)
        $bestOffset = [BitConverter]::ToInt32($bytes, $entry + 12)
    }
}

if ($bestLength -le 0) { throw "No usable frame in $ico" }

Write-Host "Largest frame: ${bestSide}x${bestSide}"

$frame = New-Object byte[] $bestLength
[Array]::Copy($bytes, $bestOffset, $frame, 0, $bestLength)

$isPng = $frame.Length -gt 8 -and $frame[0] -eq 0x89 -and $frame[1] -eq 0x50

if ($isPng) {
    $stream = New-Object System.IO.MemoryStream(,$frame)
    $source = [System.Drawing.Image]::FromStream($stream)
} else {
    $icon = New-Object System.Drawing.Icon($ico, $bestSide, $bestSide)
    $source = $icon.ToBitmap()
}

$bitmap = New-Object System.Drawing.Bitmap($size, $size)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

$graphics.DrawImage($source, 0, 0, $size, $size)

$bitmap.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()
$source.Dispose()

Write-Host "Wrote $out ($size x $size)"
