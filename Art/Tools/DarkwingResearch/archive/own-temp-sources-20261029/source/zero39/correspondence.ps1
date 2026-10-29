$ErrorActionPreference = 'Stop'

function Get-Sha256 {
  param([string]$Path)
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $stream = [System.IO.File]::OpenRead($Path)
    try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
  } finally { $sha.Dispose() }
}

$slots = [ordered]@{
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
$tab = [string][char]9
$outDir = 'D:\Art\SW Code Zero Unity\Art\Temp\zero39'

foreach ($name in $slots.Keys) {
  $root = $slots[$name]
  $tsv = Join-Path $outDir ($name + '-legacy.tsv')
  $rows = @(Get-Content -LiteralPath $tsv -Encoding UTF8 | Where-Object { $_.Trim() -ne '' })
  $report = @()
  foreach ($line in $rows) {
    $parts = $line -split $tab
    $rel = $parts[0]
    $sha = $parts[2]
    $artRel = 'Art/' + $rel
    $artPath = Join-Path $root ($artRel -replace '/', '\')
    if (Test-Path -LiteralPath $artPath -PathType Leaf) {
      $artSha = Get-Sha256 -Path $artPath
      if ($artSha -eq $sha) { $state = 'SAME' } else { $state = 'DIFFER' }
    } elseif (Test-Path -LiteralPath $artPath -PathType Container) {
      $state = 'DIFFER-DIR-COLLISION'
    } else {
      $state = 'MISSING'
    }
    $report += [pscustomobject]@{ k = $rel; a = $artRel; s = $state }
  }
  $dest = Join-Path $outDir ($name + '-correspondence.tsv')
  $report | ForEach-Object { "{0}$tab{1}$tab{2}" -f $_.k, $_.a, $_.s } | Set-Content -LiteralPath $dest -Encoding UTF8
  $counts = $report | Group-Object -Property s | Sort-Object -Property Name
  Write-Output ($name + ': ' + (($counts | ForEach-Object { $_.Name + '=' + $_.Count }) -join ' '))
}
