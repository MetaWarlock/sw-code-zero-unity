$ErrorActionPreference = 'Stop'
$waiter = Join-Path $PSScriptRoot 'acpx_wait_first.ps1'
$project = Split-Path (Split-Path (Split-Path $PSScriptRoot))
$out = Join-Path $project ('Art/Temp/wait-first-control-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$children = [Collections.Generic.List[object]]::new()
function Start-Fixture([string]$label, [int]$seconds) {
    $child = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-Command',"Start-Sleep -Seconds $seconds")
    $children.Add($child)
    [pscustomobject]@{task=$label; processId=$child.Id; startedUtc=$child.StartTime.ToUniversalTime().ToString('o'); execSessionId=$child.Id}
}
try {
    $slow = Start-Fixture 'slow' 40
    $fast = Start-Fixture 'fast' 2
    $file = Join-Path $out 'jobs.json'
    @($slow,$fast) | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding utf8
    $first = & $waiter -JobsFile $file | ConvertFrom-Json
    if ($first.completed.task -ne 'fast' -or $first.active.task -ne 'slow') { throw 'Did not return the first finished worker' }
    $new = Start-Fixture 'new-after-first' 4
    @($slow,$new) | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding utf8
    $second = & $waiter -JobsFile $file | ConvertFrom-Json
    if ($second.completed.task -ne 'new-after-first' -or $second.active.task -ne 'slow') { throw 'New worker could not wake the resumed wait' }
    $wrong = [pscustomobject]@{task='wrong-start'; processId=$slow.processId; startedUtc=([DateTimeOffset]::Parse($slow.startedUtc).AddMinutes(-1).ToString('o')); execSessionId=$slow.execSessionId}
    @($wrong) | ConvertTo-Json | Set-Content -LiteralPath $file -Encoding utf8
    $refused = $false
    try { $null = & $waiter -JobsFile $file } catch { $refused = $_.Exception.Message -like '*PID was reused*' }
    if (-not $refused) { throw 'Reused PID was not refused' }
    [pscustomobject]@{result='PASS'; firstCompleted=$first.completed.task; secondCompleted=$second.completed.task; slowStillActive=$second.active.task; pidReuseRefused=$refused; fixture=$out} | ConvertTo-Json -Compress
} finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill() }
        $child.WaitForExit()
        $child.Dispose()
    }
}
