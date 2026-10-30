param(
    [string]$JobsFile,
    [switch]$Stamp,
    [string]$TaskName
)
# The orchestrator records this stamp before a foreground acpx command, adds its exec session ID,
# and supplies ALL unfinished workers in JobsFile. Waits on OS exit events, never agent logs.
$ErrorActionPreference = 'Stop'
if ($Stamp) {
    if (-not $TaskName) { throw 'TaskName is required for a launch stamp' }
    $current = [Diagnostics.Process]::GetCurrentProcess()
    [pscustomobject]@{task=$TaskName; processId=$PID; startedUtc=$current.StartTime.ToUniversalTime().ToString('o')} | ConvertTo-Json -Compress
    return
}
if (-not $JobsFile) { throw 'JobsFile is required' }
$jobs = @(Get-Content -LiteralPath $JobsFile -Raw | ConvertFrom-Json)
if ($jobs.Count -eq 0 -or @($jobs.task | Select-Object -Unique).Count -ne $jobs.Count) {
    throw 'Expected nonempty jobs with unique task names'
}
$watched = [Collections.Generic.List[object]]::new()
$completed = [Collections.Generic.List[object]]::new()
try {
    foreach ($job in $jobs) {
        if (-not $job.task -or [int]$job.processId -le 0 -or [int]$job.execSessionId -le 0 -or -not $job.startedUtc) {
            throw 'Each job needs task, processId, startedUtc and execSessionId'
        }
        if ([int]$job.processId -eq $PID) { throw 'Refusing to wait for this waiter itself' }
        try { $worker = [Diagnostics.Process]::GetProcessById([int]$job.processId) }
        catch [ArgumentException] { $completed.Add($job); continue }
        try {
            if ($worker.HasExited) { $completed.Add($job); $worker.Dispose(); continue }
            if ($job.startedUtc -is [DateTime]) { $expected = $job.startedUtc.ToUniversalTime().Ticks }
            elseif ($job.startedUtc -is [DateTimeOffset]) { $expected = $job.startedUtc.UtcDateTime.Ticks }
            else { $expected = [DateTimeOffset]::Parse([string]$job.startedUtc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime.Ticks }
            if ($worker.StartTime.ToUniversalTime().Ticks -ne $expected) { throw "PID was reused for $($job.task)" }
            $watched.Add([pscustomobject]@{job=$job; process=$worker; exitTask=$worker.WaitForExitAsync()})
        } catch { $worker.Dispose(); throw }
    }
    if ($completed.Count -eq 0) {
        $tasks = [Threading.Tasks.Task[]]@($watched | ForEach-Object { $_.exitTask })
        $null = [Threading.Tasks.Task]::WhenAny($tasks).GetAwaiter().GetResult()
    }
    $active = [Collections.Generic.List[object]]::new()
    foreach ($item in $watched) {
        if ($item.process.HasExited) { $completed.Add($item.job) }
        else { $active.Add($item.job) }
    }
    [pscustomobject]@{event='foreground_processes_completed'; completed=@($completed); active=@($active)} |
        ConvertTo-Json -Depth 6 -Compress
} finally {
    # Disposing wait handles does not stop the workers. Their foreground exec output remains unread
    # until the orchestrator retrieves only the completed sessions. The next wait can include new jobs.
    foreach ($item in $watched) { $item.process.Dispose() }
}
