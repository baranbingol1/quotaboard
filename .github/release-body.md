QuotaBoard reads the CLI sessions and local usage histories already on your PC
to show subscription limits, reset times, token usage, and API-price estimates.
Live limit checks go directly to each provider; there is no backend and no
telemetry.

## Install

1. Download the archive for your CPU — `win-x64` for Intel/AMD, `win-arm64` for
   Snapdragon and other ARM machines. For most PCs, choose the file ending in
   `win-x64.zip`. The `.nupkg` files support in-app updates and are not for
   manual installation.
2. Extract it anywhere and run `QuotaBoard.exe`. There is no installer and no
   administrator rights are needed; nothing is written outside the folder you
   extracted to and `%LOCALAPPDATA%\QuotaBoard`.
3. These builds are unsigned, so Windows SmartScreen may warn that the publisher
   is unknown: **More info → Run anyway**. Download them only from this official
   release. Each release also ships an SPDX SBOM and a build-provenance
   attestation, which you can check with `gh attestation verify`.

Verify a download against its `.sha256` file:

```powershell
$zip = Get-Item .\QuotaBoard-*-win-x64.zip
$expected = ((Get-Content -LiteralPath "$($zip.FullName).sha256").Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "Checksum mismatch" }
"Checksum verified: $actual"
```

Requires Windows 10 22H2 (build 19045) or newer. The runtime is self-contained,
so there is no .NET to install.

## Update

Keep launching the `QuotaBoard.exe` at the root of the extracted folder. Use
**Settings → About & updates** to check for an update. Download and restart are
separate actions.

## Uninstall

Delete the folder you extracted. To remove the local database, pricing cache and
preferences as well, delete `%LOCALAPPDATA%\QuotaBoard`.

## Release integrity

Release archives and SPDX SBOMs are built from this public repository using
GitHub Actions. Each asset has a SHA-256 sidecar and a GitHub build-provenance
attestation. See the repository's [release integrity
policy](https://github.com/baranbingol1/quotaboard#release-integrity) and
[privacy policy](https://github.com/baranbingol1/quotaboard/blob/main/PRIVACY.md).
