# Parses a TRX file, prints the slowest tests, and fails when any test
# exceeds the duration ceiling. This is how we notice a suite that is
# quietly getting slower, not just one that is failing.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,
    [int]$Slowest = 15,
    [double]$MaxSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsDirectory)) {
    throw "Test results directory not found: $ResultsDirectory"
}
$trxFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter unit.trx)
if ($trxFiles.Count -ne 1) { throw "Expected exactly one unit.trx under $ResultsDirectory; found $($trxFiles.Count)" }
$TrxPath = $trxFiles[0].FullName

[xml]$trx = Get-Content -LiteralPath $TrxPath
$ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

$results = @()
foreach ($node in $trx.SelectNodes('//t:UnitTestResult', $ns)) {
    $duration = [TimeSpan]::Zero
    $durationText = $node.GetAttribute('duration')
    $startTime = $node.GetAttribute('startTime')
    $endTime = $node.GetAttribute('endTime')
    if ($durationText) {
        $duration = [TimeSpan]::Parse($durationText)
    }
    elseif ($startTime -and $endTime) {
        $duration = [DateTimeOffset]::Parse($endTime) - [DateTimeOffset]::Parse($startTime)
    }

    $results += [pscustomobject]@{
        Name     = $node.testName
        Outcome  = $node.outcome
        Seconds  = [math]::Round($duration.TotalSeconds, 3)
    }
}

if ($results.Count -eq 0) {
    throw "No unit test results found in $TrxPath"
}

Write-Host ("Recorded {0} test result(s). Slowest {1}:" -f $results.Count, $Slowest)
$results |
    Sort-Object Seconds -Descending |
    Select-Object -First $Slowest |
    ForEach-Object { Write-Host ("  {0,8:N3}s  {1}  {2}" -f $_.Seconds, $_.Outcome, $_.Name) }

$tooSlow = @($results | Where-Object { $_.Seconds -gt $MaxSeconds })
if ($tooSlow.Count -gt 0) {
    Write-Host ("Test duration gate failed ({0} test(s) over {1}s):" -f $tooSlow.Count, $MaxSeconds) -ForegroundColor Red
    $tooSlow | ForEach-Object { Write-Host ("  {0:N3}s  {1}" -f $_.Seconds, $_.Name) }
    exit 1
}

Write-Host "Test duration gate passed (ceiling ${MaxSeconds}s)."
exit 0
