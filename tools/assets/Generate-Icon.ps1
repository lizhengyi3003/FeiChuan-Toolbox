param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\..\src\TunHotspot.WinUI\Assets'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
# Original vector artwork. Render at 4x the largest ICO layer for clean small edges.
$canvas=[Drawing.Bitmap]::new(1024,1024)
$g=[Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.ScaleTransform(4,4)
$tile=[Drawing.Drawing2D.GraphicsPath]::new()
# Construct clockwise rounded corners, starting at the top left.
$tile.AddArc(8,8,96,96,180,90)
$tile.AddArc(152,8,96,96,270,90)
$tile.AddArc(152,152,96,96,0,90)
$tile.AddArc(8,152,96,96,90,90)
$tile.CloseFigure()
$brush=[Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(24,16),[Drawing.Point]::new(216,248),[Drawing.ColorTranslator]::FromHtml('#2678F3'),[Drawing.ColorTranslator]::FromHtml('#1647B8'))
$g.FillPath($brush,$tile)
$pen=[Drawing.Pen]::new([Drawing.Color]::White,15)
$pen.StartCap=$pen.EndCap=[Drawing.Drawing2D.LineCap]::Round
foreach($radius in @(100,68,36)){$g.DrawArc($pen,128-$radius,180-$radius,2*$radius,2*$radius,225,90)}
$dot=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#6CF0DE'))
$g.FillEllipse($dot,115,167,26,26)
$canvas.Save((Join-Path $OutputDirectory 'TunHotspot-master.png'),[Drawing.Imaging.ImageFormat]::Png)
$sizes=@(16,20,24,32,40,48,64,128,256)
$layers=[Collections.Generic.List[byte[]]]::new()
foreach($size in $sizes){
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $scaled=[Drawing.Graphics]::FromImage($bitmap)
    $scaled.CompositingQuality=[Drawing.Drawing2D.CompositingQuality]::HighQuality
    $scaled.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scaled.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scaled.DrawImage($canvas,0,0,$size,$size)
    $stream=[IO.MemoryStream]::new()
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $layers.Add($stream.ToArray())
    if($size -eq 256){$bitmap.Save((Join-Path $OutputDirectory 'TunHotspot.png'),[Drawing.Imaging.ImageFormat]::Png)}
    $stream.Dispose(); $scaled.Dispose(); $bitmap.Dispose()
}
$file=[IO.File]::Create((Join-Path $OutputDirectory 'TunHotspot.ico'))
$writer=[IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++){
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$layers[$i].Length); $writer.Write([uint32]$offset)
        $offset+=$layers[$i].Length
    }
    foreach($layer in $layers){$writer.Write($layer)}
} finally {$writer.Dispose(); $file.Dispose(); $dot.Dispose(); $pen.Dispose(); $brush.Dispose(); $tile.Dispose(); $g.Dispose(); $canvas.Dispose()}
Write-Output "图标已生成：$OutputDirectory\TunHotspot.ico"
