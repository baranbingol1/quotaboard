[CmdletBinding()]
param(
    # Publish output directory containing QuotaBoard.exe and the shipped DLLs.
    [Parameter(Mandatory)]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-binaries.ps1')

<#
Throwaway fallback signing for the release pipeline.

When the repository enables SignPath (SIGNPATH_ENABLED=true plus its secrets),
the release workflow signs with a trusted certificate instead and never calls
this script. Without that setup this script still exercises the Authenticode
path end to end: it generates a self-signed code-signing certificate inside
the job, signs only the project-owned binaries declared in
release-binaries.ps1, and deletes the certificate afterwards. Vendor files
are never signing targets, regardless of their current signature state.

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

$targets = @()
foreach ($candidate in @(Get-QuotaBoardOwnedBinaryPaths -RootPath $resolvedPublishDir)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $candidate
    if ($signature.Status -eq 'HashMismatch') {
        throw "$candidate has a broken Authenticode signature (HashMismatch); refusing to sign over it."
    }
    if ($signature.SignatureType -eq 'Authenticode') {
        throw "$candidate is already embedded-signed; refusing to replace its project signature."
    }
    $targets += $candidate
}

$signTool = Resolve-SignTool
Write-Host "Using signtool: $signTool"
Write-Host "Signing $($targets.Count) project-owned file(s); vendor binaries are not signing targets."
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

Write-Host "Signed $($targets.Count) project-owned file(s) with untrusted test signatures."
