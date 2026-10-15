param([string]$Destination = (Join-Path $PSScriptRoot 'Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
[xml]$theme = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Theme.xaml') -Raw
$node = $theme.SelectSingleNode("//*[local-name()='DrawingImage']")
$node.RemoveAttribute('Key','http://schemas.microsoft.com/winfx/2006/xaml')
$logo = [Windows.Markup.XamlReader]::Parse($node.OuterXml)
function Render-Png([int]$Size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($logo, [Windows.Rect]::new(0,0,$Size,$Size))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}
[IO.File]::WriteAllBytes((Join-Path $Destination 'logo.png'), (Render-Png 512))
$sizes = @(16,20,24,32,40,48,64,128,256)
$images = @($sizes | ForEach-Object { ,(Render-Png $_) })
$stream = [IO.File]::Create((Join-Path $Destination 'PulseBridge.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output ('Brand assets ready: ' + $Destination)

