# Dump the icon embedded in a built exe to a PNG for visual checking.
# Usage: powershell -ExecutionPolicy Bypass -File tools\check-exe-icon.ps1 [-Exe path] [-Out path]
param(
    [string]$Exe = "dist\OpenDM.exe",
    [string]$Out = "tools\exe-icon.png"
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $root $Exe }
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }

if (-not (Test-Path $Exe)) { throw "exe not found: $Exe" }

$exePath = (Resolve-Path $Exe).Path
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exePath)
Write-Host ("extracted {0}x{1} icon from {2}" -f $icon.Width, $icon.Height, $exePath)

$src = $icon.ToBitmap()
$canvas = New-Object System.Drawing.Bitmap 192, 192
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::FromArgb(255, 24, 28, 34))
$g.DrawImage($src, 16, 16, 160, 160)
$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $canvas.Dispose(); $src.Dispose(); $icon.Dispose()

Write-Host "saved $Out"
