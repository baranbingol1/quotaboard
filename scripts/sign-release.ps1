[CmdletBinding()]
param(
    # Publish output directory containing QuotaBoard.exe and the shipped DLLs.
    [Parameter(Mandatory)]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

<#
Throwaway fallback signing for the release pipeline.

When the repository enables SignPath (SIGNPATH_ENABLED=true plus its secrets),
the release workflow signs with a trusted certificate instead and never calls
this script. Without that setup this script still exercises the Authenticode
path end to end: it generates a self-signed code-signing certificate inside
the job, signs the binaries that carry no embedded signature of their own,
and deletes the certificate afterwards.

It signs those binaries ONLY. `signtool sign` without /as replaces a primary
signature, and this package ships roughly 300 PE files of which the large
majority are already Authenticode-signed by Microsoft or another vendor.
Signing them all would strip those publisher identities and reattribute the
whole runtime to a throwaway certificate — worse for provenance and endpoint
reputation than leaving them alone.

Catalog-signed files are signed. Catalog membership is registered on the
build machine and does not survive being zipped, so for those files an
embedded signature is the only one that reaches the user.

The resulting signature is NOT trusted. Windows SmartScreen will keep warning,
and verify-release.ps1 accepts it only when trusted signing is not required.
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

$resolvedPublishDir = [System.IO.Path]::GetFullPath($PublishDir)
if (-not (Test-Path -LiteralPath $resolvedPublishDir)) {
    throw "Publish directory does not exist: $resolvedPublishDir"
}

$candidates = @(Get-ChildItem -LiteralPath $resolvedPublishDir -Recurse -File |
    Where-Object { $_.Extension -in '.exe', '.dll' })
if ($candidates.Count -eq 0) {
    throw "No .exe or .dll files found in $resolvedPublishDir"
}

# SignatureType is the deciding property, not Status: 'Authenticode' means the
# file carries its own embedded signature and must be left untouched, while
# 'Catalog' and 'None' both mean nothing verifiable survives packaging.
$targets = @()
$preserved = @()
foreach ($candidate in $candidates) {
    $signature = Get-AuthenticodeSignature -LiteralPath $candidate.FullName
    if ($signature.Status -eq 'HashMismatch') {
        throw "$($candidate.FullName) has a broken Authenticode signature (HashMismatch); refusing to sign over it."
    }
    if ($signature.SignatureType -eq 'Authenticode') {
        $preserved += $candidate.FullName
        continue
    }
    $targets += $candidate.FullName
}
if ($targets.Count -eq 0) {
    throw "Every PE in $resolvedPublishDir is already signed; expected the first-party binaries to be unsigned."
}

$signTool = Resolve-SignTool
Write-Host "Using signtool: $signTool"
Write-Host "Preserving $($preserved.Count) existing vendor signature(s); signing $($targets.Count) unsigned or catalog-only file(s)."
Write-Host "Signing with a THROWAWAY self-signed certificate. This signature is NOT trusted; it only proves the signing pipeline works."
$targets | ForEach-Object { Write-Host "  sign: $($_.Substring($resolvedPublishDir.Length).TrimStart('\'))" }

$certificate = New-SelfSignedCertificate `
    -Subject 'CN=QuotaBoard CI Test Signing (Untrusted)' `
    -FriendlyName 'QuotaBoard CI Test Signing (Untrusted)' `
    -Type CodeSigningCert `
    -HashAlgorithm sha256 `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyExportPolicy NonExportable `
    -NotAfter (Get-Date).AddDays(14) `
    -CertStoreLocation 'Cert:\CurrentUser\My'

try {
    & $signTool sign /fd sha256 /sha1 $certificate.Thumbprint /d 'QuotaBoard' $targets
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed with exit code $LASTEXITCODE."
    }
}
finally {
    # The certificate is per-build and must not linger: deleting it keeps the
    # throwaway signature from being reused or mistaken for a real identity.
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
}

Write-Host "Signed $($targets.Count) file(s) (untrusted test signature); $($preserved.Count) vendor signature(s) left intact."
