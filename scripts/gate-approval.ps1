#requires -Version 5.1
# Live gate for the full ask-before-you-act path, run from an ORDINARY session:
#
#   audit -> goal card -> user confirms -> approval card -> grant -> fix -> verify
#
# The curator role, plan/auto mode and the curator tab are gone; this proves the path did not go
# with them, and that the two cards carry the user's answers into the work.
#
# Needs a dev-loop run booted with -SceneKind hygiene: that fixture plants two endpoint gaps
# (0.005 / 0.003 mm) and one near-duplicate pair (0.0005 mm) at a pinned 0.001 mm tolerance, so
# every stage runs on real findings. A fixture with nothing wrong would let this script report
# PASS without ever executing the path it claims to prove.
#
# The gate deliberately grants only TWO of the three findings and picks which duplicate copy
# survives, so it also proves the claims that matter most:
#   - a refused item is left untouched
#   - the user's per-item choice reaches the agent, which must act on it instead of asking again
#
# Verification never trusts the agent's prose: /dev/rhino-objects is diffed before and after AND
# /dev/audit is re-run server-side. Both are needed. The object list alone was not enough — the
# first run of this gate had rhino_list still reporting an object the broker had deleted and
# verified gone, so "still listed" and "still there" are not the same claim.
#
# NOTE: this file must stay UTF-8 WITH BOM. Windows PowerShell 5.1 reads a BOM-less .ps1 as the
# system ANSI codepage, which turns every Korean prompt below into mojibake -- and the agent then
# answers a question nobody asked.
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
if ($state.scene3dm -notmatch 'hygiene') {
    throw "This gate needs a -SceneKind hygiene run; got $($state.scene3dm)"
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
function Get-Session($sessionId) {
    return (Api GET '/runtime').sessions | Where-Object { $_.id -eq $sessionId }
}
function Send-Turn($sessionId, $text, $seconds) {
    Api POST "/sessions/$sessionId/messages" @{ Content = $text; ClientMessageId = [guid]::NewGuid().ToString() } | Out-Null
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        Start-Sleep -Seconds 5
        $s = Get-Session $sessionId
        $status = if ($s) { $s.status } else { 'gone' }
    } while ($status -eq 'working' -and (Get-Date) -lt $deadline)
    return $status
}

$results = [ordered]@{}
$before = (Api GET '/dev/rhino-objects').result.objects
$beforeMap = @{}; foreach ($o in $before) { $beforeMap[$o.objectId] = $o.fingerprint }
$results['0-objects-before'] = $before.Count
$dupBefore = (Api GET '/dev/audit?kind=nearDuplicates').result
$gapBefore = (Api GET '/dev/audit?kind=nearMissEndpoints').result
$results['0-audit-duplicates-before'] = $dupBefore.findings.Count
$results['0-audit-gaps-before'] = $gapBefore.findings.Count
if ($dupBefore.findings.Count -lt 1 -or $gapBefore.findings.Count -lt 2) {
    throw "The hygiene fixture reported $($dupBefore.findings.Count) duplicate(s) and $($gapBefore.findings.Count) gap(s); expected 1 and 2. A gate that runs on an empty finding set proves nothing."
}

# --- 1. audit -------------------------------------------------------------------
$sessionId = (Api POST '/sessions' @{ Name = 'hygiene-gate'; ModelProfile = 'xhigh' }).id
$results['1-audit-turn'] = Send-Turn $sessionId `
    '이 Rhino 문서를 점검해줘. 끝점이 안 맞는 곳(near-miss endpoints)과 근접 중복(near-duplicates)을 확인하고 측정값과 함께 보고해줘.' `
    $TimeoutSeconds

# --- 2. ask for the fix ---------------------------------------------------------
$results['2-fix-request-turn'] = Send-Turn $sessionId `
    '발견한 3건을 전부 고쳐줘. 근접 중복은 어느 사본을 남길지 내가 직접 고르겠다.' `
    $TimeoutSeconds

# --- 3. confirm the goal card, if the agent framed one first --------------------
# "Frame before you build" fires for destructive work, so the goal card is the expected first
# answer here -- but the agent may also go straight to the approval card. Both are legitimate.
$session = Get-Session $sessionId
$goal = if ($session.goalCard) { $session.goalCard | ConvertFrom-Json } else { $null }
if ($goal -and $goal.status -eq 'proposing') {
    $results['3-goal-card'] = 'proposing'
    # Pick the option that keeps all three findings in play; the narrowing happens at the
    # approval card, which is where per-item refusal is actually modelled.
    # Sort-Object, not Measure-Object: PS 5.1's Measure-Object rejects scriptblock properties.
    $option = if ($goal.options) {
        ($goal.options | Sort-Object { @($_.objectIds).Count } -Descending | Select-Object -First 1).id
    } else { $null }
    $answer = @{ status = 'confirmed' }
    if ($option) { $answer.chosenOption = $option }
    Api PUT "/sessions/$sessionId/goal" $answer | Out-Null
    $results['3-goal-confirmed'] = ((Get-Session $sessionId).goalCard | ConvertFrom-Json).status
    $results['3-continue-turn'] = Send-Turn $sessionId '확인했어. 진행해줘.' $TimeoutSeconds
}
else {
    $results['3-goal-card'] = 'none'
}

# --- 4. the approval card -------------------------------------------------------
$raw = (Get-Session $sessionId).approvalCard
if (-not $raw) { throw 'No approval card was proposed - the agent never called approval_request.' }
$card = $raw | ConvertFrom-Json
$results['4-card-status'] = $card.status
$results['4-card-items'] = $card.items.Count
$dupItem = $card.items | Where-Object { $_.choices } | Select-Object -First 1
if (-not $dupItem) { throw 'No item carried per-item choices; the human-only decision was not surfaced.' }
# Refuse the smallest endpoint gap (the 0.003 mm pair): choice-less items are the endpoint fixes.
$refused = $card.items |
    Where-Object { -not $_.choices } |
    Sort-Object { [double](($_.measure -split '\s+')[0]) } |
    Select-Object -First 1
if (-not $refused) { throw 'Expected at least one choice-less (endpoint) item to refuse.' }
$granted = @($card.items | Where-Object { $_.id -ne $refused.id } | ForEach-Object { $_.id })

# --- 5. grant 2 of 3 and choose which duplicate survives ------------------------
$keepChoice = $dupItem.choices[0]
Api PUT "/sessions/$sessionId/approval" @{
    status          = 'granted'
    approvedItemIds = $granted
    choices         = @{ "$($dupItem.id)" = $keepChoice }
} | Out-Null
$card = (Get-Session $sessionId).approvalCard | ConvertFrom-Json
$results['5-grant-minted'] = [bool]$card.grantId
$results['5-choice-stored'] = [bool]($card.PSObject.Properties.Name -contains 'choices' -and
    $card.choices -and ($card.choices.PSObject.Properties.Name -contains $dupItem.id))

# --- 6. apply -------------------------------------------------------------------
$results['6-apply-turn'] = Send-Turn $sessionId '승인했어. 진행해줘.' $TimeoutSeconds

# --- 7. independent verification -------------------------------------------------
$after = (Api GET '/dev/rhino-objects').result.objects
$afterMap = @{}; foreach ($o in $after) { $afterMap[$o.objectId] = $o.fingerprint }
$results['7-objects-after'] = $after.Count
$dupIds = @($dupItem.targets | ForEach-Object { $_.objectId })
$refusedIds = @($refused.targets | ForEach-Object { $_.objectId })

# The audits are the ground truth for "was it fixed": server-computed, no model in the loop.
$dupAfter = (Api GET '/dev/audit?kind=nearDuplicates').result
$gapAfter = (Api GET '/dev/audit?kind=nearMissEndpoints').result
$results['7-audit-duplicates-after'] = $dupAfter.findings.Count
$results['7-audit-gaps-after'] = $gapAfter.findings.Count
$results['7-approved-duplicate-fixed'] = ($dupAfter.findings.Count -eq $dupBefore.findings.Count - 1)
$results['7-approved-gap-fixed'] = ($gapAfter.findings.Count -eq $gapBefore.findings.Count - 1)
# The refused gap must SURVIVE the fix: still reported, on the same objects, at the same measure.
$refusedStill = @($gapAfter.findings | Where-Object {
        $ids = @($_.objectIds); ($ids | Where-Object { $refusedIds -contains $_ }).Count -eq $ids.Count })
$results['7-refused-finding-survives'] = ($refusedStill.Count -eq 1)
$results['7-refused-untouched'] = -not @($refusedIds | Where-Object {
        (-not $afterMap.ContainsKey($_)) -or ($afterMap[$_] -ne $beforeMap[$_]) }).Count
# Object-list view of the same delete (it must agree with the audit; when it did not, the list
# was listing deleted objects).
$goneDup = @($dupIds | Where-Object { -not $afterMap.ContainsKey($_) })
$results['7-list-agrees-one-removed'] = ($goneDup.Count -eq 1)
# The choice usually names the surviving id; when it does, hold the agent to it.
$named = @($dupIds | Where-Object { $keepChoice -match [regex]::Escape($_.Substring(0, 8)) })
$results['7-kept-copy-matches-choice'] = if ($named.Count -eq 1) {
    $afterMap.ContainsKey($named[0])
} else { 'choice-did-not-name-an-id' }

$pass = $results['1-audit-turn'] -eq 'idle' -and
        $results['6-apply-turn'] -eq 'idle' -and
        $results['4-card-status'] -eq 'proposing' -and
        $results['5-grant-minted'] -and
        $results['5-choice-stored'] -and
        $results['7-approved-duplicate-fixed'] -and
        $results['7-approved-gap-fixed'] -and
        $results['7-refused-finding-survives'] -and
        $results['7-refused-untouched'] -and
        $results['7-list-agrees-one-removed'] -and
        ($results['7-kept-copy-matches-choice'] -ne $false)
$results['GATE'] = if ($pass) { 'PASS' } else { 'FAIL' }
$results['sessionId'] = $sessionId
[pscustomobject]$results | Format-List
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Run 'gate-approval.json') -Encoding utf8
if (-not $pass) { exit 1 }
