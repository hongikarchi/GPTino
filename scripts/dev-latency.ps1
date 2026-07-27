#requires -Version 5.1
# Aggregates authoring-latency.jsonl from the newest dev-loop run into a turn-time
# decomposition: model-inference (gaps between tool calls) vs GPTino tool-handling
# (call durations, broken down by tool). Optionally window by -SinceMinutes or -Thread.
[CmdletBinding()]
param(
    [string]$Run,
    [double]$SinceMinutes,
    [string]$Thread
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
$state = Get-Content (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
$jsonl = Join-Path $state.runtime 'authoring-latency.jsonl'
if (-not (Test-Path $jsonl)) { throw "No authoring-latency.jsonl in $($state.runtime) (rebuild+reinstall with the trace, then run a turn)." }

$calls = Get-Content $jsonl | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.kind -eq 'tool' }
if ($Thread) { $calls = $calls | Where-Object { $_.thread -eq $Thread } }
$calls = $calls | ForEach-Object {
    $end = [datetimeoffset]$_.t
    [pscustomobject]@{ tool = $_.tool; ms = [long]$_.ms; ok = $_.ok; end = $end; start = $end.AddMilliseconds(-[long]$_.ms) }
} | Sort-Object start
if ($SinceMinutes) {
    $cut = (Get-Date).ToUniversalTime().AddMinutes(-$SinceMinutes)
    $calls = $calls | Where-Object { $_.start.UtcDateTime -ge $cut }
}
if (-not $calls) { throw 'No tool calls in the selected window.' }

$first = $calls[0].start; $last = ($calls | Select-Object -Last 1).end
$wall = ($last - $first).TotalSeconds
$handling = ($calls | Measure-Object -Property ms -Sum).Sum / 1000.0

# Inter-call gaps (model inference + network): positive time between one call ending and the next starting.
$gap = 0.0
for ($i = 1; $i -lt $calls.Count; $i++) {
    $g = ($calls[$i].start - $calls[$i - 1].end).TotalSeconds
    if ($g -gt 0) { $gap += $g }
}

"=== turn latency decomposition ($([int]$calls.Count) tool calls) ==="
"{0,-24}{1,10:N1}s" -f 'wall-clock', $wall
"{0,-24}{1,10:N1}s  ({2:P0})" -f 'model-inference (gaps)', $gap, ($(if ($wall) { $gap / $wall } else { 0 }))
"{0,-24}{1,10:N1}s  ({2:P0})" -f 'GPTino tool-handling', $handling, ($(if ($wall) { $handling / $wall } else { 0 }))
""
"=== by tool (handling) ==="
$calls | Group-Object tool | ForEach-Object {
    $sum = ($_.Group | Measure-Object ms -Sum).Sum
    [pscustomobject]@{
        tool     = $_.Name
        count    = $_.Count
        total_s  = [math]::Round($sum / 1000.0, 1)
        avg_ms   = [int]($sum / $_.Count)
        fails    = ($_.Group | Where-Object { -not $_.ok }).Count
    }
} | Sort-Object total_s -Descending | Format-Table -Auto
