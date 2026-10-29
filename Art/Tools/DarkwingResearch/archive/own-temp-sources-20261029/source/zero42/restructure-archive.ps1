$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $s = [System.IO.File]::OpenRead($Path); try { $b = $sha.ComputeHash($s) } finally { $s.Dispose() }
        return ([System.BitConverter]::ToString($b) -replace '-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
$sep = [IO.Path]::DirectorySeparatorChar
$arch = (Resolve-Path -LiteralPath 'D:\Art\SW Code Zero Unity\Art\Tools\DarkwingResearch\archive\liquidator-worktree-20261029\source').Path.TrimEnd('\')
$rows = @(Get-Content -LiteralPath 'D:\Art\SW Code Zero Unity\Art\Temp\zero42\temp-source-inventory.tsv' | Where-Object { $_ -match '\S' })
$moved = 0; $already = 0; $problems = @()
foreach ($r in $rows) {
  $c = $r -split "`t"
  $rel = $c[0].Trim(); $bytes = [long]$c[1].Trim(); $sha = $c[2].Trim()
  if (-not ($rel.StartsWith('Temp/') -or $rel.StartsWith('Temp' + $sep))) { $problems += "UNEXPECTED ROOT $rel"; continue }
  $below = $rel.Substring(5)
  $old = Join-Path $arch ($rel -replace '/', $sep)
  $new = Join-Path $arch ($below -replace '/', $sep)
  if (-not (Test-Path -LiteralPath $new)) {
    if (-not (Test-Path -LiteralPath $old)) { $problems += "ABSENT $rel"; continue }
    $np = [IO.Path]::GetDirectoryName($new)
    if (-not (Test-Path -LiteralPath $np)) { New-Item -ItemType Directory -Path $np | Out-Null }
    $rp = (Resolve-Path -LiteralPath $np).Path.TrimEnd('\')
    if (-not ($rp -eq $arch -or $rp.StartsWith($arch + $sep, [StringComparison]::Ordinal))) { $problems += "ESCAPE $new"; continue }
    Move-Item -LiteralPath $old -Destination $new
    $moved++
  } else { $already++ }
  $i = Get-Item -LiteralPath $new
  if ($i.Length -ne $bytes) { $problems += "SIZE $rel" }
  elseif ((Get-Sha256 $new) -ne $sha) { $problems += "HASH $rel" }
  if (Test-Path -LiteralPath $old) { $problems += "STILL-AT-OLD $rel" }
}
$left = @(Get-ChildItem -LiteralPath (Join-Path $arch ('Temp' + $sep)) -Recurse -File -Force -ErrorAction SilentlyContinue)
if ($left.Count -eq 0) { Remove-Item -LiteralPath (Join-Path $arch 'Temp') -Recurse -Force; $note = 'empty Temp tree removed' }
else { $note = 'Temp tree KEPT (' + $left.Count + ' files)' }
Write-Output ("moved={0} already={1} problems={2} rows={3} : {4}" -f $moved, $already, $problems.Count, $rows.Count, $note)
foreach ($p in $problems) { Write-Output ('  ' + $p) }
$files = @(Get-ChildItem -LiteralPath $arch -Recurse -File -Force)
Write-Output ('archive source files now=' + $files.Count + ' bytes=' + (($files | Measure-Object -Property Length -Sum).Sum))
