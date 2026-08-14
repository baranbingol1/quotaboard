# Token-window duplicate detector. Consecutive non-trivial C# lines are
# hashed in sliding windows; any window that appears in two files (or twice
# in one file, far enough apart) fails the gate. This is deliberately
# conservative so generated usings and short property blocks do not trip it.
[CmdletBinding()]
param(
    [int]$Window = 20,
    [string[]]$Path = @('src')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ignore = [regex]'^\s*(//|using\s|namespace\s|\{|\}|#|\[)'
$index = @{}

function Normalize([string]$line) {
    return ($line -replace '\s+', ' ').Trim()
}

foreach ($relative in $Path) {
    $directory = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $directory)) { continue }

    Get-ChildItem -LiteralPath $directory -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($root.Length).TrimStart('\', '/')
            $kept = [System.Collections.Generic.List[string]]::new()
            $map = [System.Collections.Generic.List[int]]::new()
            $n = 0
            foreach ($line in Get-Content -LiteralPath $_.FullName) {
                $n++
                $normalized = Normalize $line
                if ([string]::IsNullOrWhiteSpace($normalized) -or $ignore.IsMatch($normalized)) {
                    continue
                }
                if ($normalized.Length -lt 12) { continue }
                $kept.Add($normalized)
                $map.Add($n)
            }

            if ($kept.Count -lt $Window) { return }

            for ($i = 0; $i -le $kept.Count - $Window; $i++) {
                $chunk = ($kept.GetRange($i, $Window) -join "`n")
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($chunk))
                    $hash = ([BitConverter]::ToString($bytes)).Replace('-', '').Substring(0, 16)
                }
                finally {
                    $sha.Dispose()
                }
                $hit = [pscustomobject]@{
                    File = $relativePath
                    Line = $map[$i]
                    Hash = $hash
                }
                if (-not $index.ContainsKey($hash)) {
                    $index[$hash] = [System.Collections.Generic.List[object]]::new()
                }
                $index[$hash].Add($hit)
            }
        }
}

$failures = New-Object System.Collections.Generic.List[string]
foreach ($entry in $index.GetEnumerator()) {
    $hits = $entry.Value
    if ($hits.Count -lt 2) { continue }

    $distinct = @($hits | Sort-Object File, Line | Group-Object File)
    $isCrossFile = $distinct.Count -gt 1
    $isFarApart = $false
    if (-not $isCrossFile) {
        $lines = @($hits | ForEach-Object { $_.Line } | Sort-Object)
        $isFarApart = ($lines[-1] - $lines[0]) -ge ($Window * 2)
    }

    if ($isCrossFile -or $isFarApart) {
        $where = (@($hits | Select-Object -First 4 | ForEach-Object { '{0}:{1}' -f $_.File, $_.Line })) -join ', '
        $failures.Add("duplicate $Window-line window at $where")
    }
}

$unique = @($failures | Select-Object -Unique)
if ($unique.Count -gt 0) {
    Write-Host "Duplicate-code gate failed ($($unique.Count) window(s)):" -ForegroundColor Red
    $unique | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "Duplicate-code gate passed (window $Window)."
exit 0
