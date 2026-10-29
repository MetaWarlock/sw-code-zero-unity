$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
foreach ($tag in 'a','b') {
  $dest = "D:\Art\SW Code Zero Unity - Git Backups\20261029-zero39-slot-$tag-legacy-roots"
  $j = Get-Content -LiteralPath (Join-Path $dest 'manifest.json') -Raw | ConvertFrom-Json
  $legacy = @($j.files.PSObject.Properties | Where-Object { $_.Name -match '^files/(Playthroughs|Research|Tools)/' })
  Write-Output ("slot-$tag legacy manifest entries = {0}" -f $legacy.Count)
  $probes = @(
    'files/Playthroughs/20261019T212535Z/replay1/buffers.bin',
    'files/Playthroughs/20261019T212535Z/replay1/chr_map.csv',
    'files/Playthroughs/20261019T212535Z/replay1/trace.csv',
    'files/Playthroughs/20261019T212535Z/replay1/stand_log.txt',
    'files/Playthroughs/20261019T212535Z/replay1/agent_stderr.txt',
    'files/Playthroughs/20261019T212535Z/replay2/buffers.bin',
    'files/Playthroughs/20261019T212535Z/replay2/trace.csv',
    'files/Playthroughs/20261022T164819Z/input.bin',
    'files/Playthroughs/20261019T212535Z/input.bin',
    'files/Research/DarkwingDuck/extracted/raw/ui_map_1_nametable_candidate.bin',
    'files/Tools/DarkwingResearch/vendor/Mesen2/libSkiaSharp.dll',
    'files/Tools/DarkwingResearch/vendor/Mesen2/MesenNesDB.txt'
  )
  foreach ($p in $probes) {
    if ($null -ne $j.files.PSObject.Properties[$p]) {
      $f = Join-Path $dest ($p -replace '/','\')
      $disk = Get-Sha256 $f
      $ok = if ($disk -eq $j.files.$p.sha256) { 'OK' } else { 'MISMATCH' }
      Write-Output ("  COVERED $ok $p bytes=$($j.files.$p.bytes) sha=$($disk.Substring(0,16))…")
    } else { Write-Output ("  NOT IN MANIFEST $p") }
  }
  if ($tag -eq 'a') {
    Write-Output '  slot-a .cdl debug logs:'
    foreach ($p in @($legacy | Where-Object { $_.Name -match '\.cdl$' })) { Write-Output ("   {0} bytes={1} sha={2}" -f $p.Name, $p.Value.bytes, $p.Value.sha256.Substring(0,16)) }
  }
}
