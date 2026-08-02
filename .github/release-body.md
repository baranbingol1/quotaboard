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
3. These builds are code-signed when repository signing is enabled; otherwise
   they carry an untrusted CI test signature, so Windows SmartScreen may still
   warn that the publisher is unknown: **More info → Run anyway**. Each
   release also ships an SPDX SBOM and a build-provenance attestation, which
   you can check with `gh attestation verify`.

Verify a download against its `.sha256` file:

```powershell
Get-FileHash .\QuotaBoard-0.1.1-win-x64.zip -Algorithm SHA256
```

Requires Windows 10 21H2 (build 19045) or newer. The runtime is self-contained,
so there is no .NET to install.

## Uninstall

Delete the folder you extracted. To remove the local database, pricing cache and
preferences as well, delete `%LOCALAPPDATA%\QuotaBoard`.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/),
certificate by [SignPath Foundation](https://signpath.org/).

When SignPath signing is enabled, signed release binaries are built from this
public repository using GitHub Actions. See the repository's
[Code signing policy](https://github.com/baranbingol1/quotaboard#code-signing-policy)
and [privacy policy](https://github.com/baranbingol1/quotaboard/blob/main/PRIVACY.md).
