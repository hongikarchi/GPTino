[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $AgentHostExecutable,
    [string] $CodexExecutable
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hostPath = if ([string]::IsNullOrWhiteSpace($AgentHostExecutable)) {
    Join-Path $repoRoot "src\GPTino.AgentHost\bin\$Configuration\net8.0\GPTino.AgentHost.exe"
}
else {
    [IO.Path]::GetFullPath($AgentHostExecutable)
}
if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
    throw "AgentHost was not built: $hostPath"
}

if ([string]::IsNullOrWhiteSpace($CodexExecutable)) {
    $codexCommand = Get-Command 'codex.cmd' -ErrorAction Stop
    $CodexExecutable = $codexCommand.Source
}
$CodexExecutable = [IO.Path]::GetFullPath($CodexExecutable)

$smokeRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot (
    'artifacts\smoke-' + [Guid]::NewGuid().ToString('N'))))
if (-not $smokeRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Smoke directory escaped the repository.'
}
[IO.Directory]::CreateDirectory($smokeRoot) | Out-Null

$tokenBytes = New-Object byte[] 32
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($tokenBytes)
}
finally {
    $random.Dispose()
}
$apiToken = ([BitConverter]::ToString($tokenBytes) -replace '-', '')
[Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
$documentSerial = 424242
$process = $null
$duplicateProcess = $null
$clients = New-Object System.Collections.Generic.List[IDisposable]

function New-Request {
    param(
        [Net.Http.HttpMethod] $Method,
        [string] $Uri,
        [hashtable] $Headers = @{},
        [switch] $WithEmptyContent
    )

    $request = [Net.Http.HttpRequestMessage]::new($Method, $Uri)
    foreach ($entry in $Headers.GetEnumerator()) {
        [void] $request.Headers.TryAddWithoutValidation([string] $entry.Key, [string] $entry.Value)
    }
    if ($WithEmptyContent) {
        $request.Content = [Net.Http.StringContent]::new('')
    }
    return $request
}

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $hostPath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['GPTINO_API_TOKEN'] = $apiToken
    $arguments = @(
        '--project-id', [Guid]::NewGuid().ToString('D'),
        '--project-directory', $repoRoot,
        '--data-directory', $smokeRoot,
        '--rhino', (Join-Path $smokeRoot 'smoke.3dm'),
        '--grasshopper', (Join-Path $smokeRoot 'smoke.gh'),
        '--rhino-document-serial', $documentSerial.ToString(),
        '--codex-executable', $CodexExecutable
    )
    $startInfo.Arguments = (($arguments | ForEach-Object {
        '"' + ([string] $_).Replace('"', '\"') + '"'
    }) -join ' ')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'AgentHost did not start.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $ready = $null
    $lineTask = $process.StandardOutput.ReadLineAsync()
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        if (-not $lineTask.Wait(200)) {
            continue
        }
        $line = $lineTask.Result
        if ($null -eq $line) {
            break
        }
        if ($line.StartsWith('GPTINO_READY ', [StringComparison]::Ordinal)) {
            $ready = $line.Substring(13) | ConvertFrom-Json
            break
        }
        $lineTask = $process.StandardOutput.ReadLineAsync()
    }
    if ($null -eq $ready) {
        $stderr = if ($process.HasExited) { $process.StandardError.ReadToEnd() } else { '' }
        throw "GPTINO_READY timed out. $stderr"
    }

    $duplicateStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $duplicateStartInfo.FileName = $hostPath
    $duplicateStartInfo.WorkingDirectory = $repoRoot
    $duplicateStartInfo.UseShellExecute = $false
    $duplicateStartInfo.CreateNoWindow = $true
    $duplicateStartInfo.RedirectStandardOutput = $true
    $duplicateStartInfo.RedirectStandardError = $true
    $duplicateStartInfo.EnvironmentVariables['GPTINO_API_TOKEN'] = $apiToken
    $duplicateStartInfo.Arguments = $startInfo.Arguments
    $duplicateProcess = [Diagnostics.Process]::new()
    $duplicateProcess.StartInfo = $duplicateStartInfo
    if (-not $duplicateProcess.Start()) {
        throw 'Duplicate AgentHost probe did not start.'
    }
    if (-not $duplicateProcess.WaitForExit(10000)) {
        throw 'Duplicate AgentHost did not fail fast on the file-pair data lock.'
    }
    $duplicateError = $duplicateProcess.StandardError.ReadToEnd()
    if ($duplicateProcess.ExitCode -eq 0 -or $duplicateError -notmatch 'already owns') {
        throw "Duplicate AgentHost was not rejected by the file-pair data lock. $duplicateError"
    }

    $baseUri = [string] $ready.uiBaseUrl
    $parentCredential = [string] $ready.panelParentCredential
    if ([string]::IsNullOrWhiteSpace($baseUri) -or $parentCredential.Length -ne 64) {
        throw 'GPTINO_READY did not match the parent bootstrap schema.'
    }

    $plainHandler = [Net.Http.HttpClientHandler]::new()
    $plainHandler.AllowAutoRedirect = $false
    $plainClient = [Net.Http.HttpClient]::new($plainHandler)
    $clients.Add($plainClient)
    $clients.Add($plainHandler)

    $anonymous = $plainClient.GetAsync("$baseUri/api/v1/health").Result
    if ([int] $anonymous.StatusCode -ne 401) {
        throw "Anonymous API returned $([int] $anonymous.StatusCode), expected 401."
    }

    $sameOrigin = ([Uri] $baseUri).GetLeftPart([UriPartial]::Authority)
    foreach ($rejectedOrigin in @('null', 'not a uri')) {
        $originRequest = New-Request -Method ([Net.Http.HttpMethod]::Get) `
            -Uri "$baseUri/api/v1/health" `
            -Headers @{ 'X-GPTino-Token' = $apiToken; Origin = $rejectedOrigin }
        $originResponse = $plainClient.SendAsync($originRequest).Result
        if ([int] $originResponse.StatusCode -ne 403) {
            throw "Rejected Origin '$rejectedOrigin' returned $([int] $originResponse.StatusCode), expected 403."
        }
    }
    $multipleOriginRequest = New-Request -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$baseUri/api/v1/health" -Headers @{ 'X-GPTino-Token' = $apiToken }
    [void] $multipleOriginRequest.Headers.TryAddWithoutValidation(
        'Origin',
        [string[]] @($sameOrigin, $sameOrigin))
    $multipleOrigin = $plainClient.SendAsync($multipleOriginRequest).Result
    if ([int] $multipleOrigin.StatusCode -ne 403) {
        throw "Multiple Origin values returned $([int] $multipleOrigin.StatusCode), expected 403."
    }

    $healthRequest = New-Request -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$baseUri/api/v1/health" -Headers @{ 'X-GPTino-Token' = $apiToken }
    $health = $plainClient.SendAsync($healthRequest).Result
    if (-not $health.IsSuccessStatusCode) {
        throw "Authenticated health returned $([int] $health.StatusCode)."
    }

    $modelsRequest = New-Request -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$baseUri/api/v1/models" -Headers @{ 'X-GPTino-Token' = $apiToken }
    $models = $plainClient.SendAsync($modelsRequest).Result
    if (-not $models.IsSuccessStatusCode) {
        throw "Codex model catalog returned $([int] $models.StatusCode): $($models.Content.ReadAsStringAsync().Result)"
    }

    $wrongDocumentRequest = New-Request -Method ([Net.Http.HttpMethod]::Post) `
        -Uri "$baseUri/panel/bootstrap?documentSerial=424243" `
        -Headers @{ 'X-GPTino-Panel-Parent' = $parentCredential } -WithEmptyContent
    $wrongDocument = $plainClient.SendAsync($wrongDocumentRequest).Result
    if ([int] $wrongDocument.StatusCode -ne 401) {
        throw "Wrong document bootstrap returned $([int] $wrongDocument.StatusCode), expected 401."
    }

    $bootstrapRequest = New-Request -Method ([Net.Http.HttpMethod]::Post) `
        -Uri "$baseUri/panel/bootstrap?documentSerial=$documentSerial" `
        -Headers @{ 'X-GPTino-Panel-Parent' = $parentCredential } -WithEmptyContent
    $bootstrap = $plainClient.SendAsync($bootstrapRequest).Result
    if (-not $bootstrap.IsSuccessStatusCode) {
        throw "Panel bootstrap returned $([int] $bootstrap.StatusCode)."
    }
    $nonce = [string] (($bootstrap.Content.ReadAsStringAsync().Result | ConvertFrom-Json).nonce)
    if ($nonce.Length -ne 64) {
        throw 'Panel bootstrap returned an invalid nonce.'
    }

    $cookieHandler = [Net.Http.HttpClientHandler]::new()
    $cookieHandler.AllowAutoRedirect = $false
    $cookieHandler.CookieContainer = [Net.CookieContainer]::new()
    $cookieClient = [Net.Http.HttpClient]::new($cookieHandler)
    $clients.Add($cookieClient)
    $clients.Add($cookieHandler)

    $exchange = $cookieClient.GetAsync(
        "$baseUri/panel?bootstrap=$nonce&documentSerial=$documentSerial").Result
    if ([int] $exchange.StatusCode -ne 302) {
        throw "Nonce exchange returned $([int] $exchange.StatusCode), expected 302."
    }
    if ($null -eq $cookieHandler.CookieContainer.GetCookies([Uri] $baseUri)['gptino_runtime']) {
        throw 'Panel nonce exchange did not set the runtime cookie.'
    }
    $reopen = $cookieClient.GetAsync("$baseUri/panel?documentSerial=$documentSerial").Result
    if ([int] $reopen.StatusCode -ne 302) {
        throw "Cookie reopen returned $([int] $reopen.StatusCode), expected 302."
    }
    $sameOriginCookieRequest = New-Request -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$baseUri/api/v1/health" -Headers @{ Origin = $sameOrigin }
    $sameOriginCookie = $cookieClient.SendAsync($sameOriginCookieRequest).Result
    if (-not $sameOriginCookie.IsSuccessStatusCode) {
        throw "Same-origin cookie API returned $([int] $sameOriginCookie.StatusCode)."
    }

    $replayHandler = [Net.Http.HttpClientHandler]::new()
    $replayHandler.AllowAutoRedirect = $false
    $replayClient = [Net.Http.HttpClient]::new($replayHandler)
    $clients.Add($replayClient)
    $clients.Add($replayHandler)
    $replay = $replayClient.GetAsync(
        "$baseUri/panel?bootstrap=$nonce&documentSerial=$documentSerial").Result
    if ([int] $replay.StatusCode -ne 401) {
        throw "Nonce replay returned $([int] $replay.StatusCode), expected 401."
    }

    [pscustomobject] @{
        ReadySchema = 'ok'
        DuplicateRuntime = 'rejected'
        AnonymousApi = 401
        Health = [int] $health.StatusCode
        Models = [int] $models.StatusCode
        WrongDocument = 401
        NonceExchange = 302
        CookieReopen = 302
        OpaqueOrigin = 403
        MalformedOrigin = 403
        MultipleOrigin = 403
        SameOriginCookie = [int] $sameOriginCookie.StatusCode
        NonceReplay = 401
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    throw
}
finally {
    for ($index = $clients.Count - 1; $index -ge 0; $index--) {
        $clients[$index].Dispose()
    }
    if ($null -ne $duplicateProcess) {
        if (-not $duplicateProcess.HasExited) {
            $duplicateProcess.Kill($true)
            [void] $duplicateProcess.WaitForExit(10000)
        }
        $duplicateProcess.Dispose()
    }
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $taskKill = (Get-Command 'taskkill.exe' -ErrorAction Stop).Source
            & $taskKill /PID $process.Id /T /F | Out-Null
            [void] $process.WaitForExit(10000)
        }
        $process.Dispose()
    }
    [Array]::Clear($apiToken.ToCharArray(), 0, $apiToken.Length)
    if ([IO.Directory]::Exists($smokeRoot) -and
        $smokeRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}
