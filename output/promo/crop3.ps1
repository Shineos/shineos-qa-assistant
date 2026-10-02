Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Bitmap]::new('D:\dev\shineos-local-ai\assets\store-logo-1080.png')
Write-Host ("src size: " + $src.Width + "x" + $src.Height)
$dst = New-Object System.Drawing.Bitmap 960,960
$g = [System.Drawing.Graphics]::FromImage($dst)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
# アイコンは上部中央と推定: x 400..680, y 90..370 (280x280)
$srcRect = New-Object System.Drawing.Rectangle(400, 90, 280, 280)
$dstRect = New-Object System.Drawing.Rectangle(0,0,960,960)
$g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$dst.Save('D:\dev\shineos-local-ai\output\promo\userlogo-crop.png',[System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ok
