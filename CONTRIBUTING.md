# Contributing to QuotaBoard

## Requirements

- Windows 10 22H2+ or Windows 11
- .NET 10 SDK

## Build and test

```powershell
dotnet restore tests/AiLimits.Tests/AiLimits.Tests.csproj
dotnet test tests/AiLimits.Tests/AiLimits.Tests.csproj --configuration Release
```

## Run the app

`app/` is generated output and is stale after every source edit. Rebuild the self-contained app before manual testing, then launch only the published executable:

```powershell
./scripts/publish-ai-limits.ps1 -Architecture x64 -Configuration Release
explorer ./app/win-x64/QuotaBoard.exe
```

Use `-Architecture arm64` for ARM64 builds. Do not launch or copy the executable from `bin/Release`.

## Publish validation

```powershell
./scripts/publish-ai-limits.ps1 -Architecture x64 -Configuration Release
./scripts/validate-publish.ps1 -Architecture x64 -OutputPath ./app/win-x64
```

CI runs tests and validates self-contained x64 and ARM64 publishes for pull requests and `main`.

## Cleanup

```powershell
./scripts/clean.ps1          # Remove bin/, obj/, and app/
./scripts/clean.ps1 -KeepApp # Remove build scratch only
```

Never edit or commit `app/`, `bin/`, or `obj/`. `scripts/publish-ai-limits.ps1` is the only publish tool; its default local destination is `app/win-<arch>/`.

## AI-assisted contributions

AI tools are welcome when they help improve QuotaBoard. Keep pull requests focused, reviewable, and limited to the problem being solved. Review and understand generated changes before submitting them; avoid broad rewrites, unrelated cleanup, generated churn, and other AI slop.
