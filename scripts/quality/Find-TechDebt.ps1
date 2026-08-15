# Inventories tracked work markers and requires each one to name a
# ticket or tracked id, e.g. "TO DO(QB-142)" or "FIX ME(#88)" written
# without the space. Untracked markers fail the gate so debt cannot
# accumulate as anonymous comments.
[CmdletBinding()]
param(
    [string[]]$Path = @('src', 'tests', 'scripts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$marker = [regex]'(?i)\b(TODO|FIXME|HACK|XXX)\b(?!:)'
$tracked = [regex]'(?i)\b(TODO|FIXME|HACK|XXX)\s*\([^\n)]{2,}\)'
$trackedIds = [System.Collections.Generic.List[string]]::new()
$untracked = [System.Collections.Generic.List[string]]::new()

foreach ($relative in $Path) {
    $directory = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $directory)) { continue }

    Get-ChildItem -LiteralPath $directory -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|TestResults|coverage)\\' -and
            $_.Name -ne 'Find-TechDebt.ps1' -and
            $_.Extension -match '\.(cs|ps1|xaml|md|yml|yaml)$'
        } |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($root.Length).TrimStart('\', '/')
            $n = 0
            foreach ($line in Get-Content -LiteralPath $_.FullName) {
                $n++
                if (-not $marker.IsMatch($line)) { continue }
                $location = '{0}:{1}: {2}' -f $relativePath, $n, $line.Trim()
                if ($tracked.IsMatch($line)) {
                    $trackedIds.Add($location)
                }
                else {
                    $untracked.Add($location)
                }
            }
        }
}

Write-Host ("Tracked tech-debt markers: {0}" -f $trackedIds.Count)
$trackedIds | ForEach-Object { Write-Host "  $_" }

if ($untracked.Count -gt 0) {
    Write-Host "Untracked work markers must name a ticket, e.g. TO DO(QB-123) without the space:" -ForegroundColor Red
    $untracked | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "Tech-debt gate passed."
exit 0
