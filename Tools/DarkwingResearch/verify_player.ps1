param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [Parameter(Mandatory=$true)][string]$PngA,
  [Parameter(Mandatory=$true)][string]$PngB,
  [int]$FirstWaitSec = 20,
  [int]$SecondWaitSec = 30,
  [string]$PlayerLog,
  [string]$Report,
  [string]$Replay,
  [int]$ReplayTimeoutSec = 0,
  [switch]$CompletedMission,
  [switch]$ShowGameWindow = $true
)
$ErrorActionPreference = 'Stop'
function MeasureClient($bmp, [int]$left, [int]$top, [int]$width, [int]$height) {
  if ($left -lt 0 -or $top -lt 0 -or $width -lt 16 -or $height -lt 16 -or
      $left+$width -gt $bmp.Width -or $top+$height -gt $bmp.Height) { throw 'Invalid client capture bounds' }
  $colors = @{}; $nonBlack = 0; $samples = 0
  for ($y=$top; $y -lt $top+$height; $y+=4) {
    for ($x=$left; $x -lt $left+$width; $x+=4) {
      $c=$bmp.GetPixel($x,$y); $samples++
      if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlack++ }
      $colors[([int]$c.R -shl 16) -bor ([int]$c.G -shl 8) -bor [int]$c.B]=1
    }
  }
  if ($nonBlack -eq 0 -or $colors.Count -lt 2) { throw 'Empty/blank client capture' }
  return [ordered]@{samples=$samples; nonBlack=$nonBlack; colors=$colors.Count}
}
$PngA = [IO.Path]::GetFullPath($PngA)
$PngB = [IO.Path]::GetFullPath($PngB)
if (-not $PlayerLog) { $PlayerLog = Join-Path (Split-Path $PngA) 'Player.log' }
if (-not $Report) { $Report = Join-Path (Split-Path $PngA) 'player_smoke.json' }
$PlayerLog = [IO.Path]::GetFullPath($PlayerLog)
$Report = [IO.Path]::GetFullPath($Report)
foreach ($path in @($PngA,$PngB,$PlayerLog,$Report)) {
  New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
}
$result = [ordered]@{ scope='standalone boot/render/clean exit only; no playthrough or ROM comparison'; exe=$Exe; startedUtc=[DateTime]::UtcNow.ToString('o'); success=$false }
$p = $null
$exitCode = 1
try {
  if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Executable missing: $Exe" }
  Add-Type -AssemblyName System.Drawing
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct WINRECT { public int Left, Top, Right, Bottom; }
public struct WINPOINT { public int X, Y; }
public class Win32Cap {
  public delegate bool EnumWindowProc(IntPtr h, IntPtr data);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowProc proc, IntPtr data);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out WINRECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out WINRECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref WINPOINT p);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public static IntPtr PlayerWindow(int processId) {
    IntPtr found=IntPtr.Zero; int area=0;
    EnumWindows((h,unused) => {
      uint pid; GetWindowThreadProcessId(h,out pid); WINRECT r;
      if(pid==processId && GetClientRect(h,out r) && r.Right*r.Bottom>area) {
        found=h; area=r.Right*r.Bottom;
      }
      return true;
    },IntPtr.Zero);
    return found;
  }
}
"@
  function CaptureWindow([System.Diagnostics.Process]$proc, [string]$out) {
    $proc.Refresh()
    if ($proc.HasExited) { throw 'Player exited before capture' }
    $h = $proc.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { $h = [Win32Cap]::PlayerWindow($proc.Id) }
    if ($h -eq [IntPtr]::Zero) { throw 'Player has no window' }
    $r = New-Object WINRECT
    if (-not [Win32Cap]::GetWindowRect($h, [ref]$r)) { throw 'GetWindowRect failed' }
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    if ($w -lt 16 -or $ht -lt 16) { throw "Invalid window dimensions $w x $ht" }
    $bmp = New-Object System.Drawing.Bitmap($w, $ht, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      try {
        $dc = $g.GetHdc()
        try { $ok = [Win32Cap]::PrintWindow($h, $dc, 2) }
        finally { $g.ReleaseHdc($dc) }
      } finally { $g.Dispose() }
      if (-not $ok) { throw 'PrintWindow failed' }
      $client = New-Object WINRECT
      $origin = New-Object WINPOINT
      if (-not [Win32Cap]::GetClientRect($h,[ref]$client) -or -not [Win32Cap]::ClientToScreen($h,[ref]$origin)) { throw 'Cannot locate client pixels' }
      $stats = MeasureClient $bmp ($origin.X-$r.Left) ($origin.Y-$r.Top) $client.Right $client.Bottom
      $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
      return [ordered]@{path=$out; width=$w; height=$ht; clientWidth=$client.Right; clientHeight=$client.Bottom; clientLeft=($origin.X-$r.Left); clientTop=($origin.Y-$r.Top); samples=$stats.samples; nonBlack=$stats.nonBlack; colors=$stats.colors; sha256=(Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash}
    } finally { $bmp.Dispose() }
  }
  $playerArgs = '-screen-fullscreen 0 -screen-width 768 -screen-height 720 -logFile "' + $PlayerLog + '"'
  if ($Replay) {
    $Replay = [IO.Path]::GetFullPath($Replay)
    if (-not (Test-Path -LiteralPath $Replay -PathType Leaf)) { throw "Replay missing: $Replay" }
    $assembly = Join-Path (Split-Path ([IO.Path]::GetFullPath($Exe))) (([IO.Path]::GetFileNameWithoutExtension($Exe)) + '_Data/Managed/Darkwing.UnityApp.dll')
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Cannot verify this project's Mono replay assembly: $assembly" }
    $metadata = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($assembly))
    $supported = $metadata.Contains('BeginReplay') -and $metadata.Contains('ReplayFrame') -and $metadata.Contains('ReplaySpec')
    $result.replayCapability = [ordered]@{ assembly=$assembly; sha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash; supported=$supported }
    if (-not $supported) { throw 'Player has no compiled replay support; build_player.py requires --development for -darkwing-replay' }
    $playerArgs += ' -darkwing-replay "' + $Replay + '"'
    $result.scope = 'Standalone buttons-only route from Awake; compare route state separately against original'
  }
  $result.arguments = $playerArgs
  # Viewport verification needs the interactive game visible; background helpers stay hidden.
  $windowStyle = if ($ShowGameWindow) { 'Normal' } else { 'Hidden' }
  $p = Start-Process -FilePath $Exe -ArgumentList $playerArgs -PassThru -WindowStyle $windowStyle
  $result.pid = $p.Id
  Start-Sleep -Seconds $FirstWaitSec
  $result.captureA = CaptureWindow $p $PngA
  Start-Sleep -Seconds $SecondWaitSec
  if ($Replay -and $ReplayTimeoutSec -gt 0) {
    $spec = Get-Content -LiteralPath $Replay -Raw | ConvertFrom-Json
    $route = Join-Path $spec.output 'route.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($ReplayTimeoutSec)
    while (-not (Test-Path -LiteralPath $route -PathType Leaf)) {
      $p.Refresh()
      if ($p.HasExited) { throw 'Player exited during replay' }
      if ([DateTime]::UtcNow -ge $deadline) { throw "Replay timeout: $route" }
      Start-Sleep -Seconds 2
    }
    Start-Sleep -Milliseconds 500
  }
  $result.captureB = CaptureWindow $p $PngB
  if ($Replay) {
    $spec = Get-Content -LiteralPath $Replay -Raw | ConvertFrom-Json
    $route = Join-Path $spec.output 'route.json'
    if (-not (Test-Path -LiteralPath $route -PathType Leaf)) { throw "Replay did not finish: $route" }
    $routeData = Get-Content -LiteralPath $route -Raw | ConvertFrom-Json
    if ($routeData.editor) { throw 'Replay came from Editor' }
    if ($CompletedMission) {
      $f = $routeData.final
      if ($f.frame -ne $spec.frames -or $f.phase -ne 'MapCursor' -or $f.completed -ne 1 -or $f.maskCompleted -ne 1) {
        throw 'Standalone replay did not complete the first mission and return to map'
      }
      $result.final = $f | Select-Object gameFrame,phase,world,x,y,state,completed,maskCompleted,scoreUnits,livesLeft,livesRight
    } elseif ($routeData.states[-1].phase -ne 'Mission') { throw 'Standalone replay did not reach mission' }
    $result.route = $route
  }
  $result.framesDiffer = $result.captureA.sha256 -ne $result.captureB.sha256
  if (-not $p.CloseMainWindow()) { throw 'CloseMainWindow refused' }
  if (-not $p.WaitForExit(10000)) { throw 'Player did not exit cleanly in 10 seconds' }
  $result.playerExitCode = $p.ExitCode
  $result.cleanExit = $true
  if ($p.ExitCode -ne 0) { throw "Player exit code $($p.ExitCode)" }
  if (-not (Test-Path -LiteralPath $PlayerLog)) { throw 'Player log missing' }
  $logText = Get-Content -LiteralPath $PlayerLog -Raw
  $result.log = $PlayerLog
  $result.exceptions = @([regex]::Matches($logText,'(?m)^(?:.*(?:NullReferenceException|IndexOutOfRangeException|Unhandled Exception|Exception:)|Error.*)$') | ForEach-Object Value)
  $result.bloomWarnings = ([regex]::Matches($logText,"Shader 'Hidden/Universal Render Pipeline/Bloom'")).Count
  if ($result.exceptions.Count -gt 0) { throw 'Player log contains exceptions/errors' }
  $result.success = $true
  $exitCode = 0
} catch { $result.error = $_.Exception.Message }
finally {
  if ($p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit(); $result.forcedCleanup=$true }
  $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
  $result.exitCode = $exitCode
  $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Report -Encoding utf8
}
$result | ConvertTo-Json -Depth 8
exit $exitCode
