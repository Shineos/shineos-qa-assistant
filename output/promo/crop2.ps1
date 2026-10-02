Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Bitmap]::new('D:\dev\shineos-local-ai\assets\promo-hero.png')
$dst = New-Object System.Drawing.Bitmap 640,640
$g = [System.Drawing.Graphics]::FromImage($dst)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$srcRect = New-Object System.Drawing.Rectangle(80,140,160,160)
$dstRect = New-Object System.Drawing.Rectangle(0,0,640,640)
$g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$dst.Save('D:\dev\shineos-local-ai\output\promo\logo-crop2.png',[System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ok
