Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot '../src/MYSync.Desktop/Assets'
[IO.Directory]::CreateDirectory($destination) | Out-Null
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size/256.0,$size/256.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(8,8,64,64,180,90); $shape.AddArc(184,8,64,64,270,90)
    $shape.AddArc(184,184,64,64,0,90); $shape.AddArc(8,184,64,64,90,90)
    $shape.CloseFigure()
    $red = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#D9293A'))
    $graphics.FillPath($red,$shape)
    $pen = [Drawing.Pen]::new([Drawing.Color]::White,21)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $points = [Drawing.PointF[]]@([Drawing.PointF]::new(65,162),[Drawing.PointF]::new(65,82),[Drawing.PointF]::new(128,138),[Drawing.PointF]::new(191,82),[Drawing.PointF]::new(191,162))
    $graphics.DrawLines($pen,$points)
    $arrow = [Drawing.Pen]::new([Drawing.Color]::White,9)
    $graphics.DrawLine($arrow,73,194,183,194)
    $graphics.DrawLine($arrow,183,194,167,182)
    $graphics.DrawLine($arrow,73,194,89,206)
    $memory = [IO.MemoryStream]::new()
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($size,$memory.ToArray())
    $memory.Dispose(); $arrow.Dispose(); $pen.Dispose(); $red.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$stream = [IO.File]::Create((Join-Path $destination 'MYSync.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16*$frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame[0] -eq 256) {0} else {$frame[0]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame[1].Length); $writer.Write([uint32]$offset)
        $offset += $frame[1].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
} finally { $writer.Dispose() }
