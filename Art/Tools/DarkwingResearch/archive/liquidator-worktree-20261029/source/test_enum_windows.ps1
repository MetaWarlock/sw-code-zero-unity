Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WINRECT { public int Left, Top, Right, Bottom; }
public class WinTest {
  public delegate bool EnumProc(IntPtr h, IntPtr d);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr d);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out WINRECT r);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int c);
}
"@

$p = Start-Process -FilePath "Build/UserStand/DarkwingDuck.exe" -ArgumentList "-screen-fullscreen 0 -screen-width 768 -screen-height 720" -PassThru
Start-Sleep -Seconds 12
Write-Host "Process Id: $($p.Id), MainWindowHandle: $($p.MainWindowHandle)"

[WinTest]::EnumWindows({
  param($h, $d)
  $pidOut = 0
  [WinTest]::GetWindowThreadProcessId($h, [ref]$pidOut)
  if ($pidOut -eq $p.Id) {
    $r = New-Object WINRECT
    [WinTest]::GetClientRect($h, [ref]$r)
    $sb = New-Object System.Text.StringBuilder 256
    [WinTest]::GetWindowText($h, $sb, 256)
    Write-Host "Handle: $h, Title: '$($sb.ToString())', ClientRect: $($r.Right)x$($r.Bottom)"
  }
  return $true
}, [IntPtr]::Zero)

Stop-Process -Id $p.Id -Force
