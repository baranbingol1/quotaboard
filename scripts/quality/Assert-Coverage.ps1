# Merges Coverlet cobertura reports by normalized source file and line number,
# then fails when combined line coverage is under the threshold.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$ResultsDirectory,
    [double]$Minimum = 40
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$reports = @()
foreach ($directory in $ResultsDirectory) {
    if (-not (Test-Path -LiteralPath $directory)) {
        throw "Coverage results directory not found: $directory"
    }
    $reports += Get-ChildItem -LiteralPath $directory -Recurse -Filter coverage.cobertura.xml
}
if (-not $reports) {
    throw "No coverage.cobertura.xml found under the supplied result directories"
}

$lines = @{}
foreach ($report in $reports) {
    [xml]$coverage = Get-Content -LiteralPath $report.FullName
    if ($null -eq $coverage.coverage) { throw "Malformed Cobertura report: $($report.FullName)" }
    $sources = @($coverage.SelectNodes('//sources/source') | ForEach-Object { [string]$_.InnerText })
    foreach ($class in $coverage.SelectNodes('//class[@filename]')) {
        $file = [string]$class.filename
        if ($sources.Count -gt 0) {
            $separatorNormalizedFile = $file.Replace('\', [System.IO.Path]::DirectorySeparatorChar).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            $resolvedPaths = @($sources | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $_ $separatorNormalizedFile)) })
            $file = @($resolvedPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
            if ($file.Count -eq 0) { $file = $resolvedPaths[0] }
            else { $file = $file[0] }
        }
        $file = ([string]$file).Replace('\', '/').ToLowerInvariant()
        foreach ($line in $class.SelectNodes('./lines/line[@number]')) {
            $key = "$file`:$($line.number)"
            $hit = [int64]$line.hits -gt 0
            $lines[$key] = $hit -or ($lines.ContainsKey($key) -and $lines[$key])
        }
    }
}
if ($lines.Count -eq 0) { throw "Coverage reports contain no source lines" }
$linesCovered = @($lines.Values | Where-Object { $_ }).Count
$linesValid = $lines.Count
$percent = [math]::Round(100.0 * $linesCovered / $linesValid, 2)

$summary = [string]::Format(
    [System.Globalization.CultureInfo]::InvariantCulture,
    'Line coverage: {0}% ({1}/{2} lines). Floor: {3}%.',
    $percent,
    $linesCovered,
    $linesValid,
    $Minimum
)
Write-Host $summary

if ($percent -lt $Minimum) {
    Write-Host "Coverage gate failed." -ForegroundColor Red
    exit 1
}

Write-Host "Coverage gate passed."
exit 0
