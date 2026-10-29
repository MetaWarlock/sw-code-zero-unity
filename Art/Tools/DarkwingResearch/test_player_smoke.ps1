$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'verify_player.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw $parseErrors[0] }
$function = $ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'MeasureClient'}, $true)
. ([scriptblock]::Create($function.Extent.Text))
$bmp = New-Object System.Drawing.Bitmap(784,759)
try {
  $graphics = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.FillRectangle([System.Drawing.Brushes]::Black,8,31,768,720)
    $graphics.Flush()
  } finally { $graphics.Dispose() }
  $rejected=$false
  try { MeasureClient $bmp 8 31 768 720 | Out-Null } catch { $rejected=$true }
  if (-not $rejected) { throw 'White window decorations concealed a black client' }
  $bmp.SetPixel(8,31,[System.Drawing.Color]::Red)
  $stats = MeasureClient $bmp 8 31 768 720
  if ($stats.nonBlack -ne 1 -or $stats.samples -ne 34560 -or $stats.colors -ne 2) { throw 'Client stats include window decorations or wrong sample coordinates' }
} finally { $bmp.Dispose() }
'PASS 2 player client-area checks'
