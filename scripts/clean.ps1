[CmdletBinding()]
param(
    # Repository root: inferred from $PSScriptRoot one level up, override for
    # uses that cannot rely on the script's own location.
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    # Keep the runtime install (`app/`) and only drop build scratch. Useful
    # when you want to invalidate `bin/`/`obj/` without re-publishing (rare).
    [switch]$KeepApp
)

$ErrorActionPreference = 'Stop'

Write-Host "Cleaning build scratch and runtime install under $RepositoryRoot"

# MSBuild scratch directly under every project. Depth 2 covers
# src/<Project>/bin and src/<Project>/obj; tests/<TestProject>/bin|obj.
function Remove-IfExists([string]$path) {
    if (Test-Path -LiteralPath $path) {
        Write-Host "Removing: $path"
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

Get-ChildItem -Path $RepositoryRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'src', 'tests' } |
    ForEach-Object {
        Get-ChildItem -Path $_.FullName -Directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                Remove-IfExists (Join-Path $_.FullName 'bin')
                Remove-IfExists (Join-Path $_.FullName 'obj')
            }
    }

if (-not $KeepApp) {
    Remove-IfExists (Join-Path $RepositoryRoot 'app')
}

Write-Host "Done. Next 'dotnet build' regenerates bin/obj; 'scripts/publish-ai-limits.ps1' repopulates app/."
