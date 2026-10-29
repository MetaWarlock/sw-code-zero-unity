$ErrorActionPreference = 'Stop'
$targets = [ordered]@{
  'main'   = 'D:\Art\SW Code Zero Unity'
  'slot-a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'slot-b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
foreach ($name in $targets.Keys) {
  $root = $targets[$name]
  Write-Output ('=== ' + $name)
  foreach ($d in @('Art\Playthroughs', 'Art\Research', 'Art\Tools', 'Art\Research\DarkwingDuck', 'Art\Research\DarkwingDuck\extracted', 'Art\Tools\DarkwingResearch')) {
    $p = Join-Path $root $d
    if (Test-Path -LiteralPath $p) {
      $files = @(Get-ChildItem -LiteralPath $p -Recurse -File -Force)
      $bytes = ($files | Measure-Object -Property Length -Sum).Sum
      if ($null -eq $bytes) { $bytes = 0 }
      Write-Output ('  ' + $d + ' EXISTS files=' + $files.Count + ' bytes=' + $bytes)
    } else {
      Write-Output ('  ' + $d + ' ABSENT')
    }
  }
}
