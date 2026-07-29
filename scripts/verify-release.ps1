[CmdletBinding()]
param(
    # Directory holding the assets re-downloaded from the published release.
    [Parameter(Mandatory)]
    [string]$DistDir,

    # When true (SignPath signing enabled), QuotaBoard.exe must chain to a
    # trusted root. When false, a self-signed test signature is accepted as
    # long as a signature is actually present and the digest verifies.
    [bool]$RequireTrustedSignature = $false,

    # GitHub repository (owner/repo) for `gh attestation verify`. When set,
    # each release ZIP is verified against its build-provenance attestation.
    [string]$Repository
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

<#
Post-release verification.

Runs against assets re-downloaded from the published GitHub release, not the
files left on the build agent, so upload corruption or tampering is caught.
For every archive: the .sha256 sidecar must match, the zip must be non-empty
and contain QuotaBoard.exe, the executable must carry an Authenticode
signature, and (when -Repository is set) the archive's build-provenance
attestation must verify. SPDX SBOM assets must exist for every ZIP, parse as
valid SPDX JSON with a non-empty packages array, and mention QuotaBoard.exe.
Any mismatch fails the run.
#>

function Resolve-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $kits = Get-ChildItem $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.' } |
        Sort-Object { [version]$_.Name } -Descending
    foreach ($kit in $kits) {
        $candidate = Join-Path $kit.FullName 'x64\signtool.exe'
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw 'signtool.exe was not found. Install the Windows SDK signing tools.'
}

$resolvedDistDir = [System.IO.Path]::GetFullPath($DistDir)
if (-not (Test-Path -LiteralPath $resolvedDistDir)) {
    throw "Asset directory does not exist: $resolvedDistDir"
}

$zips = Get-ChildItem -LiteralPath $resolvedDistDir -Filter '*.zip' -File
if (-not $zips) {
    throw "No .zip assets found in $resolvedDistDir"
}

$signTool = Resolve-SignTool

foreach ($zip in $zips) {
    $sidecarPath = "$($zip.FullName).sha256"
    if (-not (Test-Path -LiteralPath $sidecarPath)) {
        throw "Missing sha256 sidecar for $($zip.Name)"
    }

    $expected = ((Get-Content -LiteralPath $sidecarPath -Raw).Trim() -split '\s+', 2)[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA256 mismatch for $($zip.Name): sidecar says $expected, download hashes to $actual"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
    $tempExe = $null
    try {
        if ($archive.Entries.Count -eq 0) {
            throw "$($zip.Name) contains no files"
        }
        $entryCount = $archive.Entries.Count
        $exeEntry = $archive.Entries | Where-Object { $_.FullName -eq 'QuotaBoard.exe' } | Select-Object -First 1
        if (-not $exeEntry) {
            throw "$($zip.Name) does not contain QuotaBoard.exe"
        }
        $tempExe = Join-Path ([System.IO.Path]::GetTempPath()) "QuotaBoard-verify-$([guid]::NewGuid()).exe"
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($exeEntry, $tempExe)
    }
    finally {
        $archive.Dispose()
    }

    try {
        $signature = Get-AuthenticodeSignature -FilePath $tempExe
        if (-not $signature.SignerCertificate) {
            throw "$($zip.Name): QuotaBoard.exe carries no Authenticode signature"
        }

        $verifyOutput = & $signTool verify /pa $tempExe 2>&1
        if ($LASTEXITCODE -ne 0) {
            $verifyText = ($verifyOutput | Out-String).Trim()
            $untrustedRoot = $verifyText -match 'terminated in a root certificate which is not trusted'
            if ($RequireTrustedSignature -or -not $untrustedRoot) {
                throw "$($zip.Name): signtool verify failed: $verifyText"
            }
            Write-Warning "$($zip.Name): QuotaBoard.exe signature verifies but chains to an untrusted test root (expected for self-signed CI builds)."
        }
    }
    finally {
        if ($tempExe) {
            Remove-Item -LiteralPath $tempExe -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "$($zip.Name): sha256 ok, $entryCount entries, signature ok"

    # Verify the build-provenance attestation that the build job created for
    # this archive. `gh attestation verify` checks the in-toto statement
    # against the repository's signing origin; a non-zero exit means the
    # archive's provenance cannot be verified (missing, tampered, or from a
    # different repo).
    if ($Repository) {
        Write-Host "Verifying attestation for $($zip.Name)..."
        & gh attestation verify $zip.FullName --repo $Repository 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            throw "$($zip.Name): attestation verification failed (exit $LASTEXITCODE)"
        }
        Write-Host "$($zip.Name): attestation ok"
    }
}

# --- SBOM validation -------------------------------------------------------
# Every release ZIP must ship a matching SPDX SBOM (same base name, .sbom.spdx.json
# instead of .zip). Zero SBOMs is a failure: the build is expected to publish them.

$sboms = Get-ChildItem -LiteralPath $resolvedDistDir -Filter '*.spdx.json' -File
if (-not $sboms) {
    throw "No SPDX SBOM assets found in $resolvedDistDir — expected one per release ZIP"
}

# Build a map of ZIP base names to their SBOM counterparts.
$zipBases = $zips | ForEach-Object {
    [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
}

foreach ($zipBase in $zipBases) {
    $matchingSbom = $sboms | Where-Object {
        (($_.BaseName -replace '\.sbom$', '') -eq $zipBase)
    } | Select-Object -First 1
    if (-not $matchingSbom) {
        throw "No SBOM matching $zipBase.zip (expected $zipBase.sbom.spdx.json)"
    }
}

foreach ($sbom in $sboms) {
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

    Write-Host "$($sbom.Name): SPDX $($doc.spdxVersion), $($packages.Count) packages, QuotaBoard.exe present"
}

Write-Host "All release assets verified."
