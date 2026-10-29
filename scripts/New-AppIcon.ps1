# Recreates the application ICO from the same simple vector geometry as assets/NetPeek.svg.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$bitmap = New-Object System.Drawing.Bitmap(256,256)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.ScaleTransform(2,2)
$mint = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#CDE5D5'))
$paper = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#F7FCF6'))
$ink = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#28684F'))
$pen = New-Object System.Drawing.Pen($ink,7)
$pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$shape = New-Object System.Drawing.Drawing2D.GraphicsPath
foreach ($arc in @(@(4,4,72,72,180,90),@(52,4,72,72,270,90),@(52,52,72,72,0,90),@(4,52,72,72,90,90))) { $gargs = [single[]]$arc; $shape.AddArc($gargs[0],$gargs[1],$gargs[2],$gargs[3],$gargs[4],$gargs[5]) }
$shape.CloseFigure()
$g.FillPath($mint,$shape)
$g.DrawBezier($pen,24,55,34,18,41,21,53,37)
$g.DrawBezier($pen,75,37,87,21,94,18,104,55)
$g.FillEllipse($paper,13,47,52,58); $g.DrawEllipse($pen,13,47,52,58)
$g.FillEllipse($paper,63,47,52,58); $g.DrawEllipse($pen,63,47,52,58)
$g.FillEllipse($ink,37,64,16,20); $g.FillEllipse($ink,76,64,16,20)
$buffer = New-Object System.IO.MemoryStream
$bitmap.Save($buffer,[System.Drawing.Imaging.ImageFormat]::Png)
$png = $buffer.ToArray()
$iconPath = Join-Path $root 'assets\NetPeek.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
  $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
  $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
  $writer.Write($png)
} finally { $writer.Dispose(); $buffer.Dispose(); $g.Dispose(); $bitmap.Dispose(); $shape.Dispose(); $pen.Dispose(); $mint.Dispose(); $paper.Dispose(); $ink.Dispose() }
Get-Item -LiteralPath $iconPath | Select-Object FullName,Length
