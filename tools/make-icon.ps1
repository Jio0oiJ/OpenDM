# Build OpenDM app icon (multi-size .ico) + extension PNGs.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less files as ANSI).

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $PSScriptRoot 'IconGen.cs'

Add-Type -TypeDefinition (Get-Content -Raw -Encoding UTF8 $src) -ReferencedAssemblies System.Drawing

$ico = Join-Path $root 'src\OpenDM.App\Assets\opendm.ico'
[OpenDMIcon]::Build($ico)
$kb = [math]::Round((Get-Item $ico).Length / 1KB, 1)
Write-Host "ICO     -> $ico ($kb KB)"

$extIcons = Join-Path $root 'ext\browser\icons'
foreach ($s in 16, 48, 128) {
    $p = Join-Path $extIcons "$s.png"
    [OpenDMIcon]::ExportPng($s, $p)
    Write-Host "PNG $s -> $p"
}

$preview = Join-Path $root 'tools\icon-preview.png'
[OpenDMIcon]::Preview($preview)
Write-Host "PREVIEW -> $preview"
