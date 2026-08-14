# Reads Coverlet cobertura output and fails when line coverage is under the
# threshold. Agents must keep the suite above this floor, not merely produce
# a coverage file.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,
    [int]$Minimum = 40
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
    throw "Coverage results directory not found: $ResultsDirectory"
}

$reports = Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.cobertura.xml
if (-not $reports) {
    throw "No coverage.cobertura.xml found under $ResultsDirectory"
}

[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName
$rate = [double]$coverage.coverage.'line-rate'
$percent = [math]::Round($rate * 100, 2)
$linesCovered = $coverage.coverage.'lines-covered'
$linesValid = $coverage.coverage.'lines-valid'

Write-Host ("Line coverage: {0}% ({1}/{2} lines). Floor: {3}%." -f $percent, $linesCovered, $linesValid, $Minimum)

if ($percent -lt $Minimum) {
    Write-Host "Coverage gate failed." -ForegroundColor Red
    exit 1
}

Write-Host "Coverage gate passed."
exit 0
