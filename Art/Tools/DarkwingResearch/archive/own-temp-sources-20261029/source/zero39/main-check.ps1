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

$main = 'D:\Art\SW Code Zero Unity'
$slots = [ordered]@{
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
$tab = [string][char]9
$outDir = 'D:\Art\SW Code Zero Unity\Art\Temp\zero39'

Write-Output '--- main root legacy dirs ---'
foreach ($d in @('Playthroughs', 'Research', 'Tools')) {
  $p = Join-Path $main $d
  if (Test-Path -LiteralPath $p) {
    $files = @(Get-ChildItem -LiteralPath $p -Recurse -File -Force)
    Write-Output ('  main/' + $d + ' EXISTS files=' + $files.Count)
  } else {
    Write-Output ('  main/' + $d + ' ABSENT')
  }
}

foreach ($name in $slots.Keys) {
  $root = $slots[$name]
  $tsv = Join-Path $outDir ($name + '-legacy.tsv')
  $rows = @(Get-Content -LiteralPath $tsv -Encoding UTF8 | Where-Object { $_.Trim() -ne '' })
  $report = @()
  foreach ($line in $rows) {
    $parts = $line -split $tab
    $rel = $parts[0]
    $sha = $parts[2]
    $mainArt = Join-Path $main (('Art/' + $rel) -replace '/', '\')
    if (Test-Path -LiteralPath $mainArt -PathType Leaf) {
      if ((Get-Sha256 -Path $mainArt) -eq $sha) { $state = 'IN-MAIN-ART-SAME' } else { $state = 'IN-MAIN-ART-DIFFER' }
    } elseif (Test-Path -LiteralPath $mainArt -PathType Container) {
      $state = 'IN-MAIN-ART-DIR'
    } else {
      $state = 'NOT-IN-MAIN-ART'
    }
    $report += [pscustomobject]@{ k = $rel; s = $state }
  }
  $dest = Join-Path $outDir ($name + '-maincheck.tsv')
  $report | ForEach-Object { "{0}$tab{1}" -f $_.k, $_.s } | Set-Content -LiteralPath $dest -Encoding UTF8
  $counts = $report | Group-Object -Property s | Sort-Object -Property Name
  Write-Output ($name + ': ' + (($counts | ForEach-Object { $_.Name + '=' + $_.Count }) -join ' '))
}
