$ErrorActionPreference = 'Stop'
$main = 'D:\Art\SW Code Zero Unity'
$slots = [ordered]@{
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}

function Get-RelSet {
  param([string]$Root, [string]$Rel)
  $p = Join-Path $Root ($Rel -replace '/', '\')
  if (-not (Test-Path -LiteralPath $p)) { return @{} }
  $len = $Root.Length + 1
  $map = @{}
  foreach ($f in @(Get-ChildItem -LiteralPath $p -Recurse -File -Force)) {
    $map[($f.FullName.Substring($len) -replace '\\', '/')] = $f.Length
  }
  return $map
}

Write-Output '=== ROM candidates (*.nes) ==='
foreach ($name in (@('main') + $slots.Keys)) {
  $root = if ($name -eq 'main') { $main } else { $slots[$name] }
  $hits = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force -Filter '*.nes' -ErrorAction SilentlyContinue)
  foreach ($h in $hits) {
    Write-Output ('  ' + $name + ' ' + $h.FullName.Substring($root.Length + 1) + ' ' + $h.Length)
  }
}

Write-Output '=== vendor Mesen per worktree ==='
foreach ($name in (@('main') + $slots.Keys)) {
  $root = if ($name -eq 'main') { $main } else { $slots[$name] }
  foreach ($base in @('Art/Tools/DarkwingResearch/vendor/Mesen2', 'Tools/DarkwingResearch/vendor/Mesen2')) {
    $map = Get-RelSet -Root $root -Rel $base
    Write-Output ('  ' + $name + ' ' + $base + ' files=' + $map.Count)
    foreach ($k in ($map.Keys | Sort-Object)) { Write-Output ('      ' + $k + ' ' + $map[$k]) }
  }
}

Write-Output '=== Art payload set difference: main has, slot lacks ==='
foreach ($name in $slots.Keys) {
  $root = $slots[$name]
  foreach ($rel in @('Art/Playthroughs', 'Art/Research', 'Art/Tools')) {
    $m = Get-RelSet -Root $main -Rel $rel
    $s = Get-RelSet -Root $root -Rel $rel
    $missing = @($m.Keys | Where-Object { -not $s.ContainsKey($_) })
    $extra = @($s.Keys | Where-Object { -not $m.ContainsKey($_) })
    $bytes = 0
    foreach ($k in $missing) { $bytes += $m[$k] }
    Write-Output ('  ' + $name + ' ' + $rel + ' main=' + $m.Count + ' slot=' + $s.Count + ' missingInSlot=' + $missing.Count + ' (' + $bytes + ' B) slotOnly=' + $extra.Count)
  }
}
