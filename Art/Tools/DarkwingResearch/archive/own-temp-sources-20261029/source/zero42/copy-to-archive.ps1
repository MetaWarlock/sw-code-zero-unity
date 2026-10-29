$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() } }
$src  = (Resolve-Path -LiteralPath 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator').Path.TrimEnd('\')
$primary = (Resolve-Path -LiteralPath 'D:\Art\SW Code Zero Unity').Path.TrimEnd('\')
$arch = Join-Path $primary 'Art\Tools\DarkwingResearch\archive\liquidator-worktree-20261029\source'
$bound = $arch
$rows = @(Get-Content -LiteralPath 'D:\Art\SW Code Zero Unity\Art\Temp\zero42\temp-source-inventory.tsv' | Where-Object { $_ -match '\S' })
$copied = 0; $same = 0; $problems = @()
foreach ($r in $rows) {
  $c = $r -split "`t"; $rel = $c[0].Trim(); $bytes = [long]$c[1].Trim(); $sha = $c[2].Trim()
  $s = $src + '\' + ($rel -replace '/', '\')
  if (-not (Test-Path -LiteralPath $s)) { $problems += "SOURCE-MISSING $rel"; continue }
  $rp = (Resolve-Path -LiteralPath $s).Path
  if (-not $rp.StartsWith($src + '\')) { $problems += "SOURCE-OUTSIDE-SLOT-B $rel"; continue }
  $d = Join-Path $arch ($rel -replace '/', '\')
  $parent = [IO.Path]::GetDirectoryName($d)
  if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
  $rpp = (Resolve-Path -LiteralPath $parent).Path.TrimEnd('\')
  if ($rpp -ne $parent.TrimEnd('\')) { $problems += "DEST-REPARSE $rel"; continue }
  if ($rpp -ne $bound -and -not $rpp.StartsWith($bound + '\')) { $problems += "DEST-OUTSIDE-ARCHIVE $rel"; continue }
  if ((Get-Item -LiteralPath $s).Length -ne $bytes) { $problems += "SOURCE-SIZE-DRIFT $rel"; continue }
  if ((Get-Sha256 $s) -ne $sha) { $problems += "SOURCE-HASH-DRIFT $rel"; continue }
  if (Test-Path -LiteralPath $d) {
    if ((Get-Item -LiteralPath $d).Length -eq $bytes -and (Get-Sha256 $d) -eq $sha) { $same++ }
    else { $problems += "EXISTING-DIFFERENT-NO-OVERWRITE $rel" }
    continue }
  Copy-Item -LiteralPath $s -Destination $d
  if ((Get-Sha256 $d) -ne $sha -or (Get-Item -LiteralPath $d).Length -ne $bytes) { $problems += "COPY-MISMATCH $rel"; continue }
  $copied++ }
Write-Output ("copied={0} already-identical={1} problems={2} rows={3}" -f $copied, $same, $problems.Count, $rows.Count)
foreach ($p in $problems) { Write-Output ("  " + $p) }
$all = @(Get-ChildItem -LiteralPath $arch -Recurse -File)
Write-Output ("archive files now={0} archive bytes={1}" -f $all.Count, (($all | Measure-Object -Property Length -Sum).Sum))
