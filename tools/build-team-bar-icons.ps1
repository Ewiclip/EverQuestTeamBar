#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../probes/FloatingHotbar/Assets'))

# Package the approved transparent PNG artwork into Windows multi-resolution ICOs.
# Keep the original artwork intact; only remove transparent padding and fit sizes.
function Export-WindowsIcon([string]$Name, [int[]]$Sizes, [string]$SmallIconPath) {
    $source = [Drawing.Bitmap]::new((Join-Path $assets ($Name + '.png')))
    try {
        $left = $source.Width; $top = $source.Height; $right = -1; $bottom = -1
        for ($y = 0; $y -lt $source.Height; $y++) {
            for ($x = 0; $x -lt $source.Width; $x++) {
                if ($source.GetPixel($x, $y).A -gt 8) {
                    $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y)
                    $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y)
                }
            }
        }
        if ($right -lt $left) { throw "$Name has no visible pixels." }
        $bounds = [Drawing.Rectangle]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
        $frames = [Collections.Generic.List[byte[]]]::new()
        foreach ($size in $Sizes) {
            $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $stream = [IO.MemoryStream]::new()
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $margin = [Math]::Max(1, [Math]::Round($size * 0.035))
                $scale = ($size - 2 * $margin) / [Math]::Max($bounds.Width, $bounds.Height)
                $width = [int][Math]::Round($bounds.Width * $scale)
                $height = [int][Math]::Round($bounds.Height * $scale)
                $target = [Drawing.Rectangle]::new([int][Math]::Floor(($size-$width)/2), [int][Math]::Floor(($size-$height)/2), $width, $height)
                $graphics.DrawImage($source, $target, $bounds, [Drawing.GraphicsUnit]::Pixel)
                if ($SmallIconPath -and $size -le 32) {
                    # At title-bar/taskbar sizes the shield remains recognizable;
                    # the full Assist/Attack tiles are retained in larger frames.
                    $smallIcon = [Drawing.Icon]::new($SmallIconPath, $size, $size)
                    $smallBitmap = $smallIcon.ToBitmap()
                    try { $smallBitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png) }
                    finally { $smallBitmap.Dispose(); $smallIcon.Dispose() }
                } else { $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png) }
                $frames.Add($stream.ToArray())
            } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
        }
        $file = [IO.File]::Create((Join-Path $assets ($Name + '.ico')))
        $writer = [IO.BinaryWriter]::new($file)
        try {
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$Sizes.Count)
            $offset = 6 + 16 * $Sizes.Count
            for ($i = 0; $i -lt $Sizes.Count; $i++) {
                $dimension = if ($Sizes[$i] -eq 256) { 0 } else { $Sizes[$i] }
                $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
                $writer.Write([byte]0); $writer.Write([byte]0)
                $writer.Write([uint16]1); $writer.Write([uint16]32)
                $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
                $offset += $frames[$i].Length
            }
            foreach ($frame in $frames) { $writer.Write($frame) }
        } finally { $writer.Dispose() }
        # Decode through the same Windows icon API used by the tray.
        $icon = [Drawing.Icon]::new((Join-Path $assets ($Name + '.ico')), 32, 32)
        try { Write-Output "$Name.ico: $($Sizes -join ', ') pixels; Windows decode $($icon.Width)x$($icon.Height)." }
        finally { $icon.Dispose() }
    } finally { $source.Dispose() }
}

Export-WindowsIcon 'TeamBarTray' @(16, 20, 24, 32, 40, 48, 64)
Export-WindowsIcon 'TeamBar' @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256) (Join-Path $assets 'TeamBarTray.ico')
