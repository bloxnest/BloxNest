# Renders the logo from MainWindow.xaml (LogoArt for big sizes, LogoSmall for small ones)
# into icon.ico. Run it again after changing the logo, then run build.bat.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

[xml]$doc = Get-Content -Raw (Join-Path $PSScriptRoot 'MainWindow.xaml')
$ns = New-Object System.Xml.XmlNamespaceManager $doc.NameTable
$ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')

function Load-Logo([string]$name) {
    $node = $doc.SelectSingleNode("//p:Canvas[@x:Name='$name']", $ns).Clone()
    foreach ($attr in 'Visibility', 'Grid.Row', 'HorizontalAlignment', 'VerticalAlignment', 'x:Name') {
        $node.Attributes.RemoveNamedItem($attr) | Out-Null
    }
    [System.Windows.Markup.XamlReader]::Parse($node.OuterXml)
}

# The logo sits on a dark rounded tile so it reads on light and dark taskbars alike
function Render-Png($canvas, [int]$size) {
    $inner = [Math]::Round($size * 0.84)
    $canvas.LayoutTransform = New-Object System.Windows.Media.ScaleTransform ($inner / 256), ($inner / 256)
    $canvas.HorizontalAlignment = 'Center'
    $canvas.VerticalAlignment = 'Center'
    $tile = New-Object System.Windows.Controls.Border
    $tile.Width = $size; $tile.Height = $size
    $tile.CornerRadius = New-Object System.Windows.CornerRadius ($size * 0.22)
    $tile.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#171A16')
    if ($size -ge 32) {
        $tile.BorderBrush = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#333B2D')
        $tile.BorderThickness = New-Object System.Windows.Thickness ([Math]::Max(1, $size / 64))
    }
    $tile.Child = $canvas
    $tile.Measure((New-Object System.Windows.Size $size, $size))
    $tile.Arrange((New-Object System.Windows.Rect 0, 0, $size, $size))
    $tile.UpdateLayout()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($tile)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $logo = if ($s -le 32) { Load-Logo 'LogoSmall' } else { Load-Logo 'LogoArt' }
    , (Render-Png $logo $s)
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $out.ToArray())

# Previews for checking the result
$preview = Join-Path $PSScriptRoot 'icon-preview'
New-Item -ItemType Directory -Force $preview | Out-Null
for ($i = 0; $i -lt $sizes.Count; $i++) { [IO.File]::WriteAllBytes((Join-Path $preview "icon-$($sizes[$i]).png"), $images[$i]) }
