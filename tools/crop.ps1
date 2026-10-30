param(
    [string]$Src,
    [int]$X, [int]$Y, [int]$W, [int]$H,
    [double]$Scale = 3,
    [string]$Out
)

Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Image]::FromFile($Src)
$dw = [int]($W * $Scale)
$dh = [int]($H * $Scale)
$crop = New-Object System.Drawing.Bitmap $dw, $dh
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$dst = New-Object System.Drawing.Rectangle 0, 0, $dw, $dh
$srcRect = New-Object System.Drawing.Rectangle $X, $Y, $W, $H
$g.DrawImage($img, $dst, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$crop.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $crop.Dispose(); $img.Dispose()
Write-Host "cropped -> $Out"
