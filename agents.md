# Agent notes

## Build and test

Run the test suite before considering a change complete:

```powershell
dotnet restore tests/AiLimits.Tests/AiLimits.Tests.csproj
dotnet test tests/AiLimits.Tests/AiLimits.Tests.csproj --configuration Release
```

## Manual app verification

`app/` is generated output and becomes stale after source edits. Rebuild before manual testing, then launch only the published executable:

```powershell
./scripts/publish-ai-limits.ps1 -Architecture x64
explorer ./app/win-x64/QuotaBoard.exe
```

Use `-Architecture arm64` for ARM64 builds. `scripts/clean.ps1` removes stale build output.

## Constraints

- Never hand-edit or commit `app/`, `bin/`, or `obj/`.
- Use `scripts/publish-ai-limits.ps1` as the only publish tool.
