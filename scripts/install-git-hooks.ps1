# Points this clone at .githooks/ so pre-commit runs the quality gates.
# Safe to re-run. Does not change any other git configuration.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hooks = Join-Path $root '.githooks'
if (-not (Test-Path -LiteralPath (Join-Path $hooks 'pre-commit'))) {
    throw "Missing $hooks\pre-commit"
}

Push-Location $root
try {
    git config --local core.hooksPath .githooks
    Write-Host "Installed local git hooks from .githooks (core.hooksPath)."
    Write-Host "The pre-commit hook runs scripts/invoke-quality-gates.ps1 -Quick."
}
finally {
    Pop-Location
}
