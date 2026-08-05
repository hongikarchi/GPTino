#requires -Version 5.1
# Prunes the generated artifacts tree, which is gitignored and grows without bound: dev-loop runs
# never delete themselves (evidence preservation is deliberate) and the test suite leaves its
# temporary roots behind. Left alone this reached 26 GB / 318k files.
#
# Dry run by default. Nothing is removed without -Execute.
#
# What is KEPT, always:
#   yak/ publish/          the current build outputs
#   manual/ benchmarks/    hand-written evidence and recorded results
#   the N most recent REAL dev-loop runs (default 10)
#
# What is REMOVED:
#   older real dev-loop runs (timestamped names)
#   every test leftover under dev-loop, at any age - the suite writes these and nobody reads them
#   test-temp/, smoke-*/, invalid-dev-layout/
#
# A directory is only removed when it resolves inside artifacts/ and is not a reparse point, so a
# stray junction cannot turn this into a delete somewhere else.
[CmdletBinding()]
param(
    [int]$KeepRuns = 10,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $repo 'artifacts'
if (-not (Test-Path -LiteralPath $artifacts)) { Write-Host 'No artifacts directory.'; return }
$artifacts = (Resolve-Path -LiteralPath $artifacts).Path
$prefix = $artifacts.TrimEnd('\') + '\'

# A real dev-loop run is stamped by dev-loop.ps1 as yyyyMMddTHHmmssZ-<hex>; runs from before the
# naming settled carry milliseconds too (…T040606807Z-…), and those are the GB-sized ones that
# bundled a whole Yak build. Matching only the current shape labelled them "test leftover", which
# is the kind of wrong label someone approves a 23 GB delete against.
$realRunPattern = '^\d{8}T\d{6,9}Z-[0-9a-f]+$'
# Anything a human started by hand is kept regardless of age; the name is the only record of intent.
$manualPattern = '^manual-'

$targets = New-Object System.Collections.Generic.List[object]
function Add-Target($path, $reason) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to prune outside the artifacts tree: $full"
    }
    $item = Get-Item -LiteralPath $full -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Write-Warning "Skipping reparse point: $full"
        return
    }
    $files = @(Get-ChildItem -LiteralPath $full -Recurse -File -Force -ErrorAction SilentlyContinue)
    $targets.Add([pscustomobject]@{
            Path   = $full
            Reason = $reason
            MB     = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
            Files  = $files.Count
        })
}

$devLoop = Join-Path $artifacts 'dev-loop'
if (Test-Path -LiteralPath $devLoop) {
    $all = Get-ChildItem -LiteralPath $devLoop -Directory
    $real = @($all | Where-Object { $_.Name -match $realRunPattern } | Sort-Object LastWriteTime -Descending)
    $keep = @($real | Select-Object -First $KeepRuns | ForEach-Object { $_.FullName })
    foreach ($dir in $all) {
        if ($keep -contains $dir.FullName -or $dir.Name -match $manualPattern) { continue }
        $reason = if ($dir.Name -match $realRunPattern) { 'old dev-loop run' } else { 'test leftover' }
        Add-Target $dir.FullName $reason
    }
}
Add-Target (Join-Path $artifacts 'test-temp') 'test temp root'
Add-Target (Join-Path $artifacts 'invalid-dev-layout') 'test fixture root'
foreach ($smoke in Get-ChildItem -LiteralPath $artifacts -Directory -Filter 'smoke-*' -ErrorAction SilentlyContinue) {
    Add-Target $smoke.FullName 'smoke run'
}

$totalMB = ($targets | Measure-Object MB -Sum).Sum
$totalFiles = ($targets | Measure-Object Files -Sum).Sum
Write-Host ''
$targets | Group-Object Reason | Sort-Object { ($_.Group | Measure-Object MB -Sum).Sum } -Descending |
    ForEach-Object {
        '{0,-20} {1,6} dirs {2,10:N1} MB {3,10:N0} files' -f `
            $_.Name, $_.Count, ($_.Group | Measure-Object MB -Sum).Sum, ($_.Group | Measure-Object Files -Sum).Sum
    }
Write-Host ''
Write-Host ('TOTAL: {0:N0} directories, {1:N2} GB, {2:N0} files' -f $targets.Count, ($totalMB / 1024), $totalFiles)
if (Test-Path -LiteralPath $devLoop) {
    $kept = @(Get-ChildItem -LiteralPath $devLoop -Directory | Where-Object { $_.Name -match $realRunPattern } |
        Sort-Object LastWriteTime -Descending | Select-Object -First $KeepRuns)
    Write-Host ("KEEPING {0} most recent dev-loop run(s): {1}" -f $kept.Count, (($kept | ForEach-Object { $_.Name }) -join ', '))
}

if (-not $Execute) {
    Write-Host ''
    Write-Host 'Dry run. Re-run with -Execute to remove these.'
    return
}
$removed = 0
foreach ($target in $targets) {
    try {
        Remove-Item -LiteralPath $target.Path -Recurse -Force -ErrorAction Stop
        $removed++
    }
    catch {
        # A run whose Rhino still holds a handle is skipped, not fatal: the next prune gets it.
        Write-Warning "Could not remove $($target.Path): $($_.Exception.Message)"
    }
}
Write-Host ("Removed {0} of {1} directories." -f $removed, $targets.Count)
