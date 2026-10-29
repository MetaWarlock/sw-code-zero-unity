$ErrorActionPreference = 'Stop'

function Get-Sha256 {
  param([string]$Path)
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
      $bytes = $sha.ComputeHash($stream)
    } finally {
      $stream.Dispose()
    }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
  } finally {
    $sha.Dispose()
  }
}

$slots = [ordered]@{
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
$out = 'D:\Art\SW Code Zero Unity\Art\Temp\zero39'
$tab = [string][char]9
foreach ($name in $slots.Keys) {
  $root = $slots[$name]
  $lines = @()
  foreach ($d in @('Playthroughs', 'Research', 'Tools')) {
    $p = Join-Path $root $d
    if (-not (Test-Path -LiteralPath $p)) { continue }
    foreach ($f in @(Get-ChildItem -LiteralPath $p -Recurse -File -Force)) {
      $rel = $f.FullName.Substring($root.Length + 1) -replace '\\', '/'
      $lines += [pscustomobject]@{
        k    = $rel
        b    = $f.Length
        s    = (Get-Sha256 -Path $f.FullName)
      }
    }
  }
  $dest = Join-Path $out ($name + '-legacy.tsv')
  $lines | Sort-Object -Property k | ForEach-Object { "{0}$tab{1}$tab{2}" -f $_.k, $_.b, $_.s } | Set-Content -LiteralPath $dest -Encoding UTF8
  Write-Output ($name + ' files=' + $lines.Count + ' -> ' + $dest)
}
