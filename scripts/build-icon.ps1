# Windows/WPF only. Render the editable SVG paths at each native icon size.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$assets=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/VamSys.App/Assets'
[xml]$source=Get-Content (Join-Path $assets 'FlightOpsDesk.svg') -Raw
function Render([int]$size) {
    $visual=[Windows.Media.DrawingVisual]::new()
    $dc=$visual.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    foreach($path in $source.svg.path) {
        $brush=[Windows.Media.BrushConverter]::new().ConvertFromInvariantString($path.fill)
        $dc.DrawGeometry($brush,$null,[Windows.Media.Geometry]::Parse($path.d))
    }
    $dc.Pop();$dc.Close()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.MemoryStream]::new()
    try {$encoder.Save($stream); return ,$stream.ToArray()} finally {$stream.Dispose()}
}
[IO.File]::WriteAllBytes((Join-Path $assets 'FlightOpsDesk.png'),(Render 256))
$sizes=@(16,20,24,32,40,48,64,128,256)
$images=@($sizes | ForEach-Object { ,(Render $_) })
$stream=[IO.File]::Create((Join-Path $assets 'FlightOpsDesk.ico'))
$writer=[IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++){
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
        $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$images[$i].Length
    }
    foreach($bytes in $images){$writer.Write([byte[]]$bytes)}
} finally {$writer.Dispose()}
Write-Output 'Generated FlightOpsDesk.png and nine-size FlightOpsDesk.ico from SVG.'