# Regenerates every MSIX tile / icon PNG from Assets\PinBoardLogo.png.
#
# How it works:
#   1. Loads PinBoardLogo.png as the source.
#   2. Scans every pixel (via LockBits for speed) and finds the bounding box
#      of opaque pixels (alpha > threshold).
#   3. Expands that bbox to a square centred on its midpoint — that's the
#      area of "real design" we want each output to use.
#   4. For each target output size, draws the cropped source square scaled
#      to fit (square outputs) or centred inside a transparent canvas at
#      80% of the canvas height (wide / splash outputs).
#
# Run after updating Assets\PinBoardLogo.png:
#   pwsh -File .\regen-icons.ps1
#
# Then build the project (build-msix.bat) so the new PNGs go into the MSIX.

Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path $PSScriptRoot "Assets"
$sourcePath = Join-Path $assetsDir "PinBoardLogo.png"
if (-not (Test-Path $sourcePath)) {
    Write-Host "ERROR: $sourcePath not found." -ForegroundColor Red
    exit 1
}

$source = [System.Drawing.Image]::FromFile($sourcePath)

# Force the source to 32bpp ARGB so LockBits returns a predictable BGRA layout.
$rect = New-Object System.Drawing.Rectangle 0, 0, $source.Width, $source.Height
$srcBmp = New-Object System.Drawing.Bitmap($source)
$data = $srcBmp.LockBits(
    $rect,
    [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($data.Stride * $srcBmp.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$srcBmp.UnlockBits($data)

# Bounding box of opaque pixels (alpha > $threshold). Anti-aliased edges
# can have low alpha — bump $threshold if you want to ignore them.
$threshold = 16
$minX = $srcBmp.Width;  $maxX = -1
$minY = $srcBmp.Height; $maxY = -1
for ($y = 0; $y -lt $srcBmp.Height; $y++) {
    $rowOffset = $y * $data.Stride
    for ($x = 0; $x -lt $srcBmp.Width; $x++) {
        $a = $bytes[$rowOffset + $x*4 + 3]   # BGRA, alpha is byte 3
        if ($a -gt $threshold) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

if ($maxX -lt 0) {
    Write-Host "ERROR: source has no opaque pixels." -ForegroundColor Red
    exit 1
}

# Square crop centred on the bbox midpoint.
$bw = $maxX - $minX + 1
$bh = $maxY - $minY + 1
$size = [Math]::Max($bw, $bh)
$cx = ($minX + $maxX) / 2.0
$cy = ($minY + $maxY) / 2.0
$srcX = [int]($cx - $size / 2.0)
$srcY = [int]($cy - $size / 2.0)
if ($srcX -lt 0) { $srcX = 0 }
if ($srcY -lt 0) { $srcY = 0 }
if ($srcX + $size -gt $srcBmp.Width)  { $srcX = $srcBmp.Width  - $size }
if ($srcY + $size -gt $srcBmp.Height) { $srcY = $srcBmp.Height - $size }
$sourceRect = New-Object System.Drawing.Rectangle $srcX, $srcY, $size, $size

Write-Host ("Alpha bbox: ({0},{1}) -> ({2},{3})  ({4} x {5})" -f $minX, $minY, $maxX, $maxY, $bw, $bh)
Write-Host ("Source square crop: ({0},{1}) {2} x {2}" -f $srcX, $srcY, $size)
Write-Host ""

function Save-Square($outName, $dstSize) {
    $bmp = New-Object System.Drawing.Bitmap($dstSize, $dstSize)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $dstRect = New-Object System.Drawing.Rectangle 0, 0, $dstSize, $dstSize
    $g.DrawImage($srcBmp, $dstRect, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $bmp.Save((Join-Path $assetsDir $outName), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $outName  -> $dstSize x $dstSize"
}

function Save-Wide($outName, $w, $h, $scale) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $logoSize = [int]($h * $scale)
    $x = [int](($w - $logoSize) / 2)
    $y = [int](($h - $logoSize) / 2)
    $dstRect = New-Object System.Drawing.Rectangle $x, $y, $logoSize, $logoSize
    $g.DrawImage($srcBmp, $dstRect, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $bmp.Save((Join-Path $assetsDir $outName), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $outName  -> $w x $h (logo $logoSize x $logoSize, centred)"
}

Write-Host "Generating MSIX assets from trimmed PinBoardLogo.png..."
Save-Square "LockScreenLogo.scale-200.png"                          48
Save-Square "Square150x150Logo.scale-200.png"                       300
Save-Square "Square44x44Logo.scale-200.png"                         88
Save-Square "Square44x44Logo.targetsize-16_altform-unplated.png"    16
Save-Square "Square44x44Logo.targetsize-24_altform-unplated.png"    24
Save-Square "Square44x44Logo.targetsize-32_altform-unplated.png"    32
Save-Square "Square44x44Logo.targetsize-48_altform-unplated.png"    48
Save-Square "Square44x44Logo.targetsize-256_altform-unplated.png"   256
Save-Square "StoreLogo.png"                                         50
Save-Wide   "SplashScreen.scale-200.png"   1240 600 0.8
Save-Wide   "Wide310x150Logo.scale-200.png" 620 300 0.8

$srcBmp.Dispose()
$source.Dispose()
Write-Host ""
Write-Host "Done. Rebuild with build-msix.bat to ship the new assets."
