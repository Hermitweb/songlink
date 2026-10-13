Add-Type -AssemblyName System.Drawing, System.Windows.Forms

# 全屏截图(包含 WebView2 的实际 GPU 输出)
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$g.Dispose()

$out = Join-Path $PSScriptRoot "desktop-fullscreen.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host ("saved {0} ({1}x{2})" -f $out, $bounds.Width, $bounds.Height)
