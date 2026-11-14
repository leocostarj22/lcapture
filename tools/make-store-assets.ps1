# Generates the MSIX/Store logos in packaging\Assets from the app icon.
# Usage: powershell -File tools\make-store-assets.ps1
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\src\LCapture\Assets\app-icon.png"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\packaging\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$icon = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

# Name, canvas width, canvas height, icon size inside the canvas.
$assets = @(
    @("StoreLogo.png", 50, 50, 50),
    @("Square44x44Logo.png", 44, 44, 44),
    @("Square150x150Logo.png", 150, 150, 100),
    @("Wide310x150Logo.png", 310, 150, 100)
)

foreach ($a in $assets) {
    $name, $w, $h, $size = $a
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($icon, [int](($w - $size) / 2), [int](($h - $size) / 2), $size, $size)
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "$name ($w x $h)"
}

$icon.Dispose()
