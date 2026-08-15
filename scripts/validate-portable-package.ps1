[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory)]
    [string]$ArchivePath,

    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:TEMP ("QuotaBoard-portable-validation-" + [guid]::NewGuid().ToString('N'))
$failures = [System.Collections.Generic.List[string]]::new()

function Require-File([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $script:failures.Add("Missing required portable artifact: $RelativePath")
        return $null
    }
    return $path
}

function Assert-PeArchitecture([string]$Path, [string]$Label, [int]$ExpectedMachine) {
    if (-not $Path) { return }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne $ExpectedMachine) {
        $script:failures.Add(("$Label PE machine 0x{0:X4} does not match expected 0x{1:X4}." -f $machine, $ExpectedMachine))
    }
}

try {
    Expand-Archive -LiteralPath ([System.IO.Path]::GetFullPath($ArchivePath)) -DestinationPath $root -Force
    $launcher = Require-File 'QuotaBoard.exe'
    Require-File 'Update.exe' | Out-Null
    Require-File '.portable' | Out-Null
    $application = Require-File 'current\QuotaBoard.exe'
    $versionFile = Require-File 'current\sq.version'
    Require-File 'current\QuotaBoard.pri' | Out-Null
    Require-File 'current\App.xbf' | Out-Null
    Require-File 'current\MainWindow.xbf' | Out-Null
    Require-File 'current\THIRD-PARTY-NOTICES.txt' | Out-Null

    # Velopack's launcher is a small AnyCPU-compatible x86 bootstrap. The real
    # application under current must match the package architecture.
    Assert-PeArchitecture $launcher 'Launcher' 0x014C
    $applicationMachine = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
    Assert-PeArchitecture $application 'Application' $applicationMachine

    if ($versionFile) {
        $metadata = Get-Content -Raw -LiteralPath $versionFile
        if ($metadata -notmatch [regex]::Escape($Version)) {
            $failures.Add("sq.version does not contain package version $Version.")
        }
        if ($metadata -notmatch [regex]::Escape("win-$Architecture")) {
            $failures.Add("sq.version does not contain channel win-$Architecture.")
        }
    }

    & "$PSScriptRoot\validate-publish.ps1" -Architecture $Architecture -OutputPath (Join-Path $root 'current')
    if (-not $?) {
        $failures.Add('The real application payload failed raw publish validation.')
    }

    $forbidden = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
        $_.Extension -in '.db', '.db-wal', '.db-shm', '.log', '.user' -or
        $_.Name -in '.env', 'appsettings.Development.json', 'launchSettings.json'
    }
    foreach ($file in $forbidden) {
        $failures.Add("Sensitive or developer-local file in portable package: $($file.Name)")
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Host "::error::$_" }
        exit 1
    }
    Write-Host "Portable package validation passed for win-$Architecture $Version."
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
