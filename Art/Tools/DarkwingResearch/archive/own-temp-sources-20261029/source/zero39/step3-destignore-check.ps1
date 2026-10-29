$ErrorActionPreference = 'Continue'
$slots = @{
  'a' = 'D:\Art\SW Code Zero Unity Builds\Mission2-Wolfduck'
  'b' = 'D:\Art\SW Code Zero Unity Builds\Mission3-Liquidator'
}
function Get-NulTerminated([byte[]]$bytes) {
  $text = [System.Text.Encoding]::UTF8.GetString($bytes)
  return @($text -split "`0" | Where-Object { $_ -ne '' })
}
foreach ($tag in 'a','b') {
  $wt = $slots[$tag]
  $candidate = @()
  foreach ($lr in 'Playthroughs','Research','Tools') {
    $base = Join-Path $wt $lr
    if (-not (Test-Path -LiteralPath $base)) { continue }
    Get-ChildItem -LiteralPath $base -Recurse -File -Force | ForEach-Object {
      $rel = $_.FullName.Substring($wt.Length + 1) -replace '\\','/'
      if ($rel -match '__pycache__') { return }
      $candidate += 'Art/' + $rel
    }
  }
  $stdin = ($candidate -join "`0") + "`0"
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($stdin)
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = 'git'
  $psi.Arguments = '-C "' + $wt + '" check-ignore --no-index --stdin -z'
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.UseShellExecute = $false
  $p = [System.Diagnostics.Process]::Start($psi)
  $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
  $p.StandardInput.Close()
  $out = New-Object System.IO.MemoryStream
  $p.StandardOutput.BaseStream.CopyTo($out)
  $p.WaitForExit()
  $ignored = Get-NulTerminated $out.ToArray()

  $psi2 = New-Object System.Diagnostics.ProcessStartInfo
  $psi2.FileName = 'git'
  $psi2.Arguments = '-C "' + $wt + '" ls-files -z'
  $psi2.RedirectStandardOutput = $true
  $psi2.UseShellExecute = $false
  $p2 = [System.Diagnostics.Process]::Start($psi2)
  $out2 = New-Object System.IO.MemoryStream
  $p2.StandardOutput.BaseStream.CopyTo($out2)
  $p2.WaitForExit()
  $tracked = @{}
  foreach ($t in (Get-NulTerminated $out2.ToArray())) { $tracked[$t] = $true }

  $ignoredSet = @{}
  foreach ($i in $ignored) { $ignoredSet[$i] = $true }
  $exposed = @(); $trackHits = 0
  foreach ($c in $candidate) {
    if ($ignoredSet.ContainsKey($c)) { continue }
    if ($tracked.ContainsKey($c)) { $trackHits++; continue }
    $exposed += $c
  }
  Write-Output ("slot-$tag candidates={0} ignored={1} tracked-not-ignored={2} NEITHER={3}" -f $candidate.Count, $ignoredSet.Count, $trackHits, $exposed.Count)
  $shown = 0
  foreach ($e in $exposed) { if ($shown -ge 30) { Write-Output ('  ... ' + ($exposed.Count - $shown) + ' more'); break }; Write-Output ('  NEITHER ' + $e); $shown++ }
}
