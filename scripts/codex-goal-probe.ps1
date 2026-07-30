#requires -Version 5.1
# Rhino-free probe of Codex's native thread/goal (thread/goal/set): does this Codex build accept the
# method, and what does it do with a goal? Drives the app-server directly over stdio JSON-RPC — no
# GPTino, no Rhino, no plugin install, so it never conflicts with an open Rhino. Diagnostic only.
#
# Robustness notes for Windows PowerShell 5.1:
#  - No ProcessStartInfo.ArgumentList -> single Arguments string.
#  - The StandardInput TextWriter mangles bytes -> write UTF-8 directly to BaseStream.
#  - StreamReader has no overlapping async reads -> a background runspace drains stdout to a list.
[CmdletBinding()]
param(
    [string]$Exe = "$env:USERPROFILE\.codex\.sandbox-bin\codex.exe",
    [int]$TurnObserveSeconds = 90
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "codex not found: $Exe" }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$flags = @('features.plugins=false','features.apps=false','features.remote_plugin=false',
    'features.enable_mcp_apps=false','features.plugin_sharing=false','mcp_servers.blender.enabled=false')
$psi.Arguments = (($flags | ForEach-Object { "-c $_" }) -join ' ') + ' app-server --stdio'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$proc = [Diagnostics.Process]::Start($psi)

# Background reader: continuously drain stdout lines into a synchronized list (no overlap issues).
$lines = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
$reader = [PowerShell]::Create()
$reader.Runspace.SessionStateProxy.SetVariable('out', $proc.StandardOutput)
$reader.Runspace.SessionStateProxy.SetVariable('lines', $lines)
[void]$reader.AddScript({ try { while (($l = $out.ReadLine()) -ne $null) { [void]$lines.Add($l) } } catch {} })
$readerHandle = $reader.BeginInvoke()

$utf8 = New-Object System.Text.UTF8Encoding($false)
function Send-Msg($obj) {
    $json = $obj | ConvertTo-Json -Depth 25 -Compress
    $bytes = $utf8.GetBytes($json + "`n")
    $proc.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $proc.StandardInput.BaseStream.Flush()
}
function Find-Response($wantId) {
    foreach ($line in @($lines.ToArray())) {
        try { $m = $line | ConvertFrom-Json } catch { continue }
        if (($m.PSObject.Properties.Name -contains 'id') -and ($m.id -eq $wantId)) { return $m }
    }
    return $null
}
function Wait-Response($wantId, $timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $r = Find-Response $wantId
        if ($r) { return $r }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

try {
    Start-Sleep -Seconds 2  # let the app-server's stdin reader spin up
    # Warm up: the very first BaseStream write is corrupted (codex reports a deserialize error at
    # line 1 col 1 only for message #1 while #2 parses). Absorb that with a throwaway newline so the
    # real initialize is a clean subsequent write.
    $nl = $utf8.GetBytes("`n")
    $proc.StandardInput.BaseStream.Write($nl, 0, $nl.Length); $proc.StandardInput.BaseStream.Flush()
    Start-Sleep -Milliseconds 300
    "=== initialize ==="
    Send-Msg (@{ id = 1; method = 'initialize'; 'params' = @{ clientInfo = @{ name = 'gptino-goal-probe'; title = 'probe'; version = '0.1' }; capabilities = @{ experimentalApi = $true } } })
    $r = Wait-Response 1 30
    "initialize: " + $(if ($r) { if ($r.error) { 'ERROR ' + ($r.error | ConvertTo-Json -Compress) } else { 'ok' } } else { 'TIMEOUT' })
    Send-Msg (@{ method = 'initialized'; 'params' = @{} })

    "=== thread/start ==="
    $cwd = (Resolve-Path "$env:TEMP").Path
    Send-Msg (@{ id = 2; method = 'thread/start'; 'params' = @{ cwd = $cwd; approvalPolicy = 'never'; sandbox = 'read-only'; baseInstructions = 'You are a concise test assistant.'; personality = 'pragmatic' } })
    $r = Wait-Response 2 30
    if (-not $r -or $r.error) { throw 'thread/start failed: ' + $(if ($r) { $r.error | ConvertTo-Json -Compress } else { 'timeout' }) }
    $threadId = $r.result.thread.id
    "threadId = $threadId"

    "=== thread/goal/set  (THE probe) ==="
    Send-Msg (@{ id = 3; method = 'thread/goal/set'; 'params' = @{ threadId = $threadId; objective = 'Produce a clean paneling plan for a NURBS surface; done when the plan lists grid + openings + solids.'; tokenBudget = 50000 } })
    $r = Wait-Response 3 30
    if (-not $r) { "goal/set: TIMEOUT (no response)" }
    elseif ($r.error) { "goal/set: ERROR code=$($r.error.code) msg=$($r.error.message)" }
    else { "goal/set: OK -> " + ($r.result | ConvertTo-Json -Depth 6 -Compress) }

    "=== turn/start (observe goal effect ${TurnObserveSeconds}s) ==="
    Send-Msg (@{ id = 4; method = 'turn/start'; 'params' = @{ threadId = $threadId; approvalPolicy = 'never'; input = @(@{ type = 'text'; text = 'In 2-3 sentences, outline how you would panelize a NURBS surface.' }) } })
    $r = Wait-Response 4 30
    "turn/start: " + $(if ($r) { if ($r.error) { "ERROR $($r.error.message)" } else { 'started turn ' + $r.result.turn.id } } else { 'TIMEOUT' })
    Start-Sleep -Seconds $TurnObserveSeconds

    "=== notification method tally ==="
    $notifs = foreach ($line in @($lines.ToArray())) {
        try { $m = $line | ConvertFrom-Json } catch { continue }
        if (($m.PSObject.Properties.Name -contains 'method') -and -not ($m.PSObject.Properties.Name -contains 'id')) { $m.method }
    }
    $notifs | Group-Object | Sort-Object Count -Descending | ForEach-Object { '{0,4}  {1}' -f $_.Count, $_.Name }
    "=== goal-related notifications? ==="
    $goalN = $notifs | Where-Object { $_ -match 'oal' } | Select-Object -Unique
    if ($goalN) { $goalN } else { '(none — Codex emitted no goal notifications)' }
}
finally {
    try { $proc.StandardInput.Close() } catch {}
    Start-Sleep -Milliseconds 500
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch {}
    try { $reader.Stop(); $reader.Dispose() } catch {}
}
