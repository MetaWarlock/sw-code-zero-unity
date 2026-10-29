$ErrorActionPreference = 'Stop'
$roots = [ordered]@{
  'main'   = 'D:\Art\SW Code Zero Unity'
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
$check = @('Library', 'Temp', 'obj', 'Build', 'Builds', 'Logs', 'Art/Temp', 'Art/Build', 'Art/Unity', 'Art/Unity/CodeZero', 'Assets', 'Packages', 'ProjectSettings', 'UserSettings')
foreach ($name in $roots.Keys) {
  $root = $roots[$name]
  Write-Output ('=== ' + $name)
  foreach ($rel in $check) {
    $p = Join-Path $root ($rel -replace '/', '\')
    if (Test-Path -LiteralPath $p) {
      $files = @(Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue)
      Write-Output ('  ' + $rel + ' EXISTS files=' + $files.Count)
    } else {
      Write-Output ('  ' + $rel + ' ABSENT')
    }
  }
}
