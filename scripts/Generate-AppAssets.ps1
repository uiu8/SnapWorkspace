param([string]$SourcePng)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourcePng)) {
    $SourcePng = Join-Path $repoRoot 'assets\branding\SnapWorkspace-icon-source.png'
}
$appAssets = Join-Path $repoRoot 'SnapWorkspace.App\Assets'
$msixAssets = Join-Path $repoRoot 'packaging\msix\Assets'
New-Item -ItemType Directory -Path $appAssets,$msixAssets -Force | Out-Null

Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $SourcePng).Path)

function New-CanvasPng {
    param([int]$Width, [int]$Height, [int]$IconSize, [string]$Path)
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $x = [int](($Width - $IconSize) / 2)
        $y = [int](($Height - $IconSize) / 2)
        $graphics.DrawImage($source, $x, $y, $IconSize, $IconSize)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sourceCopy = Join-Path $appAssets 'SnapWorkspace.png'
New-CanvasPng -Width 512 -Height 512 -IconSize 512 -Path $sourceCopy
New-CanvasPng -Width 44 -Height 44 -IconSize 40 -Path (Join-Path $msixAssets 'Square44x44Logo.png')
New-CanvasPng -Width 50 -Height 50 -IconSize 46 -Path (Join-Path $msixAssets 'StoreLogo.png')
New-CanvasPng -Width 150 -Height 150 -IconSize 132 -Path (Join-Path $msixAssets 'Square150x150Logo.png')
New-CanvasPng -Width 310 -Height 150 -IconSize 132 -Path (Join-Path $msixAssets 'Wide310x150Logo.png')

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngStreams = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    $pngStreams.Add($stream.ToArray())
    $stream.Dispose()
}

$iconPath = Join-Path $appAssets 'SnapWorkspace.ico'
$file = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$pngStreams[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $pngStreams[$index].Length
    }
    foreach ($bytes in $pngStreams) { $writer.Write($bytes) }
}
finally {
    $writer.Dispose()
    $source.Dispose()
}

Write-Output "Generated application and MSIX assets from $SourcePng"
