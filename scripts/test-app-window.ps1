param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\app\win-x64\QuotaBoard.exe')
)

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$process = Start-Process -FilePath $resolvedExecutable -WorkingDirectory (Split-Path $resolvedExecutable) -PassThru

try
{
    $deadline = (Get-Date).AddSeconds(8)
    do
    {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }
    while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)

    if ($process.HasExited)
    {
        throw "AI Limits exited before creating a window (exit code $($process.ExitCode))."
    }

    if ($process.MainWindowHandle -eq 0)
    {
        throw "AI Limits stayed alive but did not create a window within eight seconds."
    }

    Write-Host "PASS: AI Limits created window '$($process.MainWindowTitle)' (handle $($process.MainWindowHandle))."
}
finally
{
    if (-not $process.HasExited)
    {
        Stop-Process -Id $process.Id -Force
    }
}
