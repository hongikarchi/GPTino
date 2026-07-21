[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $AgentHostExecutable,
    [string] $CodexExecutable,
    [switch] $LiveCodexTurn,
    [ValidateRange(30, 600)]
    [int] $LiveCodexTurnTimeoutSeconds = 180,
    [string] $SmokeBridgeExecutable
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Test-PathWithinDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Directory
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd(
        [char[]] @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    $prefix = $fullDirectory + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoExistingReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
        [char[]] @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    if ($fullPath -ne $fullRepositoryRoot -and
        -not (Test-PathWithinDirectory -Path $fullPath -Directory $fullRepositoryRoot)) {
        throw 'Artifact path escaped the repository.'
    }

    $current = $fullPath
    while (-not [string]::Equals(
        $current.TrimEnd([char[]] @(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)),
        $fullRepositoryRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Artifact path contains a reparse point.'
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            throw 'Artifact path did not resolve beneath the repository.'
        }
        $current = $parent.FullName
    }
}

function Remove-GptinoEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.ProcessStartInfo] $StartInfo
    )

    foreach ($environmentKey in @($StartInfo.EnvironmentVariables.Keys)) {
        $key = [string] $environmentKey
        if ($key.StartsWith('GPTINO_', [StringComparison]::OrdinalIgnoreCase) -or
            $key.StartsWith('GPTINO:', [StringComparison]::OrdinalIgnoreCase)) {
            [void] $StartInfo.EnvironmentVariables.Remove([string] $environmentKey)
        }
    }
}

function Resolve-CodexNativeExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Codex executable was not found: $fullPath"
    }
    if ([string]::Equals(
        [IO.Path]::GetFileName($fullPath),
        'codex.exe',
        [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath
    }
    if (-not [IO.Path]::GetFileName($fullPath).StartsWith(
        'codex.',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Configured Codex path is neither codex.exe nor a supported Codex shim: $fullPath"
    }

    $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    if ([string]::Equals($architecture, 'X64', [StringComparison]::OrdinalIgnoreCase)) {
        $packageSuffix = 'x64'
        $target = 'x86_64-pc-windows-msvc'
    }
    elseif ([string]::Equals($architecture, 'Arm64', [StringComparison]::OrdinalIgnoreCase)) {
        $packageSuffix = 'arm64'
        $target = 'aarch64-pc-windows-msvc'
    }
    else {
        throw "Unsupported Codex Windows architecture: $architecture"
    }

    $commandDirectory = [IO.Path]::GetDirectoryName($fullPath)
    $packageRoot = Join-Path $commandDirectory 'node_modules\@openai\codex'
    $candidates = @(
        (Join-Path $packageRoot "node_modules\@openai\codex-win32-$packageSuffix\vendor\$target\bin\codex.exe"),
        (Join-Path $packageRoot "vendor\$target\bin\codex.exe"),
        (Join-Path $packageRoot "vendor\$target\codex.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw "The Codex shim exists, but its platform-native codex.exe was not found: $fullPath"
}

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
    $bundledCodex = [IO.Path]::Combine(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        '.codex',
        '.sandbox-bin',
        'codex.exe')
    if (Test-Path -LiteralPath $bundledCodex -PathType Leaf) {
        $CodexExecutable = $bundledCodex
    }
    else {
        $codexCommand = Get-Command 'codex.exe' -ErrorAction SilentlyContinue
        if ($null -eq $codexCommand) {
            $codexCommand = Get-Command 'codex.cmd' -ErrorAction Stop
        }
        $CodexExecutable = $codexCommand.Source
    }
}
$CodexExecutable = Resolve-CodexNativeExecutable -Path $CodexExecutable

$smokeBridgePath = $null
if ($LiveCodexTurn) {
    $smokeBridgePath = if ([string]::IsNullOrWhiteSpace($SmokeBridgeExecutable)) {
        Join-Path $repoRoot "tools\GPTino.SmokeBridge\bin\$Configuration\net8.0\GPTino.SmokeBridge.exe"
    }
    else {
        [IO.Path]::GetFullPath($SmokeBridgeExecutable)
    }
    if (-not (Test-Path -LiteralPath $smokeBridgePath -PathType Leaf)) {
        throw "Smoke bridge was not built: $smokeBridgePath"
    }
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$devLoopRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'dev-loop'))
$runId = 'smoke-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + `
    [Guid]::NewGuid().ToString('N')
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $devLoopRoot $runId))
if (-not (Test-PathWithinDirectory -Path $smokeRoot -Directory $devLoopRoot)) {
    throw 'Smoke directory escaped the development artifact root.'
}
Assert-NoExistingReparsePoint -Path $smokeRoot -RepositoryRoot $repoRoot
[IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
Assert-NoExistingReparsePoint -Path $smokeRoot -RepositoryRoot $repoRoot
$ownedMarker = Join-Path $smokeRoot '.gptino-owned-run'
[IO.File]::WriteAllText($ownedMarker, "GPTino smoke run`n", [Text.UTF8Encoding]::new($false))

$tokenBytes = New-Object byte[] 32
$bridgeSecretBytes = if ($LiveCodexTurn) { New-Object byte[] 32 } else { $null }
$fingerprintBytes = if ($LiveCodexTurn) { New-Object byte[] 32 } else { $null }
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($tokenBytes)
    if ($LiveCodexTurn) {
        $random.GetBytes($bridgeSecretBytes)
        $random.GetBytes($fingerprintBytes)
    }
}
finally {
    $random.Dispose()
}
$apiToken = ([BitConverter]::ToString($tokenBytes) -replace '-', '')
[Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
$bridgeSecret = $null
$smokeFingerprint = $null
$smokeFingerprintEvidence = $null
$unicodeResponseSentinel = $null
if ($LiveCodexTurn) {
    $bridgeSecret = [Convert]::ToBase64String($bridgeSecretBytes)
    $smokeFingerprint = 'gptino-smoke-' + (
        [BitConverter]::ToString($fingerprintBytes) -replace '-', '').ToLowerInvariant()
    [Array]::Clear($bridgeSecretBytes, 0, $bridgeSecretBytes.Length)
    [Array]::Clear($fingerprintBytes, 0, $fingerprintBytes.Length)
    $fingerprintTextBytes = [Text.Encoding]::UTF8.GetBytes($smokeFingerprint)
    $fingerprintHasher = [Security.Cryptography.SHA256]::Create()
    try {
        $fingerprintHash = $fingerprintHasher.ComputeHash($fingerprintTextBytes)
        $smokeFingerprintEvidence = (
            ([BitConverter]::ToString($fingerprintHash) -replace '-', '').ToLowerInvariant()
        ).Substring(0, 16)
        [Array]::Clear($fingerprintHash, 0, $fingerprintHash.Length)
    }
    finally {
        [Array]::Clear($fingerprintTextBytes, 0, $fingerprintTextBytes.Length)
        $fingerprintHasher.Dispose()
    }
    # Code points keep the Korean round-trip sentinel exact under Windows PowerShell 5.1 source decoding.
    $unicodeResponseSentinel = -join [char[]] @(
        0xD55C, 0xAE00, 0xC751, 0xB2F5, 0x003D, 0xC815, 0xC0C1)
}

$documentSerial = 424242
$projectId = [Guid]::NewGuid()
$grasshopperDocumentId = [Guid]::NewGuid()
$rhinoPath = Join-Path $smokeRoot 'smoke.3dm'
$grasshopperPath = Join-Path $smokeRoot 'smoke.gh'
$bridgePipe = if ($LiveCodexTurn) {
    'gptino-smoke-' + [Guid]::NewGuid().ToString('N')
}
else {
    $null
}
$process = $null
$processIdentity = $null
$duplicateProcess = $null
$duplicateProcessIdentity = $null
$bridgeProcess = $null
$bridgeProcessIdentity = $null
$codexProcess = $null
$codexProcessIdentity = $null
$bridgeSnapshotLineTask = $null
$hostOutputDrainTask = $null
$hostErrorDrainTask = $null
$primaryException = $null
$cleanupErrors = New-Object System.Collections.Generic.List[Exception]
$clients = New-Object System.Collections.Generic.List[IDisposable]
$ownedProcessIdentities = New-Object System.Collections.Generic.List[object]

function New-Request {
    param(
        [Net.Http.HttpMethod] $Method,
        [string] $Uri,
        [hashtable] $Headers = @{},
        [switch] $WithEmptyContent,
        [string] $JsonContent
    )

    $request = [Net.Http.HttpRequestMessage]::new($Method, $Uri)
    foreach ($entry in $Headers.GetEnumerator()) {
        [void] $request.Headers.TryAddWithoutValidation([string] $entry.Key, [string] $entry.Value)
    }
    if ($WithEmptyContent) {
        $request.Content = [Net.Http.StringContent]::new('')
    }
    elseif ($PSBoundParameters.ContainsKey('JsonContent')) {
        $request.Content = [Net.Http.StringContent]::new(
            $JsonContent,
            [Text.Encoding]::UTF8,
            'application/json')
    }
    return $request
}

function Invoke-JsonRequest {
    param(
        [Parameter(Mandatory = $true)]
        [Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)]
        [Net.Http.HttpMethod] $Method,
        [Parameter(Mandatory = $true)]
        [string] $Uri,
        [Parameter(Mandatory = $true)]
        [hashtable] $Headers,
        [string] $JsonContent
    )

    $requestArguments = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
    }
    if ($PSBoundParameters.ContainsKey('JsonContent')) {
        $requestArguments.JsonContent = $JsonContent
    }
    $request = New-Request @requestArguments
    $response = $null
    try {
        $response = $Client.SendAsync($request).Result
        $body = $response.Content.ReadAsStringAsync().Result
        $json = if ([string]::IsNullOrWhiteSpace($body)) {
            $null
        }
        else {
            $body | ConvertFrom-Json
        }
        return [pscustomobject] @{
            StatusCode = [int] $response.StatusCode
            IsSuccessStatusCode = $response.IsSuccessStatusCode
            Body = $body
            Json = $json
        }
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
        $request.Dispose()
    }
}

function New-OwnedProcessIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process] $OwnedProcess,
        [Parameter(Mandatory = $true)]
        [string] $ExpectedExecutable
    )

    $OwnedProcess.Refresh()
    return [pscustomobject] @{
        ProcessId = $OwnedProcess.Id
        ProcessStartTimeUtc = $OwnedProcess.StartTime.ToUniversalTime()
        ExecutablePath = [IO.Path]::GetFullPath($ExpectedExecutable)
    }
}

function Add-OwnedProcessIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]] $Ledger,
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [psobject] $Identity,
        [Parameter(Mandatory = $true)]
        [string] $RunRoot
    )

    $entry = [pscustomobject] @{
        Name = $Name
        ProcessId = [int] $Identity.ProcessId
        ProcessStartTimeUtc = [DateTime] $Identity.ProcessStartTimeUtc
        ExecutablePath = [string] $Identity.ExecutablePath
    }
    $Ledger.Add($entry)
    $ledgerRoot = Join-Path $RunRoot 'owned-processes'
    Assert-NoExistingReparsePoint -Path $ledgerRoot -RepositoryRoot $script:repoRoot
    [IO.Directory]::CreateDirectory($ledgerRoot) | Out-Null
    Assert-NoExistingReparsePoint -Path $ledgerRoot -RepositoryRoot $script:repoRoot
    $fileName = '{0:D2}-{1}.json' -f $Ledger.Count, $Name
    $path = Join-Path $ledgerRoot $fileName
    $temporaryPath = Join-Path $RunRoot ('.owned-process-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $json = ConvertTo-Json -InputObject $entry -Depth 4
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryPath, $path)
}

function Stop-OwnedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process] $OwnedProcess,
        [Parameter(Mandatory = $true)]
        [psobject] $OwnedIdentity
    )

    try {
        $OwnedProcess.Refresh()
        if (-not $OwnedProcess.HasExited) {
            $actualExecutable = $OwnedProcess.MainModule.FileName
            if ($OwnedProcess.Id -ne [int] $OwnedIdentity.ProcessId -or
                $OwnedProcess.StartTime.ToUniversalTime() -ne
                    [DateTime] $OwnedIdentity.ProcessStartTimeUtc -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($actualExecutable),
                    [string] $OwnedIdentity.ExecutablePath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw [IO.InvalidDataException]::new(
                    'Refusing to stop a process whose PID/start-time/executable identity changed.')
            }
            # Kill is invoked on the exact Process instance captured at Start(); closing the host's
            # redirected pipes also gives its Codex App Server child EOF without a name/PID sweep.
            $OwnedProcess.Kill()
            if (-not $OwnedProcess.WaitForExit(10000)) {
                throw "Process $($OwnedProcess.Id) did not exit during smoke cleanup."
            }
        }
    }
    catch [InvalidOperationException] {
        $processStillRunning = $false
        try {
            $OwnedProcess.Refresh()
            $processStillRunning = -not $OwnedProcess.HasExited
        }
        catch [InvalidOperationException] {
            # The exact owned process exited between identity verification and cleanup.
        }
        if ($processStillRunning) {
            throw
        }
    }
    finally {
        $OwnedProcess.Dispose()
    }
}

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    [void] $startInfo.EnvironmentVariables
    Remove-GptinoEnvironment -StartInfo $startInfo
    $startInfo.FileName = $hostPath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['GPTINO_API_TOKEN'] = $apiToken
    if ($LiveCodexTurn) {
        $startInfo.EnvironmentVariables['GPTINO_BRIDGE_SECRET'] = $bridgeSecret
    }
    $arguments = @(
        '--project-id', $projectId.ToString('D'),
        '--project-directory', $repoRoot,
        '--data-directory', $smokeRoot,
        '--rhino', $rhinoPath,
        '--grasshopper', $grasshopperPath,
        '--rhino-document-serial', $documentSerial.ToString(),
        '--codex-executable', $CodexExecutable
    )
    if ($LiveCodexTurn) {
        $arguments += @('--bridge-pipe', $bridgePipe)
    }
    $startInfo.Arguments = (($arguments | ForEach-Object {
        '"' + ([string] $_).Replace('"', '\"') + '"'
    }) -join ' ')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'AgentHost did not start.'
    }
    $processIdentity = New-OwnedProcessIdentity `
        -OwnedProcess $process `
        -ExpectedExecutable $hostPath
    Add-OwnedProcessIdentity `
        -Ledger $ownedProcessIdentities `
        -Name 'AgentHost' `
        -Identity $processIdentity `
        -RunRoot $smokeRoot
    $hostErrorDrainTask = $process.StandardError.ReadToEndAsync()

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
        $stderr = if ($process.HasExited -and $hostErrorDrainTask.Wait(1000)) {
            $hostErrorDrainTask.Result
        }
        else {
            ''
        }
        throw "GPTINO_READY timed out. $stderr"
    }
    $hostOutputDrainTask = $process.StandardOutput.ReadToEndAsync()

    $duplicateStartInfo = [Diagnostics.ProcessStartInfo]::new()
    [void] $duplicateStartInfo.EnvironmentVariables
    Remove-GptinoEnvironment -StartInfo $duplicateStartInfo
    $duplicateStartInfo.FileName = $hostPath
    $duplicateStartInfo.WorkingDirectory = $repoRoot
    $duplicateStartInfo.UseShellExecute = $false
    $duplicateStartInfo.CreateNoWindow = $true
    $duplicateStartInfo.RedirectStandardOutput = $true
    $duplicateStartInfo.RedirectStandardError = $true
    $duplicateStartInfo.EnvironmentVariables['GPTINO_API_TOKEN'] = $apiToken
    if ($LiveCodexTurn) {
        $duplicateStartInfo.EnvironmentVariables['GPTINO_BRIDGE_SECRET'] = $bridgeSecret
    }
    $duplicateStartInfo.Arguments = $startInfo.Arguments
    $duplicateProcess = [Diagnostics.Process]::new()
    $duplicateProcess.StartInfo = $duplicateStartInfo
    if (-not $duplicateProcess.Start()) {
        throw 'Duplicate AgentHost probe did not start.'
    }
    $duplicateProcessIdentity = New-OwnedProcessIdentity `
        -OwnedProcess $duplicateProcess `
        -ExpectedExecutable $hostPath
    Add-OwnedProcessIdentity `
        -Ledger $ownedProcessIdentities `
        -Name 'DuplicateAgentHostProbe' `
        -Identity $duplicateProcessIdentity `
        -RunRoot $smokeRoot
    if (-not $duplicateProcess.WaitForExit(10000)) {
        throw 'Duplicate AgentHost did not fail fast on the file-pair data lock.'
    }
    $duplicateError = $duplicateProcess.StandardError.ReadToEnd()
    if ($duplicateProcess.ExitCode -eq 0 -or $duplicateError -notmatch 'already owns') {
        throw "Duplicate AgentHost was not rejected by the file-pair data lock. $duplicateError"
    }

    if ($LiveCodexTurn) {
        $bridgeStartInfo = [Diagnostics.ProcessStartInfo]::new()
        [void] $bridgeStartInfo.EnvironmentVariables
        $bridgeStartInfo.FileName = $smokeBridgePath
        $bridgeStartInfo.WorkingDirectory = $repoRoot
        $bridgeStartInfo.UseShellExecute = $false
        $bridgeStartInfo.CreateNoWindow = $true
        $bridgeStartInfo.RedirectStandardOutput = $true
        $bridgeStartInfo.RedirectStandardError = $true
        Remove-GptinoEnvironment -StartInfo $bridgeStartInfo
        $bridgeStartInfo.EnvironmentVariables['GPTINO_BRIDGE_SECRET'] = $bridgeSecret
        $bridgeStartInfo.EnvironmentVariables['GPTINO_SMOKE_FINGERPRINT'] = $smokeFingerprint
        $bridgeArguments = @(
            '--pipe-name', $bridgePipe,
            '--project-id', $projectId.ToString('D'),
            '--rhino', $rhinoPath,
            '--grasshopper', $grasshopperPath,
            '--rhino-document-serial', $documentSerial.ToString(),
            '--grasshopper-document-id', $grasshopperDocumentId.ToString('D')
        )
        $bridgeStartInfo.Arguments = (($bridgeArguments | ForEach-Object {
            '"' + ([string] $_).Replace('"', '\"') + '"'
        }) -join ' ')

        $bridgeProcess = [Diagnostics.Process]::new()
        $bridgeProcess.StartInfo = $bridgeStartInfo
        if (-not $bridgeProcess.Start()) {
            throw 'Smoke bridge did not start.'
        }
        $bridgeProcessIdentity = New-OwnedProcessIdentity `
            -OwnedProcess $bridgeProcess `
            -ExpectedExecutable $smokeBridgePath
        Add-OwnedProcessIdentity `
            -Ledger $ownedProcessIdentities `
            -Name 'SmokeBridge' `
            -Identity $bridgeProcessIdentity `
            -RunRoot $smokeRoot

        $bridgeDeadline = [DateTime]::UtcNow.AddSeconds(20)
        $bridgeReady = $false
        $bridgeLineTask = $bridgeProcess.StandardOutput.ReadLineAsync()
        while ([DateTime]::UtcNow -lt $bridgeDeadline -and -not $bridgeProcess.HasExited) {
            if (-not $bridgeLineTask.Wait(200)) {
                continue
            }
            $bridgeLine = $bridgeLineTask.Result
            if ($null -eq $bridgeLine) {
                break
            }
            if ($bridgeLine -eq 'GPTINO_SMOKE_BRIDGE_READY') {
                $bridgeReady = $true
                break
            }
            throw "Smoke bridge emitted an unexpected readiness line: $bridgeLine"
        }
        if (-not $bridgeReady) {
            $bridgeError = if ($bridgeProcess.HasExited) {
                $bridgeProcess.StandardError.ReadToEnd()
            }
            else {
                ''
            }
            throw "Smoke bridge readiness timed out. $bridgeError"
        }
        $bridgeSnapshotLineTask = $bridgeProcess.StandardOutput.ReadLineAsync()

        [void] $startInfo.EnvironmentVariables.Remove('GPTINO_BRIDGE_SECRET')
        [void] $duplicateStartInfo.EnvironmentVariables.Remove('GPTINO_BRIDGE_SECRET')
        [void] $bridgeStartInfo.EnvironmentVariables.Remove('GPTINO_BRIDGE_SECRET')
        [void] $bridgeStartInfo.EnvironmentVariables.Remove('GPTINO_SMOKE_FINGERPRINT')
        $bridgeSecret = $null
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
    if ($LiveCodexTurn) {
        $healthState = $health.Content.ReadAsStringAsync().Result | ConvertFrom-Json
        if (-not [bool] $healthState.bridgeConnected) {
            throw 'Smoke bridge registered but authenticated health did not report bridgeConnected=true.'
        }
    }

    $modelsRequest = New-Request -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$baseUri/api/v1/models" -Headers @{ 'X-GPTino-Token' = $apiToken }
    $models = $plainClient.SendAsync($modelsRequest).Result
    if (-not $models.IsSuccessStatusCode) {
        throw "Codex model catalog returned $([int] $models.StatusCode): $($models.Content.ReadAsStringAsync().Result)"
    }
    $codexHealth = Invoke-JsonRequest `
        -Client $plainClient `
        -Method ([Net.Http.HttpMethod]::Get) `
        -Uri "$baseUri/api/v1/health" `
        -Headers @{ 'X-GPTino-Token' = $apiToken }
    if (-not $codexHealth.IsSuccessStatusCode -or
        -not [bool] $codexHealth.Json.codexRunning -or
        $null -eq $codexHealth.Json.codexProcessId -or
        $null -eq $codexHealth.Json.codexProcessStartTimeUtc) {
        throw 'Codex model catalog completed without an exact live App Server identity.'
    }
    $codexProcess = [Diagnostics.Process]::GetProcessById(
        [int] $codexHealth.Json.codexProcessId)
    $codexProcessIdentity = New-OwnedProcessIdentity `
        -OwnedProcess $codexProcess `
        -ExpectedExecutable $CodexExecutable
    $codexProcess.Refresh()
    if ($codexProcessIdentity.ProcessStartTimeUtc -ne
            ([DateTime] $codexHealth.Json.codexProcessStartTimeUtc).ToUniversalTime() -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($codexProcess.MainModule.FileName),
            [IO.Path]::GetFullPath($CodexExecutable),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Authenticated health did not match the exact configured Codex App Server process.'
    }
    Add-OwnedProcessIdentity `
        -Ledger $ownedProcessIdentities `
        -Name 'CodexAppServer' `
        -Identity $codexProcessIdentity `
        -RunRoot $smokeRoot

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

    $liveCodexResult = $null
    if ($LiveCodexTurn) {
        $apiHeaders = @{ 'X-GPTino-Token' = $apiToken }
        $createSessionBody = @{
            name = 'Codex live smoke'
            role = 'planner'
            modelProfile = 'auto'
            model = $null
        } | ConvertTo-Json -Compress
        $createSession = Invoke-JsonRequest `
            -Client $plainClient `
            -Method ([Net.Http.HttpMethod]::Post) `
            -Uri "$baseUri/api/v1/sessions" `
            -Headers $apiHeaders `
            -JsonContent $createSessionBody
        if ($createSession.StatusCode -ne 201) {
            throw "Live smoke session creation returned $($createSession.StatusCode): $($createSession.Body)"
        }
        $sessionIdText = [string] $createSession.Json.id
        $sessionId = [Guid]::Empty
        if (-not [Guid]::TryParse($sessionIdText, [ref] $sessionId) -or $sessionId -eq [Guid]::Empty) {
            throw 'Live smoke session creation returned an invalid session id.'
        }

        $smokePrompt = @"
This is an automated read-only smoke test. Call gptino_v1.snapshot_read exactly once with {"scopes":["canvas"]}. Do not call artifact_write, change_submit, or any mutation tool. After the tool returns, answer on exactly one line in this form:
GPTINO_SMOKE_OK $unicodeResponseSentinel documentFingerprint=<canvas.documentFingerprint> revision=<revision>
Copy the exact canvas.documentFingerprint and top-level revision from the tool result. Do not guess or use placeholders.
"@
        $sendMessageBody = @{
            content = $smokePrompt.Trim()
            clientMessageId = 'smoke-' + [Guid]::NewGuid().ToString('N')
        } | ConvertTo-Json -Compress
        $acceptedTurn = Invoke-JsonRequest `
            -Client $plainClient `
            -Method ([Net.Http.HttpMethod]::Post) `
            -Uri "$baseUri/api/v1/sessions/$($sessionId.ToString('D'))/messages" `
            -Headers $apiHeaders `
            -JsonContent $sendMessageBody
        if ($acceptedTurn.StatusCode -ne 202) {
            throw "Live Codex turn returned $($acceptedTurn.StatusCode): $($acceptedTurn.Body)"
        }
        $acceptedMessageId = [long] $acceptedTurn.Json.messageId

        $expectedAssistantLine =
            "GPTINO_SMOKE_OK $unicodeResponseSentinel documentFingerprint=$smokeFingerprint revision=1"
        $assistantMatched = $false
        $sessionIdle = $false
        $runtimeRevision = 0L
        $lastSystemError = $null
        $turnDeadline = [DateTime]::UtcNow.AddSeconds($LiveCodexTurnTimeoutSeconds)
        while ([DateTime]::UtcNow -lt $turnDeadline) {
            if ($bridgeProcess.HasExited) {
                $bridgeError = $bridgeProcess.StandardError.ReadToEnd()
                throw "Smoke bridge exited before the live turn completed. $bridgeError"
            }

            $messages = Invoke-JsonRequest `
                -Client $plainClient `
                -Method ([Net.Http.HttpMethod]::Get) `
                -Uri "$baseUri/api/v1/sessions/$($sessionId.ToString('D'))/messages?after=$acceptedMessageId&limit=250" `
                -Headers $apiHeaders
            if (-not $messages.IsSuccessStatusCode) {
                throw "Live smoke messages returned $($messages.StatusCode): $($messages.Body)"
            }
            foreach ($message in @($messages.Json)) {
                if ([string] $message.role -eq 'assistant' -and
                    ([string] $message.content).Equals(
                        $expectedAssistantLine, [StringComparison]::Ordinal)) {
                    $assistantMatched = $true
                }
                if ([string] $message.role -eq 'system' -and [string] $message.phase -eq 'error') {
                    $lastSystemError = [string] $message.content
                }
            }

            $runtime = Invoke-JsonRequest `
                -Client $plainClient `
                -Method ([Net.Http.HttpMethod]::Get) `
                -Uri "$baseUri/api/v1/runtime" `
                -Headers $apiHeaders
            if (-not $runtime.IsSuccessStatusCode) {
                throw "Live smoke runtime returned $($runtime.StatusCode): $($runtime.Body)"
            }
            $sessionView = @($runtime.Json.sessions) | Where-Object {
                [string] $_.id -eq $sessionId.ToString('D')
            } | Select-Object -First 1
            if ($null -eq $sessionView) {
                throw 'Live smoke session disappeared from runtime state.'
            }
            $sessionStatus = [string] $sessionView.status
            $sessionIdle = $sessionStatus -eq 'idle'
            $runtimeRevision = [long] $runtime.Json.revision
            if ($sessionStatus -eq 'blocked') {
                $failureDetail = if ([string]::IsNullOrWhiteSpace($lastSystemError)) {
                    'No persisted system error was available.'
                }
                else {
                    $lastSystemError
                }
                throw "Live Codex turn entered failed state. $failureDetail"
            }
            if ($assistantMatched -and $sessionIdle) {
                break
            }
            Start-Sleep -Milliseconds 500
        }

        if (-not $assistantMatched -or -not $sessionIdle) {
            throw "Live Codex turn did not return a verified assistant response within $LiveCodexTurnTimeoutSeconds seconds."
        }
        if ($runtimeRevision -ne 1L) {
            throw "Read-only live smoke expected runtime revision 1, received $runtimeRevision."
        }
        if ($null -eq $bridgeSnapshotLineTask -or -not $bridgeSnapshotLineTask.Wait(5000)) {
            throw 'Smoke bridge did not observe canvas.snapshot during the live turn.'
        }
        $expectedEvidenceLine = "GPTINO_SMOKE_SNAPSHOT sha256=$smokeFingerprintEvidence"
        if ([string] $bridgeSnapshotLineTask.Result -ne $expectedEvidenceLine) {
            throw 'Smoke bridge returned invalid snapshot observation evidence.'
        }
        $unexpectedBridgeLineTask = $bridgeProcess.StandardOutput.ReadLineAsync()
        if ($unexpectedBridgeLineTask.Wait(750)) {
            throw 'Smoke bridge observed an additional or forbidden protocol request.'
        }
        $bridgeProcess.Refresh()
        if ($bridgeProcess.HasExited) {
            throw 'Smoke bridge exited after reporting snapshot evidence.'
        }
        $artifactFiles = @()
        $artifactRoot = Join-Path $smokeRoot 'artifacts'
        if ([IO.Directory]::Exists($artifactRoot)) {
            $artifactFiles = @(Get-ChildItem `
                -LiteralPath $artifactRoot `
                -Recurse `
                -File `
                -ErrorAction Stop)
        }
        if ($artifactFiles.Count -ne 0) {
            throw 'Read-only live smoke created a session artifact unexpectedly.'
        }

        $finalHealth = Invoke-JsonRequest `
            -Client $plainClient `
            -Method ([Net.Http.HttpMethod]::Get) `
            -Uri "$baseUri/api/v1/health" `
            -Headers $apiHeaders
        if (-not $finalHealth.IsSuccessStatusCode -or
            -not [bool] $finalHealth.Json.bridgeConnected -or
            -not [bool] $finalHealth.Json.codexRunning) {
            throw 'Final live health did not report both bridgeConnected=true and codexRunning=true.'
        }
        if ([int] $finalHealth.Json.codexProcessId -ne $codexProcessIdentity.ProcessId -or
            ([DateTime] $finalHealth.Json.codexProcessStartTimeUtc).ToUniversalTime() -ne
                $codexProcessIdentity.ProcessStartTimeUtc) {
            $replacementCodexProcess = [Diagnostics.Process]::GetProcessById(
                [int] $finalHealth.Json.codexProcessId)
            $replacementCodexIdentity = New-OwnedProcessIdentity `
                -OwnedProcess $replacementCodexProcess `
                -ExpectedExecutable $CodexExecutable
            $replacementCodexProcess.Refresh()
            if ($replacementCodexIdentity.ProcessStartTimeUtc -ne
                    ([DateTime] $finalHealth.Json.codexProcessStartTimeUtc).ToUniversalTime() -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath($replacementCodexProcess.MainModule.FileName),
                    [IO.Path]::GetFullPath($CodexExecutable),
                    [StringComparison]::OrdinalIgnoreCase)) {
                $replacementCodexProcess.Dispose()
                throw 'Replacement Codex identity did not match the configured executable.'
            }
            Add-OwnedProcessIdentity `
                -Ledger $ownedProcessIdentities `
                -Name 'ReplacementCodexAppServer' `
                -Identity $replacementCodexIdentity `
                -RunRoot $smokeRoot
            $codexProcess.Dispose()
            $codexProcess = $replacementCodexProcess
            $codexProcessIdentity = $replacementCodexIdentity
            throw 'Codex App Server restarted during the live smoke turn.'
        }
        $liveCodexResult = 'ok'
    }

    $result = [pscustomobject] @{
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
    if ($LiveCodexTurn) {
        $result | Add-Member -NotePropertyName LiveCodexTurn -NotePropertyValue $liveCodexResult
        $result | Add-Member -NotePropertyName SnapshotEvidence -NotePropertyValue $smokeFingerprintEvidence
    }
    $result
}
catch {
    $primaryException = $_.Exception
    [Console]::Error.WriteLine($primaryException.ToString())
}
finally {
    $apiToken = $null
    $bridgeSecret = $null
    $smokeFingerprint = $null
    $unicodeResponseSentinel = $null
    for ($index = $clients.Count - 1; $index -ge 0; $index--) {
        try {
            $clients[$index].Dispose()
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $bridgeStartInfo) {
        try {
            [void] $bridgeStartInfo.EnvironmentVariables.Remove('GPTINO_API_TOKEN')
            [void] $bridgeStartInfo.EnvironmentVariables.Remove('GPTINO_BRIDGE_SECRET')
            [void] $bridgeStartInfo.EnvironmentVariables.Remove('GPTINO_SMOKE_FINGERPRINT')
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $startInfo) {
        try {
            [void] $startInfo.EnvironmentVariables.Remove('GPTINO_API_TOKEN')
            [void] $startInfo.EnvironmentVariables.Remove('GPTINO_BRIDGE_SECRET')
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $duplicateStartInfo) {
        try {
            [void] $duplicateStartInfo.EnvironmentVariables.Remove('GPTINO_API_TOKEN')
            [void] $duplicateStartInfo.EnvironmentVariables.Remove('GPTINO_BRIDGE_SECRET')
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $bridgeProcess) {
        try {
            if ($null -eq $bridgeProcessIdentity) {
                throw 'Smoke bridge started without a captured process identity.'
            }
            Stop-OwnedProcess `
                -OwnedProcess $bridgeProcess `
                -OwnedIdentity $bridgeProcessIdentity
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $duplicateProcess) {
        try {
            if ($null -eq $duplicateProcessIdentity) {
                throw 'Duplicate AgentHost started without a captured process identity.'
            }
            Stop-OwnedProcess `
                -OwnedProcess $duplicateProcess `
                -OwnedIdentity $duplicateProcessIdentity
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $process) {
        try {
            if ($null -eq $processIdentity) {
                throw 'AgentHost started without a captured process identity.'
            }
            Stop-OwnedProcess `
                -OwnedProcess $process `
                -OwnedIdentity $processIdentity
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
    if ($null -ne $codexProcess) {
        try {
            if ($null -eq $codexProcessIdentity) {
                throw 'Codex App Server was observed without a captured process identity.'
            }
            Stop-OwnedProcess `
                -OwnedProcess $codexProcess `
                -OwnedIdentity $codexProcessIdentity
        }
        catch {
            $cleanupErrors.Add($_.Exception)
        }
    }
}

if ($cleanupErrors.Count -ne 0) {
    $allErrors = New-Object System.Collections.Generic.List[Exception]
    if ($null -ne $primaryException) {
        $allErrors.Add($primaryException)
    }
    $allErrors.AddRange($cleanupErrors)
    throw [AggregateException]::new('GPTino smoke cleanup did not complete safely.', $allErrors.ToArray())
}
if ($null -ne $primaryException) {
    throw $primaryException
}
