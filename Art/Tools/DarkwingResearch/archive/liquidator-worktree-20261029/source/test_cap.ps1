Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WINRECT { public int Left, Top, Right, Bottom; }
public struct WINPOINT { public int X, Y; }
public class Win32CapTest {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out WINRECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out WINRECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref WINPOINT p);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@

$exe = "Build/UserStand/DarkwingDuck.exe"
$p = Start-Process -FilePath $exe -ArgumentList "-screen-fullscreen 0 -screen-width 768 -screen-height 720" -PassThru
Start-Sleep -Seconds 15

$h = $p.MainWindowHandle
$r = New-Object WINRECT
[Win32CapTest]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Host "Window handle: $h, Rect: $w x $ht"

$bmp = New-Object System.Drawing.Bitmap($w, $ht, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
$ok2 = [Win32CapTest]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc)
$g.Dispose()

$bmp.Save("Temp/test_printwindow_2.png")
Write-Host "PrintWindow flag 2 result: $ok2, saved to Temp/test_printwindow_2.png"

# Also try CopyFromScreen
$bmpScreen = New-Object System.Drawing.Bitmap($w, $ht)
$g2 = [System.Drawing.Graphics]::FromImage($bmpScreen)
$g2.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$g2.Dispose()
$bmpScreen.Save("Temp/test_copyfromscreen.png")
Write-Host "CopyFromScreen saved to Temp/test_copyfromscreen.png"

Stop-Process -Id $p.Id -Force
