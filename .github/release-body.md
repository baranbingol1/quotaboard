QuotaBoard reads the CLI sessions and local usage histories already on your PC
to show subscription limits, reset times, token usage, and API-price estimates.
Live limit checks go directly to each provider; there is no backend and no
telemetry.

## Install

1. Download the archive for your CPU — `win-x64` for Intel/AMD, `win-arm64` for
   Snapdragon and other ARM machines.
2. Extract it anywhere and run `QuotaBoard.exe`. There is no installer and no
   administrator rights are needed; nothing is written outside the folder you
   extracted to and `%LOCALAPPDATA%\QuotaBoard`.
3. Windows SmartScreen will warn that the publisher is unknown, because these
   builds are not code-signed yet: **More info → Run anyway**.

Verify a download against its `.sha256` file:

```powershell
Get-FileHash .\QuotaBoard-0.1.1-win-x64.zip -Algorithm SHA256
```

Requires Windows 10 21H2 (build 19045) or newer. The runtime is self-contained,
so there is no .NET to install.

## Uninstall

Delete the folder you extracted. To remove the local database, pricing cache and
preferences as well, delete `%LOCALAPPDATA%\QuotaBoard`.
