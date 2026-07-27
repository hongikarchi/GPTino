#requires -Version 5.1
# Drives one stress "wave": sends a message to a session, polls the turn to completion,
# and reports final status + elapsed + new git commits + any new failure/crash diagnostics
# recorded during the wave. Reads the newest dev-loop run's loop-state.json.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SessionId,
    [string]$Content,
    [string]$ContentFile,
    [int]$TimeoutSeconds = 300,
    [string]$Run
)
$ErrorActionPreference = 'Stop'
if ($ContentFile) { $Content = Get-Content -LiteralPath $ContentFile -Raw }
if (-not $Content) { throw 'Provide -Content or -ContentFile.' }
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
$state = Get-Content (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-GPTino-Token' = $state.token }
function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 30
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 30
}

# --- baselines ---
$diagDir = $state.runtime
$diagBefore = (Get-ChildItem $diagDir -Filter '.gptino-diagnostic-*.json' -Force | Measure-Object).Count
$rt = Api GET '/runtime'
$docId = $rt.grasshopperDocs[0].id
$hist = Join-Path $state.runtime "histories\$docId"
$revBefore = if (Test-Path $hist) { (git -C $hist rev-list --count HEAD 2>$null) } else { 0 }
$t0 = Get-Date

# --- send + poll ---
Api POST "/sessions/$SessionId/messages" @{ Content = $Content; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
$deadline = $t0.AddSeconds($TimeoutSeconds)
$status = 'working'
do {
    Start-Sleep -Seconds 6
    $rt = Api GET '/runtime'
    $s = $rt.sessions | Where-Object { $_.id -eq $SessionId }
    $status = if ($s) { $s.status } else { 'gone' }
} while ($status -eq 'working' -and (Get-Date) -lt $deadline)
$elapsed = [int]((Get-Date) - $t0).TotalSeconds

# --- deltas ---
$revAfter = if (Test-Path $hist) { (git -C $hist rev-list --count HEAD 2>$null) } else { 0 }
$diagAfter = Get-ChildItem $diagDir -Filter '.gptino-diagnostic-*.json' -Force | Sort-Object Name
$newDiag = $diagAfter | Select-Object -Skip $diagBefore
$fails = @()
foreach ($d in $newDiag) {
    $j = Get-Content $d.FullName -Raw | ConvertFrom-Json
    if ($j.Event -match 'fail|crash|recover|exit|unhandled|fault') {
        $fails += "[$($j.Event)] $($j.Detail.Substring(0, [Math]::Min(220, $j.Detail.Length)))"
    }
}

[pscustomobject]@{
    status       = $status
    elapsedSec   = $elapsed
    commits      = "$revBefore -> $revAfter"
    conflicts    = $rt.conflicts.Count
    queue        = $rt.queue.Count
    newDiagTotal = $newDiag.Count
    failCount    = $fails.Count
} | Format-List
if ($fails.Count -gt 0) {
    "--- failure/crash diagnostics this wave ---"
    $fails | ForEach-Object { $_ }
}
