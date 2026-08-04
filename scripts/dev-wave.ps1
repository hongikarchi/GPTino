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
    [string]$Run,
    # Optional numeric-assertion spec (harness-side grading; the agent under test never
    # sees or writes these). JSON: { "asserts": [ { "component": <name regex>,
    # "output": <socket name>, "kind": "count"|"number"|"json", "path": <dot/[i] path,
    # json kind only>, "min": <num>, "max": <num> } ] }. Values are read live via the
    # dev endpoints (/dev/snapshot for name->objectId, /dev/grasshopper/{id}/outputs
    # for sampleValues/dataCount). Asserts only run when the session ended idle.
    [string]$ExpectFile
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

# --- numeric assertions (harness-side, optional) ---
$assertResults = @()
$assertsFailed = 0
if ($ExpectFile) {
    $spec = Get-Content -LiteralPath $ExpectFile -Raw | ConvertFrom-Json
    if ($status -ne 'idle') {
        $assertsFailed = $spec.asserts.Count
        $assertResults += "SKIP-ALL: session ended '$status', not idle - all $($spec.asserts.Count) asserts count as failed"
    }
    else {
        function Get-JsonPath($obj, $path) {
            $cur = $obj
            foreach ($seg in ($path -split '\.')) {
                if ($seg -match '^(.*?)\[(\d+)\]$') {
                    if ($Matches[1]) { $cur = $cur.($Matches[1]) }
                    $cur = $cur[[int]$Matches[2]]
                }
                else { $cur = $cur.$seg }
                if ($null -eq $cur) { return $null }
            }
            return $cur
        }
        $snap = Api GET '/dev/snapshot'
        foreach ($a in $spec.asserts) {
            $label = "$($a.component) / $($a.output) [$($a.kind)]"
            $hits = @($snap.canvas.objects | Where-Object { $_.name -match $a.component })
            if ($hits.Count -ne 1) {
                $assertsFailed++
                $assertResults += "FAIL $label - component regex matched $($hits.Count) objects (need exactly 1)"
                continue
            }
            $outs = (Api GET "/dev/grasshopper/$($hits[0].objectId)/outputs").result.outputs
            $sock = $outs | Where-Object { $_.name -eq $a.output } | Select-Object -First 1
            if (-not $sock) {
                $assertsFailed++
                $assertResults += "FAIL $label - output socket not found (have: $(($outs | ForEach-Object { $_.name }) -join ', '))"
                continue
            }
            $value = $null
            switch ($a.kind) {
                'count' { $value = $sock.dataCount }
                'number' { if ($sock.sampleValues.Count -gt 0) { $value = [double]$sock.sampleValues[0] } }
                'json' {
                    if ($sock.sampleValues.Count -gt 0) {
                        try { $value = Get-JsonPath ($sock.sampleValues[0] | ConvertFrom-Json) $a.path } catch { $value = $null }
                    }
                }
            }
            if ($null -eq $value) {
                $assertsFailed++
                $assertResults += "FAIL $label - no value (dataCount=$($sock.dataCount), samples=$($sock.sampleValues.Count))"
            }
            elseif ([double]$value -lt [double]$a.min -or [double]$value -gt [double]$a.max) {
                $assertsFailed++
                $assertResults += "FAIL $label - value $value outside [$($a.min), $($a.max)]"
            }
            else {
                $assertResults += "PASS $label - $value in [$($a.min), $($a.max)]"
            }
        }
    }
}

$gate = ($status -eq 'idle') -and ($fails.Count -eq 0) -and ($assertsFailed -eq 0)
[pscustomobject]@{
    status        = $status
    elapsedSec    = $elapsed
    commits       = "$revBefore -> $revAfter"
    conflicts     = $rt.conflicts.Count
    queue         = $rt.queue.Count
    newDiagTotal  = $newDiag.Count
    failCount     = $fails.Count
    assertsFailed = $assertsFailed
    gate          = if ($gate) { 'PASS' } else { 'FAIL' }
} | Format-List
if ($assertResults.Count -gt 0) {
    "--- numeric asserts ---"
    $assertResults | ForEach-Object { $_ }
}
if ($fails.Count -gt 0) {
    "--- failure/crash diagnostics this wave ---"
    $fails | ForEach-Object { $_ }
}
