$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskBitmap = New-Object System.Drawing.Bitmap 64,64
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
$taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskGraphics.Clear([System.Drawing.Color]::Transparent)
$taskBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#132D38'))
$taskGraphics.FillEllipse($taskBrush, 1,1,62,62)
$taskPen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#55D5BC')),2
$taskGraphics.DrawEllipse($taskPen, 11,11,42,42)
$taskNorth = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(42,16),[System.Drawing.PointF]::new(36,36),[System.Drawing.PointF]::new(22,48),[System.Drawing.PointF]::new(28,28))
$taskAccent = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#55D5BC'))
$taskGraphics.FillPolygon($taskAccent,$taskNorth)
$taskWhite = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(42,16),[System.Drawing.PointF]::new(32,32),[System.Drawing.PointF]::new(28,28))
$taskGraphics.FillPolygon([System.Drawing.Brushes]::White,$taskWhite)
$taskIcon = [System.Drawing.Icon]::FromHandle($taskBitmap.GetHicon())
$taskDir = Join-Path $PSScriptRoot '..\src\PerfCompass\Assets'
New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
$taskStream = [System.IO.File]::Create((Join-Path $taskDir 'App.ico'))
$taskIcon.Save($taskStream)
$taskStream.Dispose(); $taskIcon.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose(); $taskBrush.Dispose(); $taskPen.Dispose(); $taskAccent.Dispose()
