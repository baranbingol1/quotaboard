[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Default resolution order if the caller did not pass -OutputPath:
    #   1. Inside CI (CI=true / GITHUB_ACTIONS=true / RUNNER_TEMP set):
    #      publish into the runner's ephemeral temp folder so the workspace
    #      stays clean.
    #   2. Local development: canonical hand-launchable install at
    #      <repoRoot>\app\win-<arch>\.
    # Explicit -OutputPath always wins, but the target is recursively deleted
    # before publishing, so test-publish-output-path.ps1 vets it: scratch
    # locations (repo app\, temp dirs) pass, anything else needs
    # -AllowExternalOutputPath, and some locations are always refused.
    [string]$OutputPath,

    [string]$PackageOutputPath,

    [switch]$AllowExternalOutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\AiLimits.App\AiLimits.App.csproj'
$runtimeIdentifier = "win-$Architecture"
$targetFramework = 'net10.0-windows10.0.19041.0'
$isCi = ($env:CI -eq 'true') -or ($env:GITHUB_ACTIONS -eq 'true') -or [bool]$env:RUNNER_TEMP

if (-not $OutputPath) {
    if ($isCi) {
        $tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
        $OutputPath = Join-Path $tempRoot "AiLimits\release\win-$Architecture"
    } else {
        $OutputPath = Join-Path $repositoryRoot "app\win-$Architecture"
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)

# The output directory is recursively deleted below; vet it first.
& "$PSScriptRoot\test-publish-output-path.ps1" `
    -ResolvedOutput $resolvedOutput `
    -RepositoryRoot $repositoryRoot `
    -AllowExternalOutputPath:$AllowExternalOutputPath

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '.NET 10 SDK was not found on PATH.'
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

& $dotnet.Source publish $projectPath `
    --configuration $Configuration `
    --runtime $runtimeIdentifier `
    --self-contained true `
    --output $resolvedOutput `
    -p:Platform=$Architecture `
    -p:PublishProfile= `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    throw "AI Limits publish failed with exit code $LASTEXITCODE."
}

$packageAssetsPath = Join-Path $repositoryRoot 'src\AiLimits.App\obj\project.assets.json'
if (-not (Test-Path -LiteralPath $packageAssetsPath -PathType Leaf)) {
    throw "Package assets were not generated: $packageAssetsPath"
}

$packageAssets = Get-Content -Raw -LiteralPath $packageAssetsPath | ConvertFrom-Json
$packageRoots = @($packageAssets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })
$licenseOutput = Join-Path $resolvedOutput 'Licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null

function Copy-PackageNotice {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$OutputName
    )

    $package = @(
        $packageAssets.libraries.PSObject.Properties |
            Where-Object { $_.Name -match ('^' + [regex]::Escape($PackageId) + '/[^/]+$') }
    )
    if ($package.Count -ne 1) {
        throw "Could not resolve exactly one $PackageId package from project.assets.json."
    }

    $sourcePath = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot (Join-Path ([string]$package[0].Value.path) $RelativePath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $sourcePath = $candidate
            break
        }
    }
    if (-not $sourcePath) {
        throw "Missing notice file for $PackageId package: $RelativePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $licenseOutput $OutputName) -Force
}

$packageNotices = @(
    @{ PackageId = 'CommunityToolkit.Mvvm'; RelativePath = 'License.md'; OutputName = 'CommunityToolkit-Mvvm-LICENSE.md' },
    @{ PackageId = 'CommunityToolkit.Mvvm'; RelativePath = 'ThirdPartyNotices.txt'; OutputName = 'CommunityToolkit-Mvvm-ThirdPartyNotices.txt' },
    @{ PackageId = 'Microsoft.Web.WebView2'; RelativePath = 'LICENSE.txt'; OutputName = 'Microsoft-WebView2-LICENSE.txt' },
    @{ PackageId = 'Microsoft.Web.WebView2'; RelativePath = 'NOTICE.txt'; OutputName = 'Microsoft-WebView2-NOTICE.txt' },
    @{ PackageId = 'Microsoft.WindowsAppSDK'; RelativePath = 'license.txt'; OutputName = 'Microsoft-Windows-App-SDK-LICENSE.txt' },
    @{ PackageId = 'Microsoft.WindowsAppSDK.ML'; RelativePath = 'ThirdPartyNotices.txt'; OutputName = 'Microsoft-Windows-App-SDK-ML-ThirdPartyNotices.txt' },
    @{ PackageId = 'SourceGear.sqlite3'; RelativePath = 'LICENSE.txt'; OutputName = 'SourceGear-SQLite-LICENSE.txt' }
)
foreach ($notice in $packageNotices) {
    Copy-PackageNotice @notice
}

$dotnetNoticePath = Join-Path (Split-Path $dotnet.Source -Parent) 'ThirdPartyNotices.txt'
if (-not (Test-Path -LiteralPath $dotnetNoticePath -PathType Leaf)) {
    throw ".NET third-party notices were not found: $dotnetNoticePath"
}
Copy-Item -LiteralPath $dotnetNoticePath -Destination (Join-Path $licenseOutput 'dotnet-ThirdPartyNotices.txt') -Force

# WinUI's unpackaged publish currently omits the executable project's PRI/XBF
# layout. Copy the compiled XAML layout from the RID build output explicitly.
$layoutRoot = Join-Path $repositoryRoot "src\AiLimits.App\bin\$Architecture\$Configuration\$targetFramework\$runtimeIdentifier"
$requiredRootFiles = @('QuotaBoard.pri', 'App.xbf', 'MainWindow.xbf')
foreach ($name in $requiredRootFiles) {
    $source = Join-Path $layoutRoot $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required WinUI layout artifact is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $resolvedOutput $name) -Force
}

$presentationLayout = Join-Path $layoutRoot 'AiLimits.Presentation.WinUI'
if (-not (Test-Path -LiteralPath $presentationLayout)) {
    throw "Presentation XAML layout is missing: $presentationLayout"
}
Copy-Item -LiteralPath $presentationLayout -Destination $resolvedOutput -Recurse -Force

$executable = Join-Path $resolvedOutput 'QuotaBoard.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publish completed without the expected executable: $executable"
}

Write-Host "Self-contained AI Limits build: $executable"

& "$PSScriptRoot\validate-publish.ps1" -Architecture $Architecture -OutputPath $resolvedOutput
if ($LASTEXITCODE -ne 0) {
    throw 'Raw publish validation failed.'
}

if ($PackageOutputPath) {
    $resolvedPackageOutput = [System.IO.Path]::GetFullPath($PackageOutputPath)
    & "$PSScriptRoot\test-publish-output-path.ps1" `
        -ResolvedOutput $resolvedPackageOutput `
        -RepositoryRoot $repositoryRoot `
        -AllowExternalOutputPath:$AllowExternalOutputPath
    if (Test-Path -LiteralPath $resolvedPackageOutput) {
        Remove-Item -LiteralPath $resolvedPackageOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedPackageOutput -Force | Out-Null

    [xml]$buildProps = Get-Content -Raw (Join-Path $repositoryRoot 'Directory.Build.props')
    $version = [string]$buildProps.Project.PropertyGroup.Version
    $channel = "win-$Architecture"
    & $dotnet.Source vpk pack `
        --packId QuotaBoard `
        --packVersion $version `
        --packDir $resolvedOutput `
        --mainExe QuotaBoard.exe `
        --packTitle QuotaBoard `
        --packAuthors baranbingol1 `
        --icon (Join-Path $repositoryRoot 'src\AiLimits.App\Assets\QuotaBoard.ico') `
        --runtime $runtimeIdentifier `
        --channel $channel `
        --outputDir $resolvedPackageOutput `
        --noInst true `
        --delta None
    if ($LASTEXITCODE -ne 0) {
        throw "Velopack packaging failed with exit code $LASTEXITCODE."
    }

    [array]$portableFiles = Get-ChildItem -LiteralPath $resolvedPackageOutput -Filter '*Portable.zip'
    [array]$fullPackages = Get-ChildItem -LiteralPath $resolvedPackageOutput -Filter '*-full.nupkg'
    if ($portableFiles.Count -ne 1 -or $fullPackages.Count -ne 1) {
        throw 'Velopack did not create exactly one portable ZIP and one full package.'
    }
    $portable = $portableFiles[0]
    $fullPackage = $fullPackages[0]
    $portableName = "QuotaBoard-$version-$channel.zip"
    $packageName = "QuotaBoard-$version-$channel-full.nupkg"
    Move-Item -LiteralPath $portable.FullName -Destination (Join-Path $resolvedPackageOutput $portableName) -Force
    Move-Item -LiteralPath $fullPackage.FullName -Destination (Join-Path $resolvedPackageOutput $packageName) -Force

    & "$PSScriptRoot\validate-portable-package.ps1" `
        -Architecture $Architecture `
        -ArchivePath (Join-Path $resolvedPackageOutput $portableName) `
        -Version $version
    if ($LASTEXITCODE -ne 0) {
        throw 'Portable package validation failed.'
    }
}
