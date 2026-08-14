# Conservative cyclomatic-complexity estimate for C# methods. Decision
# points (if / else if / for / foreach / while / case / catch / && / || /
# ?? / ternary) plus one for the method itself. Class, record, and
# interface declarations are ignored. CI fails the build when any method
# exceeds the threshold so new hotspots cannot land unnoticed. This estimator
# is independent from Roslyn CA1502 but shares its numeric policy.
[CmdletBinding()]
param(
    [int]$Threshold = 40,
    [string[]]$Path = @('src')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$decision = [regex]'(?x)
    \b(if|else\s+if|for|foreach|while|case|catch)\b
    | \?\?
    | \&\&
    | \|\|
    | (?<![?\w])\?(?![?.\w])
'
$methodStart = [regex]'(?x)
    ^\s*
    (?:(?:public|internal|protected|private|static|async|partial|override|virtual|sealed|extern|unsafe|new|readonly|required)\s+)+
    (?!class\b|struct\b|record\b|enum\b|interface\b|delegate\b)
    [\w.<>,\[\]?]+\s+
    (?<name>\w+)\s*\(
'

$failures = New-Object System.Collections.Generic.List[string]

foreach ($relative in $Path) {
    $directory = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $directory)) { continue }

    Get-ChildItem -LiteralPath $directory -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $lines = Get-Content -LiteralPath $_.FullName
            $current = $null
            $depth = 0
            $decisions = 0
            $started = $false
            $seenBody = $false

            for ($i = 0; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                $trimmed = $line.Trim()
                if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('///') -or $trimmed.StartsWith('[')) {
                    continue
                }

                if (-not $started -and $methodStart.IsMatch($line)) {
                    $current = $methodStart.Match($line).Groups['name'].Value
                    $decisions = 1
                    $depth = 0
                    $started = $true
                    $seenBody = $false
                }

                if (-not $started) { continue }

                $decisions += $decision.Matches($line).Count
                $opens = ([regex]::Matches($line, '\{')).Count
                $closes = ([regex]::Matches($line, '\}')).Count
                $depth += $opens
                $depth -= $closes
                if ($opens -gt 0) { $seenBody = $true }

                if ($started -and $seenBody -and $depth -le 0) {
                    if ($decisions -gt $Threshold) {
                        $relativePath = $_.FullName.Substring($root.Length).TrimStart('\', '/')
                        $failures.Add(('{0}:{1} {2} complexity={3} (limit {4})' -f $relativePath, ($i + 1), $current, $decisions, $Threshold))
                    }
                    $started = $false
                    $current = $null
                }
            }
        }
}

if ($failures.Count -gt 0) {
    Write-Host "Cyclomatic complexity gate failed ($($failures.Count) method(s) over $Threshold):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "Cyclomatic complexity gate passed (threshold $Threshold)."
exit 0
