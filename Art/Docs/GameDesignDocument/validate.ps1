# Existing GDD entry point delegates to the common strict XML/DTD/link validator.
[CmdletBinding()]
param([string]$Path = '', [string]$Python = '')
$ErrorActionPreference='Stop'
if (-not $Path) { $Path=$PSScriptRoot }
if (-not $Python) {
  $runtime=Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
  $Python=if (Test-Path -LiteralPath $runtime) { $runtime } else { 'python' }
}
$entry=Join-Path $PSScriptRoot '../../Tools/DarkwingResearch/project_layout.py'
& $Python $entry validate --path $Path
exit $LASTEXITCODE
