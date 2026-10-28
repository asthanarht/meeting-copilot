param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\src\MeetingCopilot.Desktop\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$AssetDirectory = (Resolve-Path $AssetDirectory).Path
[xml]$svg = Get-Content (Join-Path $AssetDirectory 'MeetingCopilot.svg') -Raw
if ($svg.DocumentElement.GetAttribute('viewBox') -ne '0 0 32 32') {
    throw 'The icon exporter expects a 32 by 32 source viewBox.'
}
$drawings = @()
foreach ($element in $svg.DocumentElement.ChildNodes) {
    if ($element.LocalName -eq 'title') { continue }
    if ($element.LocalName -ne 'path') { throw "Unsupported icon element: $($element.LocalName)" }
    $brush = $null
    if ($element.GetAttribute('fill') -ne 'none') {
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($element.GetAttribute('fill'))
    }
    $pen = $null
    if ($element.HasAttribute('stroke')) {
        $stroke = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($element.GetAttribute('stroke'))
        $width = [double]::Parse($element.GetAttribute('stroke-width'), [Globalization.CultureInfo]::InvariantCulture)
        $pen = [Windows.Media.Pen]::new($stroke, $width)
        $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
        $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    }
    $drawings += [Windows.Media.GeometryDrawing]::new($brush, $pen, [Windows.Media.Geometry]::Parse($element.GetAttribute('d')))
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 32.0, $size / 32.0))
    foreach ($drawing in $drawings) { $context.DrawDrawing($drawing) }
    $context.Pop()
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $frames += ,$stream.ToArray()
    } finally { $stream.Dispose() }
}
$output = [IO.File]::Create((Join-Path $AssetDirectory 'MeetingCopilot.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $AssetDirectory 'MeetingCopilot.png'), $frames[-1])
Write-Output 'Exported MeetingCopilot.ico (16-256px) and MeetingCopilot.png from the SVG source.'
