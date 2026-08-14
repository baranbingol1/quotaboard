# Runs the local quality gates that CI also runs. -Quick skips the slower
# duplicate scan window expansion and is what the pre-commit hook uses.
[CmdletBinding()]
param(
    [switch]$Quick,
    [switch]$SkipFormat
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

function Invoke-Gate {
    param(
        [string]$Name,
        [scriptblock]$Body
    )
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Quality gate failed: $Name"
    }
}

if (-not $SkipFormat) {
    Invoke-Gate 'CSharpier format check' {
        dotnet tool restore --verbosity quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet csharpier check src tests
    }
}

Invoke-Gate 'Large files' { & "$PSScriptRoot\quality\Find-LargeFiles.ps1" }
Invoke-Gate 'Cyclomatic complexity' { & "$PSScriptRoot\quality\Measure-Complexity.ps1" }
Invoke-Gate 'Tech-debt markers' { & "$PSScriptRoot\quality\Find-TechDebt.ps1" }

if (-not $Quick) {
    Invoke-Gate 'Duplicate code' { & "$PSScriptRoot\quality\Find-DuplicateCode.ps1" }
}

Write-Host ""
Write-Host "All requested quality gates passed." -ForegroundColor Green
