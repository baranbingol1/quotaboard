param(
    [string]$Exe,
    [string]$Theme = "",
    [string]$Page = "",
    [string]$Shot = "shot.png",
    [int]$SettleSeconds = 6,
    # Resize before the settle so responsive layouts reflow once, not twice.
    # 0 keeps whatever size the app opened at.
    [int]$Width = 0,
    [int]$Height = 0,
    [switch]$CleanState,
    [string]$DataRoot = "",
    [switch]$FollowSystemTheme
)

$ErrorActionPreference = "Stop"
$exe = $Exe
$arguments = $null
if ($CleanState) {
    if ($DataRoot -eq "") {
        $DataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("QuotaBoard-E2E-" + [guid]::NewGuid().ToString("N"))
    }
    $DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
    $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $DataRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "-DataRoot must be below the Windows temporary directory"
    }
    New-Item -ItemType Directory -Force $DataRoot | Out-Null
    $prefDir = $DataRoot
    $arguments = "--e2e-clean-state `"$DataRoot`""
    Write-Host "Isolated data root: $DataRoot"
} else {
    if ($DataRoot -ne "") { throw "-DataRoot requires -CleanState" }
    $prefDir = Join-Path $env:LOCALAPPDATA "QuotaBoard"
}
$prefPath = Join-Path $prefDir "theme.preference"

if ($Theme -ne "" -and $FollowSystemTheme) { throw "Use either -Theme or -FollowSystemTheme, not both" }
if ($Theme -ne "") {
    New-Item -ItemType Directory -Force $prefDir | Out-Null
    [System.IO.File]::WriteAllText($prefPath, $Theme)
    Write-Host "Seeded theme.preference = $Theme"
} elseif ($FollowSystemTheme -and (Test-Path $prefPath)) {
    Remove-Item $prefPath -Force -Confirm:$false
    Write-Host "Removed theme.preference (follow system)"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$startParameters = @{
    FilePath = $exe
    WorkingDirectory = (Split-Path $exe)
    PassThru = $true
}
if ($null -ne $arguments) { $startParameters.ArgumentList = $arguments }
$processName = [System.IO.Path]::GetFileNameWithoutExtension($exe)
$workingPrefix = [System.IO.Path]::GetFullPath($startParameters.WorkingDirectory).TrimEnd('\') + '\'
$existingProcessIds = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | ForEach-Object Id)
$process = Start-Process @startParameters
try {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
        # A portable Velopack ZIP starts through its root launcher. Follow the
        # new process under current\ after that short-lived launcher exits.
        if ($process.HasExited) {
            $launchedApp = Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object {
                if ($existingProcessIds -contains $_.Id) { return $false }
                try { return $_.Path.StartsWith($workingPrefix, [System.StringComparison]::OrdinalIgnoreCase) }
                catch { return $false }
            } | Select-Object -First 1
            if ($null -ne $launchedApp) {
                $process = $launchedApp
                Write-Host "Following launched app process $($process.Id)"
            }
        }
    } while (($process.HasExited -or $process.MainWindowHandle -eq 0) -and (Get-Date) -lt $deadline)

    if ($process.HasExited) { throw "App exited early, code $($process.ExitCode)" }
    if ($process.MainWindowHandle -eq 0) { throw "No window within 15s" }
    $hwnd = $process.MainWindowHandle
    Write-Host "Window created: '$($process.MainWindowTitle)' handle $hwnd"

    if ($Width -gt 0 -and $Height -gt 0) {
        [Win32]::MoveWindow($hwnd, 40, 40, $Width, $Height, $true) | Out-Null
        Start-Sleep -Milliseconds 500
        Write-Host "Resized to $Width x $Height"
    }

    Start-Sleep -Seconds $SettleSeconds

    if ($Page -ne "") {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Page)
        $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -eq $item) { throw "Nav item '$Page' not found via UIA" }
        $pattern = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
        } elseif ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke()
        } else {
            throw "Nav item '$Page' supports neither SelectionItem nor Invoke"
        }
        Write-Host "Navigated to $Page"
        Start-Sleep -Seconds 3
    }

    if ($env:DRIVE_EXPAND -eq "1") {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $expandCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
        foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $expandCond)) {
            $ec = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
                try { $ec.Expand(); Write-Host "Expanded: $($el.Current.Name)" } catch {}
            }
        }
        Start-Sleep -Seconds 1
    }

    if ($env:DRIVE_SCROLL) {
        # "1" scrolls to the bottom; any other number is a scroll percent.
        $pct = 100
        if ($env:DRIVE_SCROLL -match '^\d+$' -and [int]$env:DRIVE_SCROLL -le 100 -and $env:DRIVE_SCROLL -ne "1") { $pct = [int]$env:DRIVE_SCROLL }
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $scrollCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
        $scrollers = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollCond)
        $done = $false
        foreach ($scroller in $scrollers) {
            $sp = $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($sp.Current.VerticallyScrollable) {
                $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct)
                Write-Host "Scrolled to $pct%"
                Start-Sleep -Seconds 1
                $done = $true
                break
            }
        }
        if (-not $done) { Write-Host "WARN: no vertically scrollable element found" }
    }

    $rect = New-Object Win32+RECT
    [Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($width, $height)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $gfx.GetHdc()
    $ok = [Win32]::PrintWindow($hwnd, $hdc, 2)  # PW_RENDERFULLCONTENT
    $gfx.ReleaseHdc($hdc)
    if (-not $ok) { Write-Host "WARN: PrintWindow returned false" }
    $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $gfx.Dispose(); $bmp.Dispose()
    Write-Host "Saved $Shot ($width x $height)"
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -Confirm:$false }
}
