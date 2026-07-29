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
the job, signs QuotaBoard.exe and every shipped DLL, and deletes the
certificate afterwards.

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

$targets = Get-ChildItem -LiteralPath $resolvedPublishDir -File |
    Where-Object { $_.Extension -in '.exe', '.dll' } |
    ForEach-Object { $_.FullName }
if (-not $targets) {
    throw "No .exe or .dll files found to sign in $resolvedPublishDir"
}

$signTool = Resolve-SignTool
Write-Host "Using signtool: $signTool"
Write-Host "Signing $($targets.Count) files with a THROWAWAY self-signed certificate. This signature is NOT trusted; it only proves the signing pipeline works."

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

Write-Host "Signed $($targets.Count) files (untrusted test signature)."
