#requires -Version 5.1
# Live gate for the structural pipeline's ask-before-you-solve path, run from an ordinary session:
#
#   structural_extract -> free-end ask-back (focus chips) -> answers -> structural_solve -> verify
#
# Needs a dev-loop run booted with -SceneKind structural-solids: that fixture plants unit-prototype
# block instances (KS nominal x 1.02 dims), a loose PCA brace, a mesh distractor, and exactly one
# DELIBERATE elevated free end (the cantilever ask-back). A fixture with nothing ambiguous would
# let this gate report PASS without exercising the ask-first discipline it exists to prove.
#
# Verification never trusts the agent's prose: /dev/structural-extract grades the extraction with
# no model in the loop BEFORE any conversation, and the solve is graded from the results artifact
# (confirmedCantilever must carry the user's answer; reactions must balance the applied load).
#
# NOTE: this file must stay UTF-8 WITH BOM — PS 5.1 reads a BOM-less .ps1 in the ANSI codepage and
# turns the Korean prompts into mojibake the agent then answers instead of the real question.
[CmdletBinding()]
param(
    [string]$Run,
    [int]$TimeoutSeconds = 480
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Run) {
    $Run = (Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'loop-state.json') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
$state = Get-Content (Join-Path $Run 'loop-state.json') -Raw | ConvertFrom-Json
if ($state.scene3dm -notmatch 'structural-solids') {
    throw "This gate needs a -SceneKind structural-solids run; got $($state.scene3dm)"
}
$base = $state.uiBaseUrl.TrimEnd('/') + '/api/v1'
$headers = @{ 'X-GPTino-Token' = $state.token }
function Api($method, $path, $body) {
    $uri = $base + $path
    if ($null -ne $body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
        return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
    }
    return Invoke-RestMethod -Method $method -Uri $uri -Headers $headers -TimeoutSec 60
}
function Send-Turn($sessionId, $text, $seconds) {
    Api POST "/sessions/$sessionId/messages" @{ Content = $text; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 5
        $s = (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
        $status = if ($s) { $s.status } else { 'gone' }
    } while ($status -eq 'working' -and (Get-Date) -lt $deadline)
    return $status
}
function Read-Artifact($sessionId, $relative) {
    $sidn = ([guid]$sessionId).ToString('N')
    $path = Join-Path $Run "runtime\artifacts\$sidn\$relative"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

$results = [ordered]@{}

# --- 0. grade the extraction server-side, before any model sees the document ----
$probe = (Api GET '/dev/structural-extract').result
$results['0-members'] = $probe.members.Count
$results['0-free-ends'] = $probe.freeEnds.Count
$results['0-prototypes'] = $probe.prototypes.Count
$meshSkipped = 0
foreach ($p in $probe.skippedByReason.PSObject.Properties) {
    if ($p.Name -match 'Mesh') { $meshSkipped += $p.Value }
}
$results['0-mesh-skipped'] = $meshSkipped
if ($probe.members.Count -ne 10 -or $probe.freeEnds.Count -ne 3 -or $probe.prototypes.Count -lt 2) {
    throw "Fixture mismatch: expected 10 members / 3 free ends / 2 prototypes, got " +
        "$($probe.members.Count) / $($probe.freeEnds.Count) / $($probe.prototypes.Count). " +
        "A gate graded against the wrong fixture proves nothing."
}
# The deliberate cantilever tip is the elevated free end (z=3000); the other two are column bases.
$tip = $probe.freeEnds | Where-Object { $_.point.z -gt 100 } | Select-Object -First 1
if (-not $tip) { throw 'The fixture lost its deliberate elevated free end.' }
$tipPoint = @($tip.point.x, $tip.point.y, $tip.point.z)

# --- 1. ask for the check; the agent must extract and STOP to ask ---------------
$sessionId = (Api POST '/sessions' @{ Name = 'structural-gate'; ModelProfile = 'xhigh' }).id
$results['1-extract-turn'] = Send-Turn $sessionId `
    '이 라이노 문서의 철골 구조를 점검해줘. 부재 추출부터 하고, 애매한 부분이 있으면 해석 전에 나한테 물어봐.' `
    $TimeoutSeconds
$results['1-members-artifact'] = ($null -ne (Read-Artifact $sessionId 'structural\members.json'))
$results['1-no-premature-solve'] = ($null -eq (Read-Artifact $sessionId 'structural\results.json'))
$lastReply = ((Api GET "/sessions/$sessionId/messages") | Where-Object { $_.role -eq 'assistant' } | Select-Object -Last 1).content
$results['1-asked-with-focus-chips'] = ($lastReply -match '\[\[focus:[0-9a-fA-F-]{36}')

# --- 2. answer the ask-backs; the agent must solve with them --------------------
$results['2-solve-turn'] = Send-Turn $sessionId `
    ('끝 안 닿은 보는 의도된 캔틸레버 맞아. 기둥 밑동들은 기초 지점으로 보면 돼. 하중은 자중만으로 해석해줘.') `
    $TimeoutSeconds
$report = Read-Artifact $sessionId 'structural\results.json'
if (-not $report) { throw 'No results artifact — structural_solve never ran.' }
$results['2-equilibrium-ok'] = ([math]::Abs($report.equilibriumErrorPercent) -lt 0.5)
$results['2-supports'] = $report.supports
$results['2-no-unapproved-repair'] = ($report.repairedFreeEnds -eq 0)
# The user's cantilever answer must reach the solver: the tip survives as a CONFIRMED free end.
$confirmedTip = $report.freeEndsRemaining | Where-Object {
    $_.confirmedCantilever -and
    ([math]::Abs($_.xyzMm[0] - $tipPoint[0]) -lt 400) -and
    ([math]::Abs($_.xyzMm[1] - $tipPoint[1]) -lt 400) -and
    ([math]::Abs($_.xyzMm[2] - $tipPoint[2]) -lt 400)
}
$results['2-cantilever-answer-threaded'] = (@($confirmedTip).Count -ge 1)
$results['2-checks'] = "$($report.memberChecks.passed)/$($report.memberChecks.checked) passed"
$finalReply = ((Api GET "/sessions/$sessionId/messages") | Where-Object { $_.role -eq 'assistant' } | Select-Object -Last 1).content
$results['2-verdict-points-at-objects'] = ($finalReply -match '\[\[focus:[0-9a-fA-F-]{36}')

$pass = $results['1-extract-turn'] -eq 'idle' -and
        $results['2-solve-turn'] -eq 'idle' -and
        $results['1-members-artifact'] -and
        $results['1-no-premature-solve'] -and
        $results['1-asked-with-focus-chips'] -and
        $results['2-equilibrium-ok'] -and
        $results['2-no-unapproved-repair'] -and
        $results['2-cantilever-answer-threaded'] -and
        $results['2-verdict-points-at-objects']
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $sessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-structural.json') -Encoding utf8
if (-not $pass) { exit 1 }
