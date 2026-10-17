param(
  [string]$Exe,
  [string]$PngA,
  [string]$PngB,
  [int]$FirstWaitSec = 20,
  [int]$SecondWaitSec = 30
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WINRECT { public int Left, Top, Right, Bottom; }
public class Win32Cap {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out WINRECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
}
"@

function CaptureWindow([System.Diagnostics.Process]$proc, [string]$out) {
  $h = $proc.MainWindowHandle
  if ($h -eq [IntPtr]::Zero) { "NO_WINDOW" | Out-File -Encoding utf8 $out; return }
  [Win32Cap]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 400
  $r = New-Object WINRECT
  [Win32Cap]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
  if ($w -lt 16 -or $ht -lt 16) { "BAD_RECT($w x $ht)" | Out-File -Encoding utf8 $out; return }
  $bmp = New-Object System.Drawing.Bitmap($w, $ht, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc()
  $ok = [Win32Cap]::PrintWindow($h, $dc, 2)
  $g.ReleaseHdc($dc)
  $g.Dispose()
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  # pixel stats
  $colors = @{}
  $nonBlack = 0
  for ($y = 0; $y -lt $ht; $y += 4) {
    for ($x = 0; $x -lt $w; $x += 4) {
      $c = $bmp.GetPixel($x, $y)
      if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlack++ }
      $key = ($c.R -shl 16) -bor ($c.G -shl 8) -bor $c.B
      $colors[$key] = 1
    }
  }
  $bmp.Dispose()
  "rect=${w}x${ht} printWindow=$ok sampled=$((($ht/4)*($w/4))) nonBlack=$nonBlack distinctColors=$($colors.Count)"
}

$before = (Get-Item "$env:LOCALAPPDATA\DefaultCompany\SW Code Zero Unity\Player.log" -ErrorAction SilentlyContinue).Length
"playerlog_bytes_before=$before"

$p = Start-Process -FilePath $Exe -PassThru
"launched pid=$($p.Id)"

Start-Sleep -Seconds $FirstWaitSec
$p.Refresh()
$alive = -not $p.HasExited
"checkA alive=$alive title=$($p.MainWindowTitle) wsKB=$([math]::Round($p.WorkingSet64/1KB)) cpuMs=$([math]::Round($p.TotalProcessorTime.TotalMilliseconds))"
$statA = CaptureWindow $p $PngA
"captureA $statA -> $PngA"

Start-Sleep -Seconds $SecondWaitSec
$p.Refresh()
$alive2 = -not $p.HasExited
"checkB alive=$alive2 wsKB=$([math]::Round($p.WorkingSet64/1KB)) cpuMs=$([math]::Round($p.TotalProcessorTime.TotalMilliseconds))"
$statB = CaptureWindow $p $PngB
"captureB $statB -> $PngB"

$ha = (Get-FileHash $PngA -Algorithm SHA256).Hash
$hb = (Get-FileHash $PngB -Algorithm SHA256).Hash
"pngA_sha=$ha"
"pngB_sha=$hb"
"frames_differ=$($ha -ne $hb)"

"playerlog_bytes_after=$(( (Get-Item "$env:LOCALAPPDATA\DefaultCompany\SW Code Zero Unity\Player.log" -ErrorAction SilentlyContinue).Length ))"

if ($alive2) {
  $p.CloseMainWindow() | Out-Null
  Start-Sleep -Seconds 4
  $p.Refresh()
  if (-not $p.HasExited) { $p.Kill() }
  "player_closed"
}
