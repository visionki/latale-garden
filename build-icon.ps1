param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = Join-Path $PSScriptRoot 'Assets\app-icon-source.png'
$iconPath = Join-Path $PSScriptRoot 'Assets\app.ico'
$windowPath = Join-Path $PSScriptRoot 'Assets\app-icon.png'
$source = [Drawing.Bitmap]::FromFile($sourcePath)
try {
    if (($source.PixelFormat -band [Drawing.Imaging.PixelFormat]::Alpha) -eq 0 -or $source.GetPixel(0, 0).A -ne 0) { throw 'The icon source must have a transparent alpha channel.' }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($size in $sizes) {
        $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $attributes = New-Object Drawing.Imaging.ImageAttributes
                try {
                    $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
                    $destination = New-Object Drawing.Rectangle(0, 0, $size, $size)
                    $graphics.DrawImage($source, $destination, 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
                } finally { $attributes.Dispose() }
            } finally { $graphics.Dispose() }
            $stream = New-Object IO.MemoryStream
            try {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $images.Add($stream.ToArray())
            } finally { $stream.Dispose() }
            if ($size -eq 256) { $bitmap.Save($windowPath, [Drawing.Imaging.ImageFormat]::Png) }
        } finally { $bitmap.Dispose() }
    }
    $file = [IO.File]::Create($iconPath)
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([UInt16]1); $writer.Write([UInt16]32)
            $writer.Write([UInt32]$images[$index].Length); $writer.Write([UInt32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($image in $images) { $writer.Write([byte[]]$image) }
    } finally { $writer.Dispose(); $file.Dispose() }
    Write-Output ('Built transparent icon sizes: ' + ($sizes -join ', '))
} finally { $source.Dispose() }
