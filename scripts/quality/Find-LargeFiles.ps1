# Fails when a tracked source file grows past the line or byte ceiling.
# Binary assets already live under assets/ and App Assets; those are excluded
# from the line check and instead have a larger byte ceiling so a screenshot
# cannot silently become a multi-megabyte blob.
[CmdletBinding()]
param(
    [int]$MaxSourceLines = 2200,
    [int]$MaxSourceBytes = 200KB,
    [int]$MaxBinaryBytes = 512KB
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failures = [System.Collections.Generic.List[string]]::new()

$sourceExtensions = '.cs', '.xaml', '.ps1', '.csproj', '.props', '.targets', '.yml', '.yaml', '.md', '.json'
$binaryExtensions = '.png', '.ico', '.ttf', '.otf'

function Test-Ignored([string]$fullName) {
    return $fullName -match '\\(bin|obj|\.git|TestResults|coverage|artifacts|CodexBar|\.dotnet-home)\\'
}

Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { -not (Test-Ignored $_.FullName) } |
    ForEach-Object {
        $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/')
        $ext = $_.Extension.ToLowerInvariant()

        if ($sourceExtensions -contains $ext) {
            $lines = (Get-Content -LiteralPath $_.FullName | Measure-Object -Line).Lines
            if ($lines -gt $MaxSourceLines) {
                $failures.Add(("{0}: {1} lines (limit {2})" -f $relative, $lines, $MaxSourceLines))
            }
            if ($_.Length -gt $MaxSourceBytes) {
                $failures.Add(("{0}: {1:N0} bytes (source limit {2:N0})" -f $relative, $_.Length, $MaxSourceBytes))
            }
        }
        elseif ($binaryExtensions -contains $ext -and $_.Length -gt $MaxBinaryBytes) {
            $failures.Add(("{0}: {1:N0} bytes (binary limit {2:N0})" -f $relative, $_.Length, $MaxBinaryBytes))
        }
    }

if ($failures.Count -gt 0) {
    Write-Host "Large-file gate failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "Large-file gate passed (source <= $MaxSourceLines lines / $MaxSourceBytes bytes, binaries <= $MaxBinaryBytes)."
exit 0
