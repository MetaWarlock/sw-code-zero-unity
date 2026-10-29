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
$log = New-Object System.Collections.ArrayList
foreach ($tag in $slots.Keys) {
  $wt = (Resolve-Path -LiteralPath $slots[$tag]).Path
  $artRoot = (Resolve-Path -LiteralPath ($wt + $sep + 'Art')).Path
  foreach ($lr in 'Playthroughs','Research','Tools') {
    $srcBase = (Resolve-Path -LiteralPath ($wt + $sep + $lr)).Path
    if (-not $srcBase.StartsWith($wt + $sep)) { throw "source outside assigned worktree: $srcBase" }
    foreach ($f in (Get-ChildItem -LiteralPath $srcBase -Recurse -File -Force)) {
      $rel = $f.FullName.Substring($wt.Length + 1)
      $posix = $rel -replace '\\','/'
      if ($posix -match '(^|/)__pycache__/') {
        [void]$log.Add([pscustomobject]@{slot=$tag; action='SKIPPED-CACHE'; rel=$posix; bytes=$f.Length; sha=''})
        continue
      }
      $dst = Join-Path $artRoot $rel
      if (-not $dst.StartsWith($artRoot + $sep)) { throw "destination outside Art: $dst" }
      $parent = [IO.Path]::GetDirectoryName($dst)
      if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
      $rp = (Resolve-Path -LiteralPath $parent).Path.TrimEnd($sep)
      if ($rp -ne $parent.TrimEnd($sep)) { throw "reparse/aliased destination parent: $parent -> $rp" }
      $srcSha = Get-Sha256 $f.FullName
      if (Test-Path -LiteralPath $dst) {
        $dstSha = Get-Sha256 $dst
        if ($dstSha -eq $srcSha) {
          [void]$log.Add([pscustomobject]@{slot=$tag; action='ALREADY-PRESENT-SAME'; rel=$posix; bytes=$f.Length; sha=$srcSha})
        } else {
          [void]$log.Add([pscustomobject]@{slot=$tag; action='HELD-DIFFERENT-NO-OVERWRITE'; rel=$posix; bytes=$f.Length; sha=("src=" + $srcSha + " dst=" + $dstSha)})
        }
        continue
      }
      Copy-Item -LiteralPath $f.FullName -Destination $dst
      if ((Get-Sha256 $dst) -ne $srcSha) { throw "copy mismatch: $posix" }
      [void]$log.Add([pscustomobject]@{slot=$tag; action='COPIED'; rel=$posix; bytes=$f.Length; sha=$srcSha})
    }
  }
}
$tab = [string][char]9
$rows = foreach ($e in $log) { (@($e.slot, $e.action, $e.rel, $e.bytes, $e.sha) -join $tab) }
$out = 'D:\Art\SW Code Zero Unity\Art\Temp\zero39\step3-copy.log.tsv'
Set-Content -LiteralPath $out -Value ((@(($('slot','action','rel','bytes','sha256') -join $tab)) + $rows)) -Encoding UTF8
Write-Output ("log rows = {0}" -f $log.Count)
foreach ($g in ($log | Group-Object slot)) {
  Write-Output ("slot-{0}: {1}" -f $g.Name, ((($g.Group | Group-Object action) | ForEach-Object { $_.Name + '=' + $_.Count }) -join ' '))
}
foreach ($e in ($log | Where-Object { $_.action -ne 'COPIED' })) {
  Write-Output ("  {0} {1} bytes={2} {3}" -f $e.slot, $e.action, $e.bytes, $e.rel)
}
