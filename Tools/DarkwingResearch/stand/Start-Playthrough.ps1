# One-file launcher for the Darkwing Duck playthrough stand.
# Records the original ROM by default; -ReplayMovie plays an existing Mesen .mmo instead.
# Close the Mesen window to end the session; the follow-up commands are printed after it exits.
param([string]$ReplayMovie)
$ErrorActionPreference = "Stop"
if ($ReplayMovie) {
  $moviePath = (Resolve-Path -LiteralPath $ReplayMovie).Path
  if ([IO.Path]::GetExtension($moviePath) -ne '.mmo') { throw 'Expected a Mesen .mmo recording' }
  $mesenPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../vendor/Mesen2/Mesen.exe')).Path
  $romPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../../Art/Darkwing Duck (USA).nes')).Path
  $configPath = Join-Path (Split-Path $mesenPath) 'settings.json'
  $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
  if (-not $config.Preferences.SingleInstance) { throw 'Mesen SingleInstance must be enabled for movie playback' }
  Start-Process -FilePath $mesenPath -ArgumentList ('"' + $romPath + '" --emulation.emulationSpeed=100 --doNotSaveSettings') -WindowStyle Normal | Out-Null
  $deadline = [DateTime]::UtcNow.AddSeconds(20)
  do {
    $ready = @(Get-Process -Name Mesen -ErrorAction SilentlyContinue | Where-Object {
      $_.Path -eq $mesenPath -and $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -like '*Darkwing Duck*'
    }).Count -gt 0
    if ($ready) { break }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $deadline)
  if (-not $ready) { throw 'Mesen did not finish loading Darkwing Duck' }
  Start-Process -FilePath $mesenPath -ArgumentList ('"' + $moviePath + '"') -WindowStyle Normal | Out-Null
  exit 0
}
$stand = Join-Path $PSScriptRoot "stand.py"
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = "py" }
& $python $stand record @args
exit $LASTEXITCODE
