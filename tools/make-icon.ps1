# Regenerate the original Codex Usage Tray icon (no external assets or dependencies).
# Each ICO frame is drawn independently for crisp Windows display scaling.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot '../src/CodexTrayApp/Assets/app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $iconPath) | Out-Null
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $scale = 4
    $bitmap = [System.Drawing.Bitmap]::new($size * $scale, $size * $scale)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size * $scale / 256.0, $size * $scale / 256.0)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#152127'))
    $track = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#35474F'), 18)
    $accent = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#48D9B0'), 18)
    $glyph = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#F2FAF7'), 13)
    $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $outline.AddArc(8, 8, 48, 48, 180, 90)
        $outline.AddArc(200, 8, 48, 48, 270, 90)
        $outline.AddArc(200, 200, 48, 48, 0, 90)
        $outline.AddArc(8, 200, 48, 48, 90, 90)
        $outline.CloseFigure()
        $graphics.FillPath($background, $outline)
        $graphics.DrawEllipse($track, 35, 35, 186, 186)
        $accent.StartCap = $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawArc($accent, 35, 35, 186, 186, -90, 270)
        $glyph.StartCap = $glyph.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($glyph, 83, 100, 110, 126)
        $graphics.DrawLine($glyph, 110, 126, 83, 152)
        $graphics.DrawLine($glyph, 133, 152, 169, 152)
        $frame = [System.Drawing.Bitmap]::new($size, $size)
        $downsample = [System.Drawing.Graphics]::FromImage($frame)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $downsample.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $downsample.DrawImage($bitmap, 0, 0, $size, $size)
            $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            ,$stream.ToArray()
        } finally {
            $stream.Dispose()
            $downsample.Dispose()
            $frame.Dispose()
        }
    } finally {
        $outline.Dispose()
        $glyph.Dispose()
        $accent.Dispose()
        $track.Dispose()
        $background.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frameBytes in $frames) { $writer.Write([byte[]]$frameBytes) }
} finally { $writer.Dispose() }
Write-Output "Generated $iconPath"
