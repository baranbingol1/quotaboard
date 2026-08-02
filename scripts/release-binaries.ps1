# Single source of truth for project-owned PE files in a release publish tree.
function Get-QuotaBoardOwnedBinaryNames {
    @(
        'QuotaBoard.exe'
        'QuotaBoard.dll'
        'AiLimits.Application.dll'
        'AiLimits.Domain.dll'
        'AiLimits.Infrastructure.dll'
        'AiLimits.Platform.Windows.dll'
        'AiLimits.Presentation.WinUI.dll'
    )
}

function Get-QuotaBoardOwnedBinaryPaths {
    param([Parameter(Mandatory)][string]$RootPath)

    $root = [System.IO.Path]::GetFullPath($RootPath)
    $paths = @(Get-QuotaBoardOwnedBinaryNames | ForEach-Object {
        $path = Join-Path $root $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing project-owned release binary: $_"
        }
        $path
    })

    $versions = @($paths | ForEach-Object {
        $metadata = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($_)
        $relative = [System.IO.Path]::GetFileName($_)
        if ($metadata.ProductName -ne 'QuotaBoard') {
            throw "$relative has ProductName '$($metadata.ProductName)'; expected 'QuotaBoard'."
        }
        if ([string]::IsNullOrWhiteSpace($metadata.FileVersion) -or
            [string]::IsNullOrWhiteSpace($metadata.ProductVersion)) {
            throw "$relative is missing FileVersion or ProductVersion metadata."
        }
        "$($metadata.FileVersion)|$($metadata.ProductVersion)"
    } | Sort-Object -Unique)
    if ($versions.Count -ne 1) {
        throw "Project-owned release binaries do not share one FileVersion/ProductVersion: $($versions -join ', ')"
    }

    $paths
}
