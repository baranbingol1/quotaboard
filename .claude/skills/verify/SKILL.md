---
name: verify
description: Build, launch, drive, and screenshot the AI Limits WinUI app to verify changes at the real UI surface.
---

# Verifying AI Limits changes

## Build

```powershell
dotnet build src/AiLimits.App/AiLimits.App.csproj -p:Platform=x64
```

Output exe: `src\AiLimits.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\QuotaBoard.exe`
(the csproj defaults to Release; no publish step needed for verification).

## Drive + capture

`scripts/drive-app.ps1` launches the exe, optionally seeds the theme preference,
navigates via UI Automation, scrolls, screenshots via PrintWindow, and kills the app:

```powershell
# $env:DRIVE_SCROLL = "1" scrolls the active page to the bottom before capture
# $env:DRIVE_EXPAND = "1" expands all collapsed Expanders first
& scripts\drive-app.ps1 -Exe <full path to QuotaBoard.exe> `
    -Theme "Dark" `      # seeds %LOCALAPPDATA%\AI Limits\theme.preference; "" deletes it (follow system)
    -Page "Usage" `      # UIA nav-item name: Overview / Usage / Connections / Diagnostics
    -SettleSeconds 8 `   # data load takes ~6-8s (scanners + pricing catalog)
    -Shot out.png
```

## Gotchas

- **Turkish characters in the repo path**: PowerShell 5.1 reads BOM-less UTF-8 scripts
  as ANSI, so never hardcode the repo path inside a .ps1 â€” pass it as a parameter.
- **Screenshots**: use PrintWindow with PW_RENDERFULLCONTENT (flag 2), not
  CopyFromScreen â€” the user's desktop may have other windows on top, and
  SetForegroundWindow is blocked for background processes.
- The app reads the user's real local CLI histories, so Usage rows show live data.
- Theme preference lives at `%LOCALAPPDATA%\AI Limits\theme.preference`
  (`Light` / `Dark` / absent = follow system). Restore whatever was there when done.
- The window title is "AI Limits"; window creation takes up to ~5s.
