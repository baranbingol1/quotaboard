[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    [string]$OutputPath = (Join-Path $env:TEMP "AiLimits\release\win-$Architecture")
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($OutputPath)
$failures = New-Object System.Collections.Generic.List[string]

function Require-File([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) {
        $script:failures.Add("Missing required artifact: $relative")
        return $null
    }
    return $path
}

Write-Host "Validating publish output at $root"

# Required WinUI layout + native dependencies.
$exe = Require-File 'QuotaBoard.exe'
Require-File 'QuotaBoard.pri' | Out-Null
Require-File 'App.xbf' | Out-Null
Require-File 'MainWindow.xbf' | Out-Null
Require-File 'AiLimits.Presentation.WinUI' | Out-Null
Require-File 'Microsoft.WindowsAppRuntime.Bootstrap.dll' | Out-Null
# Native SQLite ships with Microsoft.Data.Sqlite's SQLitePCLRaw bundle.
if (-not (Get-ChildItem -Path $root -Recurse -Filter 'e_sqlite3.dll' -ErrorAction SilentlyContinue)) {
    $failures.Add('Missing native SQLite (e_sqlite3.dll).')
}

# PE architecture check on the produced executable.
if ($exe) {
    $expected = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne $expected) {
        $failures.Add(("PE machine 0x{0:X4} does not match expected 0x{1:X4} for {2}." -f $machine, $expected, $Architecture))
    } else {
        Write-Host ("PE architecture OK: 0x{0:X4}" -f $machine)
    }

    # Version metadata present and consistent.
    $version = (Get-Item $exe).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $failures.Add('Executable is missing file version metadata.')
    } else {
        Write-Host "Version: $version"
    }
}

# Nothing sensitive or developer-local must ship.
$forbidden = Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $_.Extension -in '.db', '.db-wal', '.db-shm', '.log', '.pdb.user', '.user' -or
    $_.Name -in 'appsettings.Development.json', 'launchSettings.json', 'theme.json', 'font.json'
}
foreach ($file in $forbidden) {
    $failures.Add("Unexpected file in publish output: $($file.FullName.Substring($root.Length).TrimStart('\'))")
}

if ($failures.Count -gt 0) {
    Write-Host "::error::Publish validation failed:"
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host "Publish validation passed for win-$Architecture."
