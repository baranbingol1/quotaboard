# Contributing to QuotaBoard

## Requirements

- Windows 10 22H2+ or Windows 11
- .NET 10 SDK

## Build and test

```powershell
dotnet restore
dotnet test tests/AiLimits.Tests/AiLimits.Tests.csproj --configuration Release
dotnet test tests/AiLimits.IntegrationTests/AiLimits.IntegrationTests.csproj --configuration Release
```

Unit tests live in `tests/AiLimits.Tests`. Integration tests in
`tests/AiLimits.IntegrationTests` exercise SQLite and the refresh pipeline on
a real on-disk database.

## Quality gates

Formatting, naming, complexity, file size, dead-code, duplication,
unused packages, and TODO tracking are enforced locally and in CI:

```powershell
dotnet tool restore
dotnet csharpier check src tests
./scripts/invoke-quality-gates.ps1
./scripts/install-git-hooks.ps1   # one-time; points this clone at .githooks
```

Agent operating commands live in `AGENTS.md`. `.editorconfig` is the naming
and formatting contract. CSharpier is the formatter. Pre-commit
(`.githooks/pre-commit` and `.pre-commit-config.yaml`) runs the same scripts
CI runs. Architecture boundaries are locked by ArchUnitNET in
`tests/AiLimits.Tests/Architecture`. Snapshot history has an N+1 query budget
in `SqliteHistoryQueryBudgetTests`. Unused PackageReferences are reported by
ReferenceTrimmer (`scripts/quality/Find-UnusedPackages.ps1`).

Coverage is collected with Coverlet. CI fails the suite under 40% line
coverage, prints the slowest tests from the TRX, and retries the known
timing-sensitive refresh-coalescing test via xRetry.

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

## Issues and pull requests

Pick Bug, Feature, or Chore. Maintainers add `P0`–`P3` and an area label
(`ui`, `providers`, `persistence`, `infra`, `docs`, `ci`). Security reports
go to [private vulnerability reporting](https://github.com/baranbingol1/quotaboard/security/advisories/new).

## AI-assisted contributions

AI tools are welcome when they help improve QuotaBoard. Keep pull requests focused, reviewable, and limited to the problem being solved. Review and understand generated changes before submitting them; avoid broad rewrites, unrelated cleanup, generated churn, and other AI slop.
