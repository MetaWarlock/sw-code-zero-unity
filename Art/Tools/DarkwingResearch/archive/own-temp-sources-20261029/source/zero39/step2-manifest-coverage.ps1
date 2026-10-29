$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
foreach ($tag in 'a','b') {
  $dest = "D:\Art\SW Code Zero Unity - Git Backups\20261029-zero39-slot-$tag-legacy-roots"
  $man = Join-Path $dest 'manifest.json'
  $j = Get-Content -LiteralPath $man -Raw | ConvertFrom-Json
  $legacy = @($j.files.PSObject.Properties | Where-Object { $_.Name -match '^files/(Playthroughs|Research|Tools)/' })
  $groups = @{}
  foreach ($p in $legacy) {
    $top = ($p.Name -split '/')[1]
    if (-not $groups.ContainsKey($top)) { $groups[$top] = @{count=0; bytes=[long]0} }
    $groups[$top].count += 1
    $groups[$top].bytes += [long]$p.Value.bytes
  }
  Write-Output ("slot-$tag snapshot head=$($j.head) roots=$($j.roots -join ',')")
  Write-Output ("  total files={0} bytes={1} manifestSha256={2}" -f $j.files.Count, (($j.files.PSObject.Properties | Measure-Object -Property {[long]$_.Value.bytes} -Sum).Sum), (Get-Sha256 $man))
  foreach ($k in 'Playthroughs','Research','Tools') {
    if ($groups.ContainsKey($k)) { Write-Output ("  legacy {0}: files={1} bytes={2}" -f $k, $groups[$k].count, $groups[$k].bytes) }
    else { Write-Output ("  legacy {0}: ABSENT" -f $k) }
  }
  $probes = @(
    'files/Playthroughs/20261019T050329Z/input.bin',
    'files/Playthroughs/20261019T050329Z/movie.mmo',
    'files/Playthroughs/20261019T050329Z/replay1/buffers.bin',
    'files/Playthroughs/20261019T050329Z/replay1/chr_map.csv',
    'files/Playthroughs/20261019T050329Z/replay1/trace.csv',
    'files/Playthroughs/20261019T050329Z/replay1/stand_log.txt',
    'files/Playthroughs/20261019T050329Z/replay1/agent_stderr.txt',
    'files/Playthroughs/20261024T183247Z/input.bin',
    'files/Research/DarkwingDuck/extracted/raw/prg_07.bin',
    'files/Research/DarkwingDuck/extracted/raw/chr.bin',
    'files/Tools/DarkwingResearch/vendor/Mesen2/Mesen.exe',
    'files/Tools/DarkwingResearch/vendor/Mesen2/settings.json',
    'files/Tools/DarkwingResearch/vendor/Mesen2/MesenCore.dll'
  )
  foreach ($p in $probes) {
    if ($null -ne $j.files.PSObject.Properties[$p]) {
      $f = Join-Path $dest ($p -replace '/','\')
      $disk = Get-Sha256 $f
      $ok = if ($disk -eq $j.files.$p.sha256) { 'OK' } else { 'MISMATCH' }
      Write-Output ("  COVERED $ok $p bytes=$($j.files.$p.bytes) sha=$disk")
    } else { Write-Output ("  NOT IN MANIFEST $p") }
  }
  $pyc = @($j.files.PSObject.Properties | Where-Object { $_.Name -match '\.pyc$' })
  Write-Output ("  pyc entries in manifest = {0}" -f $pyc.Count)
}
