$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
$sep = [IO.Path]::DirectorySeparatorChar
$slots = [ordered]@{
  'a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
foreach ($tag in $slots.Keys) {
  $wt = (Resolve-Path -LiteralPath $slots[$tag]).Path
  $same = 0; $bad = @(); $cache = 0
  foreach ($lr in 'Playthroughs','Research','Tools') {
    $srcBase = $wt + $sep + $lr
    foreach ($f in (Get-ChildItem -LiteralPath $srcBase -Recurse -File -Force)) {
      $rel = $f.FullName.Substring($wt.Length + 1)
      $posix = $rel -replace '\\','/'
      if ($posix -match '(^|/)__pycache__/') { $cache++; continue }
      $dst = $wt + $sep + 'Art' + $sep + $rel
      if (-not (Test-Path -LiteralPath $dst)) { $bad += "MISSING $posix"; continue }
      if ((Get-Sha256 $dst) -eq (Get-Sha256 $f.FullName)) { $same++ } else { $bad += "DIFFER $posix" }
    }
  }
  Write-Output ("slot-$tag correspondence after copy: SAME={0} problems={1} cache-skipped={2}" -f $same, $bad.Count, $cache)
  foreach ($b in $bad) { Write-Output ("  " + $b) }
  $routes = @(
    'Art\Playthroughs\20261019T050329Z\input.bin',
    'Art\Playthroughs\20261019T212535Z\input.bin',
    'Art\Playthroughs\20261022T164819Z\input.bin',
    'Art\Playthroughs\20261024T183247Z\input.bin',
    'Art\Playthroughs\20261019T212535Z\replay1\trace.csv',
    'Art\Playthroughs\20261019T212535Z\replay1\buffers.bin',
    'Art\Tools\DarkwingResearch\vendor\Mesen2\Mesen.exe',
    'Art\Tools\DarkwingResearch\vendor\Mesen2\settings.json',
    'Art\Darkwing Duck (USA).nes',
    'Art\Research\DarkwingDuck\extracted\raw\prg.bin',
    'Art\Research\DarkwingDuck\extracted\raw\chr.bin'
  )
  foreach ($r in $routes) {
    $p = $wt + $sep + $r
    if (Test-Path -LiteralPath $p) {
      $i = Get-Item -LiteralPath $p
      Write-Output ("  PRESENT {0} bytes={1} sha={2}" -f $r, $i.Length, (Get-Sha256 $p).Substring(0,16))
    } else { Write-Output ("  ABSENT  " + $r) }
  }
}
