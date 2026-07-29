[CmdletBinding()]
param(
    # Directory holding the assets re-downloaded from the published release.
    [Parameter(Mandatory)]
    [string]$DistDir,

    # When true (SignPath signing enabled), QuotaBoard.exe must chain to a
    # trusted root. When false, a self-signed test signature is accepted as
    # long as a signature is actually present and the digest verifies.
    [bool]$RequireTrustedSignature = $false
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

<#
Post-release verification.

Runs against assets re-downloaded from the published GitHub release, not the
files left on the build agent, so upload corruption or tampering is caught.
For every archive: the .sha256 sidecar must match, the zip must be non-empty
and contain QuotaBoard.exe, and the executable must carry an Authenticode
signature. SPDX SBOM assets must parse as JSON. Any mismatch fails the run.
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
}

foreach ($sbom in Get-ChildItem -LiteralPath $resolvedDistDir -Filter '*.spdx.json' -File) {
    try {
        $null = Get-Content -LiteralPath $sbom.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$($sbom.Name) is not valid JSON: $_"
    }
    Write-Host "$($sbom.Name): SBOM parses as JSON"
}

Write-Host "All release assets verified."
