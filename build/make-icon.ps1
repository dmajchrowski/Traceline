<#
.SYNOPSIS
  Draws Traceline's icon (a green tile with a white trace line, the same picture as the tray icon) and writes
  src\Traceline.App\Assets\traceline.ico with the sizes Windows asks for. Run it again only to change the picture.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$target = Join-Path $PSScriptRoot "..\src\Traceline.App\Assets\traceline.ico"
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null

function New-Picture([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 32.0, $size / 32.0)   # the picture is drawn on a 32 x 32 grid
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0.5, 0.5, 12, 12, 180, 90); $tile.AddArc(19.5, 0.5, 12, 12, 270, 90)
    $tile.AddArc(19.5, 19.5, 12, 12, 0, 90); $tile.AddArc(0.5, 19.5, 12, 12, 90, 90)
    $tile.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(11, 110, 79))
    $g.FillPath($fill, $tile)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $(if ($size -lt 24) { 3.2 } else { 2.6 })
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 4, 20), (New-Object System.Drawing.PointF 10, 20), (New-Object System.Drawing.PointF 13, 11),
        (New-Object System.Drawing.PointF 18, 24), (New-Object System.Drawing.PointF 21, 16), (New-Object System.Drawing.PointF 28, 16)))
    $pen.Dispose(); $fill.Dispose(); $tile.Dispose(); $g.Dispose()
    $bitmap
}

# One entry per size. 256 is stored as PNG; the smaller ones as plain 32-bit bitmaps, which every tool reads.
$entries = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $bitmap = New-Picture $size
    $data = New-Object System.IO.MemoryStream
    if ($size -eq 256) {
        $bitmap.Save($data, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $w = New-Object System.IO.BinaryWriter $data
        $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {      # colour rows, bottom row first
            for ($x = 0; $x -lt $size; $x++) { $w.Write([int]$bitmap.GetPixel($x, $y).ToArgb()) }
        }
        $w.Write((New-Object byte[] ([int]([math]::Ceiling($size / 32.0)) * 4 * $size)))   # transparency mask: all taken from the alpha channel
        $w.Flush()
    }
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $data.ToArray() }
}

$file = New-Object System.IO.MemoryStream
$out = New-Object System.IO.BinaryWriter $file
$out.Write([int16]0); $out.Write([int16]1); $out.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $out.Write([byte]($e.Size % 256)); $out.Write([byte]($e.Size % 256)); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([int16]1); $out.Write([int16]32); $out.Write([int]$e.Bytes.Length); $out.Write([int]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $out.Write($e.Bytes) }
$out.Flush()
[System.IO.File]::WriteAllBytes($target, $file.ToArray())
"Wrote $((Resolve-Path $target).Path): $($entries.Count) sizes, $($file.Length) bytes"
