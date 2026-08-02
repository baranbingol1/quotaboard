[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RootPath,

    # Trusted SignPath releases must chain to a trusted root. The fallback
    # certificate is deliberately untrusted, but its signatures must still be
    # present and cryptographically intact.
    [bool]$RequireTrustedSignature = $false
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-binaries.ps1')

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

$root = [System.IO.Path]::GetFullPath($RootPath)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Signature verification directory does not exist: $root"
}

$targets = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $_.Extension -in '.exe', '.dll'
})
if ($targets.Count -eq 0) {
    throw "No .exe or .dll files found to verify in $root"
}

$signTool = Resolve-SignTool
$ownedPaths = @(Get-QuotaBoardOwnedBinaryPaths -RootPath $root)
foreach ($target in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($root, $target.FullName)
    $signature = Get-AuthenticodeSignature -LiteralPath $target.FullName
    $isOwned = $ownedPaths -contains $target.FullName
    if (-not $signature.SignerCertificate) {
        if ($isOwned) {
            throw "$relative carries no Authenticode signature"
        }
        continue
    }
    if ($signature.Status -in 'HashMismatch', 'NotSigned') {
        throw "$relative has an invalid Authenticode signature ($($signature.Status)): $($signature.StatusMessage)"
    }
    # A catalog signature is registered on the machine that installed the
    # file, not carried inside it, so it does not survive being zipped and
    # unpacked on the user's machine. Only an embedded signature ships.
    if ($signature.SignatureType -ne 'Authenticode') {
        if ($isOwned) {
            throw "$relative is $($signature.SignatureType)-signed rather than embedded; the signature would not survive packaging"
        }
        continue
    }

    $verifyOutput = & $signTool verify /pa $target.FullName 2>&1
    if ($LASTEXITCODE -ne 0) {
        $verifyText = ($verifyOutput | Out-String).Trim()
        $untrustedRoot = $verifyText -match 'terminated in a root certificate which is not trusted'
        if (-not $isOwned -or $RequireTrustedSignature -or -not $untrustedRoot) {
            throw "${relative}: signtool verify failed: $verifyText"
        }
        Write-Warning "$relative signature is intact but chains to an untrusted test root (expected for self-signed CI builds)."
    }
}

Write-Host "Verified all project-owned signatures and every existing embedded vendor signature; unsigned vendor binaries were left unchanged."
