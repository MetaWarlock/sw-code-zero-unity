$ErrorActionPreference = 'Stop'
$sep = [IO.Path]::DirectorySeparatorChar
$backupRoot = 'D:\Art\SW Code Zero Unity - Git Backups'
$utc = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$slots = [ordered]@{
  'a' = @{ wt = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'; snap = '20261029-zero39-slot-a-legacy-roots'; manifest = '8dc94e991828edea0436b9764af798c7f1ddecfe06b36ded9aa37da9726128cf' }
  'b' = @{ wt = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'; snap = '20261029-zero39-slot-b-legacy-roots'; manifest = 'b1051e14999dea2141370af638aa33c23b44c456bf6aa3b868089664c5be0a9f' }
}
$resolvedBackup = (Resolve-Path -LiteralPath $backupRoot).Path.TrimEnd($sep)
Write-Output ("backup root = {0}" -f $resolvedBackup)
foreach ($tag in $slots.Keys) {
  $cfg = $slots[$tag]
  $wt = (Resolve-Path -LiteralPath $cfg.wt).Path.TrimEnd($sep)
  $archiveRaw = Join-Path $resolvedBackup ('20261029-zero39-slot-' + $tag + '-legacy-archive')
  if (Test-Path -LiteralPath $archiveRaw) { throw "archive directory already exists, refusing to merge: $archiveRaw" }
  $archive = New-Item -ItemType Directory -Path $archiveRaw -Force | ForEach-Object { $_.FullName.TrimEnd($sep) }
  # destination must be strictly inside the permitted backup root and must not be inside any snapshot
  if (-not $archive.StartsWith($resolvedBackup + $sep)) { throw "destination outside permitted backup root: $archive" }
  $snapDir = Join-Path $resolvedBackup $cfg.snap
  if ($archive.StartsWith((Resolve-Path -LiteralPath $snapDir).Path)) { throw "destination is inside an immutable snapshot" }
  foreach ($lr in 'Playthroughs','Research','Tools') {
    $src = (Resolve-Path -LiteralPath (Join-Path $wt $lr)).Path
    if (-not $src.StartsWith($wt + $sep)) { throw "source outside assigned worktree: $src" }
    $before = @(Get-ChildItem -LiteralPath $src -Recurse -File -Force)
    $bytesBefore = ($before | Measure-Object -Property Length -Sum).Sum
    $dst = Join-Path $archive $lr
    if (Test-Path -LiteralPath $dst) { throw "destination exists, refusing to overwrite: $dst" }
    Move-Item -LiteralPath $src -Destination $dst
    if (Test-Path -LiteralPath $src) { throw "source still present after move: $src" }
    $after = @(Get-ChildItem -LiteralPath $dst -Recurse -File -Force)
    $bytesAfter = ($after | Measure-Object -Property Length -Sum).Sum
    Write-Output ("slot-$tag moved {0}: files {1}->{2} bytes {3}->{4} -> {5}" -f $lr, $before.Count, $after.Count, $bytesBefore, $bytesAfter, $dst)
  }
  $note = @(
    ('slot-{0} legacy root archive' -f $tag),
    ('movedUtc={0}' -f $utc),
    ('sourceWorktree={0}' -f $wt),
    ('branch=codex/orchestration-slot-{0}' -f $tag),
    ('head=3fe92b549362463db53b670624a8ce5fdc8aca4a'),
    ('verifiedSnapshot=D:\Art\SW Code Zero Unity - Git Backups\{0}' -f $cfg.snap),
    ('snapshotManifestSha256={0}' -f $cfg.manifest),
    'moved with PowerShell Move-Item, no deletion, nothing overwritten',
    'original old-layout bytes retained here; the Art/ copies in the worktree were filled from these files',
    'this directory is an archive, NOT a preserve.py snapshot and not referenced by any manifest'
  )
  Set-Content -LiteralPath (Join-Path $archive 'ARCHIVE-NOTE.txt') -Value $note -Encoding UTF8
}
Write-Output 'step4 complete'
