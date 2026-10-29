$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
$sep = [IO.Path]::DirectorySeparatorChar
$slotb = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
$arch = 'D:\Art\SW Code Zero Unity - Git Backups\20261029-zero42-slot-b-temp-source-relocated'
$rows = @(Get-Content -LiteralPath 'D:\Art\SW Code Zero Unity\Art\Temp\zero42\temp-source-inventory.tsv' | Where-Object { $_ -match '\S' })
if (-not (Test-Path -LiteralPath $arch)) { New-Item -ItemType Directory -Path $arch | Out-Null }
$root = (Resolve-Path -LiteralPath $arch).Path.TrimEnd('\')
$moved = 0; $already = 0; $problems = @()
foreach ($r in $rows) {
  $c = $r -split "`t"
  $rel = $c[0].Trim(); $bytes = [long]$c[1].Trim(); $sha = $c[2].Trim()
  if (-not ($rel.StartsWith('Temp/') -or $rel.StartsWith('Temp' + $sep))) { $problems += "UNEXPECTED ROOT $rel"; continue }
  $src = Join-Path $slotb ($rel -replace '/', $sep)
  $new = Join-Path $root ($rel -replace '/', $sep)
  if (Test-Path -LiteralPath $new) {
    $i = Get-Item -LiteralPath $new
    if ($i.Length -ne $bytes -or (Get-Sha256 $new) -ne $sha) { $problems += "PREEXISTING-MISMATCH $rel" } else { $already++ }
    if (Test-Path -LiteralPath $src) { $problems += "BOTH-SIDES $rel" }
    continue
  }
  if (-not (Test-Path -LiteralPath $src)) { $problems += "ABSENT-SOURCE $rel"; continue }
  $si = Get-Item -LiteralPath $src
  if ($si.Length -ne $bytes) { $problems += "SOURCE-SIZE $rel"; continue }
  if ((Get-Sha256 $src) -ne $sha) { $problems += "SOURCE-HASH $rel"; continue }
  $np = [IO.Path]::GetDirectoryName($new)
  if (-not (Test-Path -LiteralPath $np)) { New-Item -ItemType Directory -Path $np | Out-Null }
  $rp = (Resolve-Path -LiteralPath $np).Path.TrimEnd('\')
  if (-not ($rp -eq $root -or $rp.StartsWith($root + $sep, [StringComparison]::Ordinal))) { $problems += "ESCAPE $new"; continue }
  Move-Item -LiteralPath $src -Destination $new
  $moved++
  $di = Get-Item -LiteralPath $new
  if ($di.Length -ne $bytes -or (Get-Sha256 $new) -ne $sha) { $problems += "DEST-MISMATCH $rel" }
  if (Test-Path -LiteralPath $src) { $problems += "STILL-AT-SOURCE $rel" }
}
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.Name -ne 'ARCHIVE-NOTE.txt' })
Write-Output ("moved={0} already={1} problems={2} rows={3} relocatedFiles={4} bytes={5}" -f $moved, $already, $problems.Count, $rows.Count, $files.Count, (($files | Measure-Object -Property Length -Sum).Sum))
foreach ($p in $problems) { Write-Output ('  ' + $p) }
$still = @(Get-ChildItem -LiteralPath (Join-Path $slotb 'Temp') -Recurse -File -Force -ErrorAction SilentlyContinue)
$suffixes = '.py', '.ps1', '.lua', '.cs', '.csproj', '.sh', '.cmd', '.bat'
$stillsrc = @($still | Where-Object { $suffixes -contains $_.Extension.ToLowerInvariant() })
Write-Output ("slotB-Temp-files-after={0} source-suffix-after={1}" -f $still.Count, $stillsrc.Count)
foreach ($x in $stillsrc) { Write-Output ('  REMAINS ' + $x.FullName.Substring($slotb.Length + 1)) }
