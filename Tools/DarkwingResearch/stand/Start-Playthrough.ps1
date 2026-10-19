# One-file launcher for the Darkwing Duck playthrough stand.
# Opens Mesen with the original ROM and starts Mesen's own movie recorder on it.
# Close the Mesen window to end the session; the follow-up commands are printed after it exits.
$ErrorActionPreference = "Stop"
$stand = Join-Path $PSScriptRoot "stand.py"
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = "py" }
& $python $stand record @args
