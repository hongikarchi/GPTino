#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$Version,

    [string]$OutputRoot,

    [switch]$SkipPanelBuild,

    [switch]$SkipRestore,

    [switch]$SkipSolutionBuild,

    [switch]$SkipTests,

    [switch]$BuildYak,

    [string]$YakExe
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'GPTino packages can only be assembled on Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = $artifactRoot
}
else {
    $OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
}

$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ($OutputRoot + [IO.Path]::DirectorySeparatorChar).StartsWith(
        $artifactPrefix,
        [StringComparison]::OrdinalIgnoreCase) -and
    -not [string]::Equals($OutputRoot, $artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be the repository artifacts directory or one of its descendants: $artifactRoot"
}

$manifestSource = Join-Path $repoRoot 'packaging\yak\manifest.yml'
$manifestText = [IO.File]::ReadAllText($manifestSource)
$versionMatch = [regex]::Match($manifestText, '(?m)^version:\s*(\S+)\s*$')
if (-not $versionMatch.Success) {
    throw "No version field was found in $manifestSource"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $versionMatch.Groups[1].Value
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Version '$Version' is not a supported Yak semantic version."
}

$solution = Join-Path $repoRoot 'GPTino.sln'
$panelRoot = Join-Path $repoRoot 'ui\panel'
$agentProject = Join-Path $repoRoot 'src\GPTino.AgentHost\GPTino.AgentHost.csproj'
$terminalProject = Join-Path $repoRoot 'src\GPTino.Terminal\GPTino.Terminal.csproj'
$frameworkFolder = 'net8.0'
$stageRoot = Join-Path $OutputRoot 'yak\GPTino'
$pluginStage = Join-Path $stageRoot $frameworkFolder
$agentStage = Join-Path $pluginStage 'agent'
$legalStage = Join-Path $stageRoot 'legal'
$publishRoot = Join-Path $OutputRoot 'publish'
$agentPublish = Join-Path $publishRoot 'agent'
$terminalPublish = Join-Path $publishRoot 'terminal'

function Assert-GeneratedPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a generated path outside $artifactRoot`: $fullPath"
    }

    return $fullPath
}

function Get-RelativePackagePath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )

    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path is outside its staging root: $fullPath"
    }
    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-GeneratedPath $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath exited with code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Copy-VerifiedFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $destinationHash, [StringComparison]::Ordinal)) {
            throw "Package dependency collision at $Destination"
        }
        return
    }

    Copy-Item -LiteralPath $Source -Destination $Destination
}

function Copy-PluginOutput {
    param([Parameter(Mandatory)][string]$SourceDirectory)

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Expected plug-in output was not found: $SourceDirectory"
    }

    $files = Get-ChildItem -LiteralPath $SourceDirectory -File | Where-Object {
        $_.Name -match '^GPTino\..+\.(?:rhp|gha|dll|deps\.json|runtimeconfig\.json)$'
    }
    foreach ($file in $files) {
        Copy-VerifiedFile $file.FullName (Join-Path $pluginStage $file.Name)
    }
}

$requiredCommands = @('dotnet')
if (-not $SkipPanelBuild) {
    $requiredCommands += 'npm.cmd'
}
foreach ($command in $requiredCommands) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required build command '$command' was not found on PATH."
    }
}

if (-not $SkipPanelBuild) {
    Invoke-Tool 'npm.cmd' @('ci') $panelRoot
    Invoke-Tool 'npm.cmd' @('run', 'test') $panelRoot
    Invoke-Tool 'npm.cmd' @('run', 'build') $panelRoot
}

$panelIndex = Join-Path $panelRoot 'dist\index.html'
if (-not (Test-Path -LiteralPath $panelIndex -PathType Leaf)) {
    throw 'The panel build is missing. Run without -SkipPanelBuild or build ui/panel first.'
}

if (-not $SkipRestore) {
    Invoke-Tool 'dotnet' @('restore', $solution, "/p:Version=$Version") $repoRoot
}

if (-not $SkipSolutionBuild) {
    $buildArguments = @(
        'build', $solution,
        '-c', $Configuration,
        '--no-restore',
        '--no-incremental',
        "/p:Version=$Version",
        '/p:IncludeSourceRevisionInInformationalVersion=false'
    )
    Invoke-Tool 'dotnet' $buildArguments $repoRoot
}

if (-not $SkipTests) {
    $testArguments = @('test', $solution, '-c', $Configuration, '--no-build', '--no-restore')
    Invoke-Tool 'dotnet' $testArguments $repoRoot
}

Reset-GeneratedDirectory $stageRoot
Reset-GeneratedDirectory $publishRoot
New-Item -ItemType Directory -Path $pluginStage, $agentStage, $legalStage -Force | Out-Null

$publishCommon = @(
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    "/p:Version=$Version",
    '/p:IncludeSourceRevisionInInformationalVersion=false',
    '/p:DebugType=None',
    '/p:DebugSymbols=false'
)
Invoke-Tool 'dotnet' (@('publish', $agentProject) + $publishCommon + @('-o', $agentPublish)) $repoRoot
Invoke-Tool 'dotnet' (@(
        'publish', $terminalProject
    ) + $publishCommon + @(
        '/p:PublishSingleFile=true',
        '/p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $terminalPublish
    )) $repoRoot

$rhinoOutput = Join-Path $repoRoot "src\GPTino.Rhino\bin\$Configuration\net8.0-windows"
$grasshopperOutput = Join-Path $repoRoot "src\GPTino.Grasshopper\bin\$Configuration\net8.0-windows"
Copy-PluginOutput $rhinoOutput
Copy-PluginOutput $grasshopperOutput

Copy-Item -Path (Join-Path $agentPublish '*') -Destination $agentStage -Recurse -Force
$terminalExecutable = Join-Path $terminalPublish 'GPTino.Terminal.exe'
if (-not (Test-Path -LiteralPath $terminalExecutable -PathType Leaf)) {
    throw "The terminal publish did not produce $terminalExecutable"
}
Copy-Item -LiteralPath $terminalExecutable -Destination (Join-Path $agentStage 'GPTino.Terminal.exe') -Force

$stagedManifest = [regex]::new('(?m)^version:\s*\S+\s*$').Replace(
    $manifestText,
    "version: $Version",
    1)
[IO.File]::WriteAllText(
    (Join-Path $stageRoot 'manifest.yml'),
    $stagedManifest,
    [Text.UTF8Encoding]::new($false))

foreach ($legalFile in @('LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $legalFile) -Destination (Join-Path $stageRoot $legalFile)
}

$nugetRoot = $env:NUGET_PACKAGES
if ([string]::IsNullOrWhiteSpace($nugetRoot)) {
    $nugetRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
$dotnetRoot = $env:DOTNET_ROOT
if ([string]::IsNullOrWhiteSpace($dotnetRoot)) {
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
}
$legalCopies = @(
    @((Join-Path $repoRoot 'references\licenses\GPTino.Direct.MIT.txt'), 'DIRECT_MIT_LICENSES.txt'),
    @((Join-Path $repoRoot 'references\licenses\Cordyceps.MIT.txt'), 'CORDYCEPS_MIT_LICENSE.txt'),
    @((Join-Path $repoRoot 'references\licenses\SQLitePCLRaw.NOTICE.txt'), 'SQLITEPCLRAW_NOTICE.txt'),
    @((Join-Path $nugetRoot 'libgit2sharp\0.31.0\App_Readme\LICENSE.md'), 'LIBGIT2SHARP_LICENSE.txt'),
    @((Join-Path $nugetRoot 'libgit2sharp.nativebinaries\2.0.323\libgit2\libgit2.license.txt'), 'LIBGIT2_LICENSE.txt'),
    @((Join-Path $panelRoot 'node_modules\react\LICENSE'), 'REACT_LICENSE.txt'),
    @((Join-Path $panelRoot 'node_modules\react-dom\LICENSE'), 'REACT_DOM_LICENSE.txt'),
    @((Join-Path $panelRoot 'node_modules\scheduler\LICENSE'), 'SCHEDULER_LICENSE.txt'),
    @((Join-Path $dotnetRoot 'ThirdPartyNotices.txt'), 'DOTNET_THIRD_PARTY_NOTICES.txt')
)
foreach ($copy in $legalCopies) {
    $source = $copy[0]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required third-party license was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $legalStage $copy[1])
}

$requiredFiles = @(
    'manifest.yml',
    'LICENSE',
    'NOTICE',
    'THIRD_PARTY_NOTICES',
    'legal\DIRECT_MIT_LICENSES.txt',
    'legal\CORDYCEPS_MIT_LICENSE.txt',
    'legal\LIBGIT2_LICENSE.txt',
    'legal\SCHEDULER_LICENSE.txt',
    'legal\DOTNET_THIRD_PARTY_NOTICES.txt',
    "$frameworkFolder\GPTino.Rhino.rhp",
    "$frameworkFolder\GPTino.Grasshopper.gha",
    "$frameworkFolder\agent\GPTino.AgentHost.exe",
    "$frameworkFolder\agent\GPTino.AgentHost.dll",
    "$frameworkFolder\agent\GPTino.AgentHost.deps.json",
    "$frameworkFolder\agent\GPTino.AgentHost.runtimeconfig.json",
    "$frameworkFolder\agent\GPTino.Terminal.exe",
    "$frameworkFolder\agent\wwwroot\index.html"
)
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $stageRoot $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Package validation failed; required file is missing: $relativePath"
    }
}

foreach ($dependencyFile in @('GPTino.Rhino.deps.json', 'GPTino.Grasshopper.deps.json')) {
    $dependencyPath = Join-Path $pluginStage $dependencyFile
    $dependencyManifest = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json
    $runtimeTarget = @($dependencyManifest.targets.PSObject.Properties)[0]
    if ($null -eq $runtimeTarget) {
        throw "Package dependency manifest has no runtime target: $dependencyFile"
    }

    $mismatchedProjects = @($runtimeTarget.Value.PSObject.Properties) | Where-Object {
        $_.Name -match '^GPTino\.[^/]+/(.+)$' -and
        -not [string]::Equals($Matches[1], $Version, [StringComparison]::Ordinal)
    }
    if ($mismatchedProjects) {
        throw "Package dependency versions do not match $Version in $dependencyFile`: $($mismatchedProjects.Name -join ', ')"
    }
}

$forbiddenNames = @('.mcp.json', 'auth.json', '.gptino-instance.lock')
$forbiddenExtensions = @('.pdb', '.map', '.db', '.db-shm', '.db-wal', '.secret', '.3dm', '.gh')
$forbidden = Get-ChildItem -LiteralPath $stageRoot -Recurse -File | Where-Object {
    $forbiddenNames -contains $_.Name -or $forbiddenExtensions -contains $_.Extension
}
if ($forbidden) {
    throw "Forbidden files entered the package: $($forbidden.FullName -join ', ')"
}

$contentsPath = Join-Path $OutputRoot "GPTino-$Version-yak-stage-contents.txt"
$contentsPath = Assert-GeneratedPath $contentsPath
$contents = Get-ChildItem -LiteralPath $stageRoot -Recurse -File |
    ForEach-Object { Get-RelativePackagePath $stageRoot $_.FullName } |
    Sort-Object
[IO.File]::WriteAllLines($contentsPath, $contents, [Text.UTF8Encoding]::new($false))

$archivePath = Join-Path $OutputRoot "GPTino-$Version-yak-stage.zip"
$archivePath = Assert-GeneratedPath $archivePath
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

if ($BuildYak) {
    if ([string]::IsNullOrWhiteSpace($YakExe)) {
        $YakExe = 'C:\Program Files\Rhino 8\System\Yak.exe'
    }
    $YakExe = [IO.Path]::GetFullPath($YakExe)
    if (-not (Test-Path -LiteralPath $YakExe -PathType Leaf)) {
        throw "Yak was requested but not found: $YakExe"
    }
    Invoke-Tool $YakExe @('build', '--platform', 'win') $stageRoot
    $yakPackages = @(Get-ChildItem -LiteralPath $stageRoot -Filter '*.yak' -File)
    if ($yakPackages.Count -ne 1) {
        throw "Yak completed but the staging directory contains $($yakPackages.Count) .yak files."
    }
    Write-Host "Installable Yak package: $($yakPackages[0].FullName)"
}

Write-Host ''
Write-Host "Validated Yak staging directory: $stageRoot"
Write-Host "Portable staging archive: $archivePath"
if (-not $BuildYak) {
    Write-Host 'Yak build was not requested; no installable .yak file was produced.'
}
