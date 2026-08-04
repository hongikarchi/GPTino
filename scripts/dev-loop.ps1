#requires -Version 5.1
# GPTino autonomous dev-loop launcher.
#
# Boots the installed GPTino plugin in a marked development run directory so the
# AgentHost writes a loopback endpoint that can be driven head-lessly over HTTP —
# no manual "Open Grasshopper" / panel clicking. Grasshopper is opened by chaining
# Rhino's /runscript (the dev-loop artifact tree has no spaces, so no SendKeys /
# quoting workarounds are needed).
#
# Prereq: Rhino fully closed, and the current package installed into
#   %APPDATA%\McNeel\Rhinoceros\packages\8.0\GPTino\<version>.
#
# Output: writes <run>\loop-state.json { uiBaseUrl, token, run, scene3dm, sceneGh }
# and prints the same, so a driver can create sessions and post messages.

[CmdletBinding()]
param(
    [string]$RunId = (Get-Date -Format 'yyyyMMddTHHmmssZ') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8)),
    # Scene fixture kind: 'paneling' (default, original fixture) or 'structural'
    # (column axes + perimeter beams + isolated test beam for Karamba benchmarks).
    [ValidateSet('paneling', 'structural')]
    [string]$SceneKind = 'paneling',
    [switch]$RegenerateScene,
    # Launch WITHOUT opening Grasshopper, to exercise the Rhino-only target (curator-only panel).
    [switch]$NoGrasshopper,
    [int]$ReadyTimeoutSeconds = 120,
    [string]$RhinoExe = 'C:\Program Files\Rhino 8\System\Rhino.exe'
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

if (-not (Test-Path -LiteralPath $RhinoExe)) { throw "Rhino not found: $RhinoExe" }
if (Get-Process -Name Rhino -ErrorAction SilentlyContinue) {
    throw 'Rhino is running. Close it completely before starting a dev-loop run.'
}

# --- marked run directory (must satisfy DevelopmentDataDirectoryPolicy) ---------
$runRoot = Join-Path $repo "artifacts\dev-loop\$RunId"
$runtime = Join-Path $runRoot 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Set-Content -LiteralPath (Join-Path $runRoot '.gptino-owned-run') -Value "GPTino dev loop`n" -Encoding utf8

# --- 256-bit hex API token -----------------------------------------------------
$bytes = New-Object 'System.Byte[]' 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = -join ($bytes | ForEach-Object { $_.ToString('x2') })

# --- scene assets --------------------------------------------------------------
# Non-paneling kinds get a kind-suffixed filename so a reused run directory can never
# serve a stale scene of the wrong kind.
$sceneName = if ($SceneKind -eq 'paneling') { 'scene.3dm' } else { "scene-$SceneKind.3dm" }
$scene3dm = Join-Path $runRoot $sceneName
$sceneGh = Join-Path $runRoot 'scene.gh'

# Empty saved Grasshopper doc (canonical 1631-byte template reused from a prior run).
$emptyGh = Get-ChildItem (Join-Path $repo 'artifacts\dev-loop') -Recurse -Filter 'bench.gh' -File |
    Where-Object { $_.Length -eq 1631 } | Select-Object -First 1
if (-not $emptyGh) { throw 'No empty (1631-byte) bench.gh template found to seed scene.gh.' }
Copy-Item -LiteralPath $emptyGh.FullName -Destination $sceneGh -Force

# Paneling geometry: generate scene.3dm via a synchronous RhinoPython pass.
if ($RegenerateScene -or -not (Test-Path -LiteralPath $scene3dm)) {
    Remove-Item -LiteralPath $scene3dm, "$scene3dm.scene-ok" -Force -ErrorAction SilentlyContinue
    $genPsi = New-Object System.Diagnostics.ProcessStartInfo
    $genPsi.FileName = $RhinoExe
    $genScript = Join-Path $repo 'scripts\dev-scene.py'
    $genPsi.Arguments = "/nosplash /runscript=`"_-RunPythonScript $genScript _Exit`""
    $genPsi.UseShellExecute = $false
    $genPsi.EnvironmentVariables['GPTINO_SCENE_3DM'] = $scene3dm
    $genPsi.EnvironmentVariables['GPTINO_SCENE_KIND'] = $SceneKind
    Write-Host "Generating $SceneKind scene -> $scene3dm"
    $gen = [System.Diagnostics.Process]::Start($genPsi)
    # The python writes a '.scene-ok' marker when it has saved the .3dm. Poll for it
    # rather than trusting Rhino's _Exit (a stray dialog can leave the GUI running);
    # once the marker lands the scene is complete and the throwaway Rhino is killed.
    $marker = "$scene3dm.scene-ok"
    $genDeadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $genDeadline -and -not (Test-Path -LiteralPath $marker)) {
        if ($gen.HasExited) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $gen.HasExited) { try { $gen.Kill() } catch { } }
    if (-not (Test-Path -LiteralPath $marker)) {
        throw "Scene generation did not produce $scene3dm (.scene-ok marker missing)."
    }
    Start-Sleep -Seconds 2  # let the Rhino handle on scene.3dm release before the live open
}

# A reused run directory still holds the PREVIOUS run's endpoint.json; polling would report that
# stale (dead) endpoint as ready before the new AgentHost overwrites it. Clear it first.
Remove-Item -LiteralPath (Join-Path $runtime 'endpoint.json') -Force -ErrorAction SilentlyContinue

# A killed Rhino leaves its instance lock behind, and the next Rhino defers AgentHost startup to
# the "already running" instance that no longer exists — the endpoint then never appears. Clear
# the lock only when its pid is genuinely gone, so a real second instance still wins the race.
$lockPath = Join-Path $runtime '.gptino-instance.lock'
if (Test-Path -LiteralPath $lockPath) {
    $lockPid = ((Get-Content -LiteralPath $lockPath | Where-Object { $_ -match '^pid=' }) -replace 'pid=', '').Trim()
    if (-not $lockPid -or -not (Get-Process -Id $lockPid -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
        Write-Host "Cleared a stale instance lock (pid $lockPid is gone)."
    }
}

# --- live launch: open scene, panel, and Grasshopper doc via runscript ----------
# Order: open the panel (starts the AgentHost) then open the saved .gh (forms the
# rhino+gh target). Paths carry no spaces, so the .gh path is passed unquoted.
# -NoGrasshopper leaves Grasshopper closed: the Rhino-only target must bring the panel
# up on its own, which is the curator's whole premise and cannot be gated any other way.
$runscript = if ($NoGrasshopper) {
    '_GPTinoOpenPanel _Enter'
}
else {
    "_GPTinoOpenPanel _Enter -_Grasshopper _Document _Open $sceneGh _Enter"
}
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $RhinoExe
$psi.Arguments = "/nosplash `"$scene3dm`" /runscript=`"$runscript`""
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['GPTINO_DEV_MODE'] = '1'
$psi.EnvironmentVariables['GPTINO_DEV_DATA_DIRECTORY'] = $runtime
$psi.EnvironmentVariables['GPTINO_API_TOKEN'] = $token
Write-Host "Launching Rhino (dev-mode) ..."
$rhino = [System.Diagnostics.Process]::Start($psi)

# --- wait for the AgentHost endpoint -------------------------------------------
$endpointPath = Join-Path $runtime 'endpoint.json'
$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$uiBaseUrl = $null
while ((Get-Date) -lt $deadline) {
    if ($rhino.HasExited) { throw "Rhino exited early (code $($rhino.ExitCode)) before the endpoint appeared." }
    if (Test-Path -LiteralPath $endpointPath) {
        try {
            $info = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
            if ($info.uiBaseUrl) { $uiBaseUrl = $info.uiBaseUrl; break }
        }
        catch { Start-Sleep -Milliseconds 300 }
    }
    Start-Sleep -Milliseconds 500
}
if (-not $uiBaseUrl) { throw "AgentHost endpoint did not appear within $ReadyTimeoutSeconds s." }

$state = [ordered]@{
    uiBaseUrl = $uiBaseUrl
    token     = $token
    run       = $runRoot
    runtime   = $runtime
    scene3dm  = $scene3dm
    sceneGh   = $sceneGh
    rhinoPid  = $rhino.Id
}
$stateJson = $state | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $runRoot 'loop-state.json') -Value $stateJson -Encoding utf8
Write-Host '--- LOOP STATE ---'
Write-Output $stateJson
