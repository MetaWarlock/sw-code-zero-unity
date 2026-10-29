$ErrorActionPreference = 'Stop'
$slots = [ordered]@{
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
foreach ($name in $slots.Keys) {
  $root = $slots[$name]
  Write-Output ("=== " + $name + " " + $root)
  foreach ($d in @('Playthroughs', 'Research', 'Tools')) {
    $p = Join-Path $root $d
    if (Test-Path -LiteralPath $p) {
      $files = @(Get-ChildItem -LiteralPath $p -Recurse -File -Force)
      $bytes = ($files | Measure-Object -Property Length -Sum).Sum
      if ($null -eq $bytes) { $bytes = 0 }
      $dirs = @(Get-ChildItem -LiteralPath $p -Recurse -Directory -Force)
      Write-Output ("  " + $d + " files=" + $files.Count + " dirs=" + $dirs.Count + " bytes=" + $bytes)
      $exts = $files | Group-Object -Property Extension | Sort-Object -Property Count -Descending
      foreach ($e in $exts) {
        Write-Output ("    ext " + $e.Name + " = " + $e.Count)
      }
    } else {
      Write-Output ("  " + $d + " ABSENT")
    }
  }
}
