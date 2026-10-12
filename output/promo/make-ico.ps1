## assets/store-logo-1080.png (jp comment removed)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$srcPath = 'D:\dev\shineos-local-ai\assets\store-logo-1080.png'
$outPath = 'D:\dev\shineos-local-ai\assets\app.ico'

## (jp comment removed)
$targets = @(16, 32, 48, 64, 128, 256)
$frames = @{}
$png256 = $null
$cur = [System.Drawing.Bitmap]::new($srcPath)
try {
  foreach ($t in $targets) {
    while ($cur.Width -gt ($t * 2)) {
      $half = [System.Drawing.Bitmap]::new([int]($cur.Width / 2), [int]($cur.Width / 2))
      $g = [System.Drawing.Graphics]::FromImage($half)
      $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
      $g.DrawImage($cur, 0, 0, $half.Width, $half.Height)
      $g.Dispose()
      $cur.Dispose()
      $cur = $half
    }
    $b = [System.Drawing.Bitmap]::new($t, $t, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($cur, 0, 0, $t, $t)
    $g.Dispose()

    if ($t -eq 256) {
      $ms = New-Object System.IO.MemoryStream
      $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
      $png256 = $ms.ToArray()
      $ms.Dispose()
    }
    else {
      $rect = New-Object System.Drawing.Rectangle(0, 0, $t, $t)
      $bd = $b.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
      $bytes = New-Object byte[] ($bd.Stride * $t)
      [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $bytes, 0, $bytes.Length)
      $b.UnlockBits($bd)
      if ($bd.Stride -ne ($t * 4)) {
        throw "unexpected stride at $t"
      }
      $frames[$t] = $bytes
    }
    $b.Dispose()
  }
}
finally {
  $cur.Dispose()
}

## ICO(jp comment removed)
Write-Host ("frames captured: " + $frames.Count + " keys=[" + ($frames.Keys -join ',') + "] png256=" + ($null -ne $png256))
$order = @(16, 32, 48, 64, 128, 256)
$blobs = @{}
foreach ($t in $order) {
  if ($t -eq 256) {
    $blobs[$t] = $png256
  }
  else {
    $w = $t
    $xor = $frames[$t]
    $andRow = [int](($w + 31) / 32) * 4
    $dib = New-Object System.IO.MemoryStream
    $db = New-Object System.IO.BinaryWriter($dib)
    $db.Write([UInt32]40)
    $db.Write([Int32]$w)
    $db.Write([Int32]($w * 2))
    $db.Write([UInt16]1)
    $db.Write([UInt16]32)
    $db.Write([UInt32]0)
    $db.Write([UInt32]($w * $w * 4))
    $db.Write([Int32]0)
    $db.Write([Int32]0)
    $db.Write([UInt32]0)
    $db.Write([UInt32]0)
    $db.Write([Int32]0)
    $db.Write([Int32]0)
    for ($y = $w - 1; $y -ge 0; $y--) {
      $seg = New-Object byte[] ($w * 4)
      [Array]::Copy($xor, ($y * $w * 4), $seg, 0, ($w * 4))
      $db.Write($seg)
    }
    $zeros = New-Object byte[] ($andRow * $w)
    $db.Write($zeros)
    $db.Flush()
    $blobs[$t] = $dib.ToArray()
    $dib.Dispose()
  }
}

$fs = New-Object System.IO.FileStream($outPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
  $bw.Write([UInt16]0)
  $bw.Write([UInt16]1)
  $bw.Write([UInt16]$order.Count)
  $offset = 6 + 16 * $order.Count
  foreach ($t in $order) {
    $dim = $t
    if ($t -eq 256) {
      $dim = 0
    }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$t].Length)
    $bw.Write([UInt32]$offset)
    $offset += $blobs[$t].Length
  }
  foreach ($t in $order) {
    $bw.Write($blobs[$t])
  }
  $bw.Flush()
}
finally {
  $bw.Dispose()
  $fs.Dispose()
}

Write-Host ("written: " + (Get-Item $outPath).Length + " bytes")

foreach ($s in @(16, 32, 48, 64, 128, 256)) {
  $i = [System.Drawing.Icon]::new($outPath, $s, $s)
  Write-Host ($s.ToString() + "px -> " + $i.Width + "x" + $i.Height + " OK")
  $i.Dispose()
}
