<#
.SYNOPSIS
  OrderDeck app ikonu (.ico) üretir — installer\orderdeck-icon.png kaynağından.

.DESCRIPTION
  System.Drawing ile kaynağı 16..128 px boyutlarına yüksek kaliteli ölçekler,
  her birini PNG olarak kodlar ve Vista+ uyumlu (PNG-embedded) çok çözünürlüklü
  bir .ico yazar → OrderDeck.App\orderdeck.ico.

  Kaynak 128px olduğu için 256 üretilmez (upscale bulanık olur); en büyük 128.
  Yeniden üretmek için: powershell installer\make-icon.ps1
#>
param(
  [string]$Source = "$PSScriptRoot\orderdeck-icon.png",
  [string]$Output = "$PSScriptRoot\..\OrderDeck.App\orderdeck.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$srcPath = (Resolve-Path -LiteralPath $Source).Path
$src = [System.Drawing.Image]::FromFile($srcPath)

$sizes = 16, 24, 32, 48, 64, 128
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.DrawImage($src, 0, 0, $s, $s)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += , ($ms.ToArray())
  $bmp.Dispose()
  $ms.Dispose()
}
$src.Dispose()

# ICO container (ICONDIR + N×ICONDIRENTRY + PNG blobs)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$sizes.Count)      # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $data = $pngs[$i]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([Byte]$dim)              # width  (0 => 256)
  $bw.Write([Byte]$dim)              # height
  $bw.Write([Byte]0)                 # palette count
  $bw.Write([Byte]0)                 # reserved
  $bw.Write([UInt16]1)               # color planes
  $bw.Write([UInt16]32)              # bits per pixel
  $bw.Write([UInt32]$data.Length)    # bytes in resource
  $bw.Write([UInt32]$offset)         # offset
  $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush()

$outDir = Split-Path -Parent $Output
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
$bw.Dispose()

$kb = [math]::Round((Get-Item $Output).Length / 1KB, 1)
Write-Host "OK: $Output ($kb KB, $($sizes.Count) boyut: $($sizes -join ','))" -ForegroundColor Green
