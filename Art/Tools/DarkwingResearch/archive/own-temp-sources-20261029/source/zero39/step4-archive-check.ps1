$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
$sep = [IO.Path]::DirectorySeparatorChar
$inv = @{
  'a' = 'D:\Art\SW Code Zero Unity\Art\Temp\zero39\slot-a-legacy.tsv'
  'b' = 'D:\Art\SW Code Zero Unity\Art\Temp\zero39\slot-b-legacy.tsv'
}
foreach ($tag in 'a','b') {
  $archive = (Resolve-Path -LiteralPath ('D:\Art\SW Code Zero Unity - Git Backups\20261029-zero39-slot-' + $tag + '-legacy-archive')).Path
  $rows = @(Get-Content -LiteralPath $inv[$tag] | Where-Object { $_ -match '\S' })
  $ok = 0; $bad = @()
  foreach ($r in $rows) {
    $c = $r -split "`t"
    $rel = $c[0].Trim(); $bytes = [long]$c[1].Trim(); $sha = $c[2].Trim()
    $p = Join-Path $archive ($rel -replace '/', $sep)
    if (-not (Test-Path -LiteralPath $p)) { $bad += "MISSING $rel"; continue }
    $i = Get-Item -LiteralPath $p
    if ($i.Length -ne $bytes) { $bad += "SIZE $rel $bytes -> $($i.Length)"; continue }
    if ((Get-Sha256 $p) -ne $sha) { $bad += "HASH $rel"; continue }
    $ok++
  }
  $extra = @(Get-ChildItem -LiteralPath $archive -Recurse -File -Force | Where-Object { $_.Name -ne 'ARCHIVE-NOTE.txt' })
  Write-Output ("slot-$tag archive vs step-1 inventory: verified={0} of {1} rows, problems={2}, archived files={3}" -f $ok, $rows.Count, $bad.Count, $extra.Count)
  foreach ($b in $bad) { Write-Output ("  " + $b) }
  $wt = ('D:\Art\SW Code Zero Unity Builds\' + ('Mission2-Wolfduck','Mission3-Liquidator')[('a','b').IndexOf($tag)])
  $root = (Resolve-Path -LiteralPath $wt).Path
  $left = @(foreach ($lr in 'Playthroughs','Research','Tools') { $lp = $root + $sep + $lr; if (Test-Path -LiteralPath $lp) { $lr } })
  Write-Output ("  legacy roots still in worktree: " + ($left -join ', '))
}
