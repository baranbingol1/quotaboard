# Fails when a central PackageVersion is never referenced, or when
# ReferenceTrimmer reports an unused PackageReference (RT0003).
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packagesProps = Join-Path $root 'Directory.Packages.props'
$failures = [System.Collections.Generic.List[string]]::new()

$catalog = [regex]::Matches(
    [System.IO.File]::ReadAllText($packagesProps),
    'PackageVersion\s+Include="([^"]+)"'
) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

$referenced = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase
)
Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object {
        $_.Extension -in '.csproj', '.props', '.targets' -and
        $_.FullName -notmatch '\\(bin|obj|\.git|\.agents|\.claude|CodexBar)\\'
    } |
    ForEach-Object {
        foreach ($match in [regex]::Matches(
                [System.IO.File]::ReadAllText($_.FullName),
                '<(?:PackageReference|GlobalPackageReference)\s+Include="([^"]+)"'
            )) {
            [void]$referenced.Add($match.Groups[1].Value)
        }
    }

foreach ($package in $catalog) {
    if (-not $referenced.Contains($package)) {
        $failures.Add("Directory.Packages.props pins $package but no project references it")
    }
}

if (-not $SkipBuild) {
    $projects = @(
        'src\AiLimits.App\AiLimits.App.csproj',
        'tests\AiLimits.Tests\AiLimits.Tests.csproj',
        'tests\AiLimits.IntegrationTests\AiLimits.IntegrationTests.csproj'
    )
    foreach ($relative in $projects) {
        $project = Join-Path $root $relative
        Write-Host "ReferenceTrimmer: $relative"
        $output = & dotnet build $project --configuration Release -p:EnableReferenceTrimmer=true -p:NuGetAudit=false
        $code = $LASTEXITCODE
        $hits = @($output | Where-Object { $_ -match '\bRT0003\b' })
        $output | ForEach-Object { Write-Host $_ }
        if ($code -ne 0 -and $hits.Count -eq 0) {
            $failures.Add("dotnet build failed for $relative (exit $code)")
        }
        foreach ($hit in $hits) {
            $failures.Add($hit.Trim())
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "Unused-package gate failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "Unused-package gate passed."
exit 0
