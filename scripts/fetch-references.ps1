[CmdletBinding()]
param([switch]$Refresh)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $repoRoot 'references\sources.lock.json'
$targetRoot = Join-Path $repoRoot '.references'
$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json

New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null

foreach ($source in $lock.sources) {
    $target = Join-Path $targetRoot $source.name
    if (-not (Test-Path -LiteralPath $target)) {
        git clone --filter=blob:none --no-checkout $source.repository $target
        if ($LASTEXITCODE -ne 0) { throw "clone failed: $($source.name)" }
    }
    elseif ($Refresh) {
        git -C $target fetch --filter=blob:none origin $source.commit
        if ($LASTEXITCODE -ne 0) { throw "fetch failed: $($source.name)" }
    }

    git -C $target -c advice.detachedHead=false checkout --detach $source.commit
    if ($LASTEXITCODE -ne 0) { throw "checkout failed: $($source.name)@$($source.commit)" }

    $actual = git -C $target rev-parse HEAD
    if ($actual.Trim() -ne $source.commit) {
        throw "pin mismatch: $($source.name) expected $($source.commit), got $actual"
    }
    Write-Host "ready $($source.name)@$($source.commit.Substring(0, 8))"
}
