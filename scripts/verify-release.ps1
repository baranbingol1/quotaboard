[CmdletBinding()]
param(
    # Directory holding the assets re-downloaded from the published release.
    [Parameter(Mandatory)]
    [string]$DistDir,

    # When true (SignPath signing enabled), every project-owned binary must
    # chain to a trusted root. Existing vendor signatures are always checked;
    # unsigned vendor binaries are permitted and remain untouched.
    [bool]$RequireTrustedSignature = $false,

    # GitHub repository (owner/repo) for `gh attestation verify`. When set,
    # each release ZIP is verified against its build-provenance attestation.
    [string]$Repository,

    [string]$ExpectedSignerWorkflow,

    [string]$ExpectedSourceRef,

    [string]$ExpectedSourceDigest,

    [string]$ExpectedVersion,

    [string]$ExpectedSignerCertificateSha256
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

<#
Post-release verification.

Runs against assets re-downloaded from the published GitHub release, not the
files left on the build agent, so upload corruption or tampering is caught.
For every archive: the .sha256 sidecar must match, the full archive must pass
publish-layout and architecture validation, every project-owned PE must carry
an Authenticode signature, existing vendor signatures must remain valid, and
(when -Repository is set) build-provenance
attestation must verify.

SBOMs get the same treatment as archives, not a weaker structural one: an
SPDX asset must exist for every ZIP, match its own .sha256 sidecar, verify
against its build-provenance attestation, and parse as SPDX JSON with a
non-empty packages array naming QuotaBoard.exe. Structure alone would let a
stale or substituted document through, because any well-formed SPDX file
satisfies it. Any mismatch fails the run.
#>

# Both archives and SBOMs ship with a sidecar holding "<sha256>  <filename>".
function Assert-Sha256Sidecar {
    param([Parameter(Mandatory)][System.IO.FileInfo]$Asset)

    $sidecarPath = "$($Asset.FullName).sha256"
    if (-not (Test-Path -LiteralPath $sidecarPath)) {
        throw "Missing sha256 sidecar for $($Asset.Name)"
    }
    $expected = ((Get-Content -LiteralPath $sidecarPath -Raw).Trim() -split '\s+', 2)[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $Asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA256 mismatch for $($Asset.Name): sidecar says $expected, download hashes to $actual"
    }
}

# `gh attestation verify` checks the in-toto statement against the repository's
# signing origin; a non-zero exit means the asset's provenance cannot be
# verified (missing, tampered, or from a different repo).
function Assert-Attestation {
    param(
        [Parameter(Mandatory)][System.IO.FileInfo]$Asset,
        [string]$Repo,
        [string]$SignerWorkflow,
        [string]$SourceRef,
        [string]$SourceDigest
    )

    if (-not $Repo) {
        return
    }
    Write-Host "Verifying attestation for $($Asset.Name)..."
    & gh attestation verify $Asset.FullName --repo $Repo `
        --signer-workflow $SignerWorkflow `
        --source-ref $SourceRef `
        --source-digest $SourceDigest 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "$($Asset.Name): attestation verification failed (exit $LASTEXITCODE)"
    }
    Write-Host "$($Asset.Name): attestation ok"
}

$resolvedDistDir = [System.IO.Path]::GetFullPath($DistDir)
if (-not (Test-Path -LiteralPath $resolvedDistDir)) {
    throw "Asset directory does not exist: $resolvedDistDir"
}
$missingAttestationIdentity = [string]::IsNullOrWhiteSpace($ExpectedSignerWorkflow) `
    -or [string]::IsNullOrWhiteSpace($ExpectedSourceRef) `
    -or [string]::IsNullOrWhiteSpace($ExpectedSourceDigest)
if ($Repository -and $missingAttestationIdentity) {
    throw 'Attestation verification requires the expected signer workflow, source ref, and source digest.'
}
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    throw 'Release verification requires -ExpectedVersion.'
}

$expectedAssets = @(
    foreach ($architecture in 'x64', 'arm64') {
        $baseName = "QuotaBoard-$ExpectedVersion-win-$architecture"
        "$baseName.zip"
        "$baseName.zip.sha256"
        "$baseName.sbom.spdx.json"
        "$baseName.sbom.spdx.json.sha256"
    }
) | Sort-Object
$actualAssets = @(Get-ChildItem -LiteralPath $resolvedDistDir -File | Select-Object -ExpandProperty Name | Sort-Object)
$assetDifference = @(Compare-Object $expectedAssets $actualAssets)
if ($assetDifference.Count -gt 0) {
    $detail = ($assetDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join "`n"
    throw "Release asset inventory does not exactly match version ${ExpectedVersion}:`n$detail"
}

$zips = Get-ChildItem -LiteralPath $resolvedDistDir -Filter '*.zip' -File
if (-not $zips) {
    throw "No .zip assets found in $resolvedDistDir"
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

foreach ($zip in $zips) {
    Assert-Sha256Sidecar -Asset $zip

    if ($zip.Name -notmatch '(?i)-win-(x64|arm64)\.zip$') {
        throw "$($zip.Name): cannot infer architecture; expected a -win-x64.zip or -win-arm64.zip suffix"
    }
    $architecture = $Matches[1].ToLowerInvariant()
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "QuotaBoard-verify-$([guid]::NewGuid())"
    try {
        New-Item -ItemType Directory -Path $tempDir | Out-Null
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
        try {
            if ($archive.Entries.Count -eq 0) {
                throw "$($zip.Name) contains no files"
            }
            $entryCount = $archive.Entries.Count
            [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $tempDir)
        }
        finally {
            $archive.Dispose()
        }

        & (Join-Path $scriptDir 'validate-publish.ps1') -Architecture $architecture -OutputPath $tempDir
        if ($LASTEXITCODE -ne 0) {
            throw "$($zip.Name): publish validation failed (exit $LASTEXITCODE)"
        }
        & (Join-Path $scriptDir 'verify-signatures.ps1') `
            -RootPath $tempDir `
            -RequireTrustedSignature $RequireTrustedSignature `
            -ExpectedSignerCertificateSha256 $ExpectedSignerCertificateSha256
        if ($LASTEXITCODE -ne 0) {
            throw "$($zip.Name): signature verification failed (exit $LASTEXITCODE)"
        }
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "$($zip.Name): sha256 ok, $entryCount entries, signature ok"

    Assert-Attestation -Asset $zip -Repo $Repository `
        -SignerWorkflow $ExpectedSignerWorkflow `
        -SourceRef $ExpectedSourceRef `
        -SourceDigest $ExpectedSourceDigest
}

# --- SBOM validation -------------------------------------------------------
# Every release ZIP must ship a matching SPDX SBOM (same base name, .sbom.spdx.json
# instead of .zip). Zero SBOMs is a failure: the build is expected to publish them.

$sboms = Get-ChildItem -LiteralPath $resolvedDistDir -Filter '*.spdx.json' -File
if (-not $sboms) {
    throw "No SPDX SBOM assets found in $resolvedDistDir; expected one per release ZIP"
}

# Build a map of ZIP base names to their SBOM counterparts.
$zipBases = $zips | ForEach-Object {
    [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
}

foreach ($zipBase in $zipBases) {
    $expectedSbom = "$zipBase.sbom.spdx.json"
    $matchingSbom = $sboms | Where-Object { $_.Name -eq $expectedSbom } | Select-Object -First 1
    if (-not $matchingSbom) {
        throw "No SBOM matching $zipBase.zip (expected $expectedSbom)"
    }
}

foreach ($sbom in $sboms) {
    # Hash and provenance first: without them the structural checks below only
    # prove the document is well-formed SPDX, not that it came from this build.
    Assert-Sha256Sidecar -Asset $sbom
    Assert-Attestation -Asset $sbom -Repo $Repository `
        -SignerWorkflow $ExpectedSignerWorkflow `
        -SourceRef $ExpectedSourceRef `
        -SourceDigest $ExpectedSourceDigest

    $raw = Get-Content -LiteralPath $sbom.FullName -Raw
    try {
        $doc = $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$($sbom.Name) is not valid JSON: $_"
    }

    if (-not $doc.PSObject.Properties.Name) {
        throw "$($sbom.Name) is an empty JSON object"
    }

    # Minimal SPDX shape: must declare an SPDX version and carry a non-empty
    # packages array. sbom-tool always emits both.
    if (-not $doc.spdxVersion) {
        throw "$($sbom.Name) missing required field 'spdxVersion'"
    }
    $packages = @($doc.packages)
    if ($packages.Count -eq 0) {
        throw "$($sbom.Name) has an empty 'packages' array"
    }

    # The SBOM must describe the same executable the ZIP ships.
    $hasExe = $packages | Where-Object {
        $_.name -eq 'QuotaBoard' -or $_.name -eq 'QuotaBoard.exe'
    }
    if (-not $hasExe) {
        throw "$($sbom.Name) does not list QuotaBoard/QuotaBoard.exe in its packages"
    }

    Write-Host "$($sbom.Name): sha256 ok, SPDX $($doc.spdxVersion), $($packages.Count) packages, QuotaBoard.exe present"
}

Write-Host "All release assets verified."
