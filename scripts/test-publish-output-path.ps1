# SPDX-License-Identifier: Apache-2.0
# Vets the publish output directory before publish-ai-limits.ps1 recursively
# deletes it. Throws on any violation; prints nothing and has no side effects,
# so it is safe to call directly when testing the rules.
[CmdletBinding()]
param(
    # Full, already-resolved path to the intended output directory.
    [Parameter(Mandatory = $true)]
    [string]$ResolvedOutput,

    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    # Explicit opt-in for directories outside the repo's app\ folder and the
    # temp directories. The hard refusals below still apply.
    [switch]$AllowExternalOutputPath
)

$ErrorActionPreference = 'Stop'

# True when $Path equals $Root or lives underneath it.
function Test-EqualOrUnder([string]$Path, [string]$Root) {
    $r = $Root.TrimEnd('\')
    return $Path.Equals($r, [StringComparison]::OrdinalIgnoreCase) -or
           $Path.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

$candidate = $ResolvedOutput.TrimEnd('\')
$repoFull = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')

# Hard refusals: apply even with -AllowExternalOutputPath.

if ($candidate -eq [System.IO.Path]::GetPathRoot($candidate).TrimEnd('\')) {
    throw "Refusing to use a drive root as -OutputPath: $ResolvedOutput"
}
if (Test-EqualOrUnder $repoFull $candidate) {
    throw "Refusing to delete the repository root or its ancestor: $ResolvedOutput"
}
if ((Test-EqualOrUnder $candidate $repoFull) -and
    -not (Test-EqualOrUnder $candidate (Join-Path $repoFull 'app'))) {
    throw "Refusing to delete a repository path outside app\: $ResolvedOutput"
}

$protectedRoots = @($env:USERPROFILE, $env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)
# Shell-known folders catch redirection and localization (e.g. an OneDrive
# Desktop); the literal profile-relative paths below catch the classic
# folders when the shell points elsewhere.
$protectedRoots += @('Desktop', 'MyDocuments', 'MyPictures', 'MyMusic', 'MyVideos',
    'ApplicationData', 'LocalApplicationData', 'ProgramFiles', 'ProgramFilesX86',
    'Windows', 'System') | ForEach-Object { [Environment]::GetFolderPath([Environment+SpecialFolder]::$_) }
$protectedRoots += @('Desktop', 'Documents', 'Downloads', 'Pictures', 'Music', 'Videos') |
    ForEach-Object { Join-Path $env:USERPROFILE $_ }

foreach ($protected in ($protectedRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
    if (Test-EqualOrUnder $protected $candidate) {
        throw "Refusing to delete a protected location ($protected): $ResolvedOutput"
    }
}

# Allowlist: without the switch, only scratch locations may be deleted.

if (-not $AllowExternalOutputPath) {
    $scratchRoots = @((Join-Path $repoFull 'app'))
    foreach ($temp in @($env:RUNNER_TEMP, $env:TMP, $env:TEMP)) {
        if (-not [string]::IsNullOrWhiteSpace($temp)) {
            $scratchRoots += [System.IO.Path]::GetFullPath($temp).TrimEnd('\')
        }
    }
    $isScratch = $false
    foreach ($scratch in $scratchRoots) {
        if (Test-EqualOrUnder $candidate $scratch) { $isScratch = $true; break }
    }
    if (-not $isScratch) {
        throw "-OutputPath '$ResolvedOutput' is outside the repo's app\ folder and the temp directories, and publishing deletes it recursively. Pass -AllowExternalOutputPath to confirm an external location."
    }
}
