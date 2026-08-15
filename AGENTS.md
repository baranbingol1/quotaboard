# QuotaBoard

Local-first WinUI 3 / .NET 10 Windows desktop app. It reads already-signed-in
CLI sessions and local usage files, then talks to each provider directly.
There is no QuotaBoard backend, no telemetry, and nothing to configure.

Human onboarding and screenshots live in `README.md`. Contributor narrative
lives in `CONTRIBUTING.md`. This file is the operating brief for coding agents.

## Commands

Windows + .NET 10 SDK (`global.json`). No env vars.

```powershell
dotnet restore
dotnet tool restore
dotnet test tests/AiLimits.Tests/AiLimits.Tests.csproj --configuration Release
dotnet test tests/AiLimits.IntegrationTests/AiLimits.IntegrationTests.csproj --configuration Release
dotnet csharpier check src tests
./scripts/invoke-quality-gates.ps1
./scripts/publish-ai-limits.ps1 -Architecture x64 -Configuration Release
explorer ./app/win-x64/QuotaBoard.exe
```

Focused unit test: `dotnet test tests/AiLimits.Tests/AiLimits.Tests.csproj --filter FullyQualifiedName~RefreshCoordinatorTests`.
Format write: `dotnet csharpier format src tests`.
One-time hooks: `./scripts/install-git-hooks.ps1`.
Cleanup: `./scripts/clean.ps1` (add `-KeepApp` to leave the published exe).

Do not launch or copy `bin/` output. `app/` is generated and stale after every
source edit; republish before clicking the app.

## Layout

- `src/AiLimits.Domain` — models and value types. No project references.
- `src/AiLimits.Application` — orchestration, ports, view-model contracts.
- `src/AiLimits.Infrastructure` — provider adapters, HTTP, SQLite.
- `src/AiLimits.Platform.Windows` — Credential Manager, Win32, WebView2.
- `src/AiLimits.Presentation.WinUI` — XAML, pages, view models.
- `src/AiLimits.App` — composition root and published exe (`QuotaBoard.exe`).
- `tests/AiLimits.Tests` — unit + ArchUnitNET layer tests.
- `tests/AiLimits.IntegrationTests` — real on-disk SQLite + refresh pipeline.
- `scripts/` — publish, validate, quality gates. `scripts/publish-ai-limits.ps1`
  is the only publish path.

`app/`, `bin/`, `obj/`, `TestResults/`, `CodexBar/` are not source.

## Conventions

Layers are enforced by `tests/AiLimits.Tests/Architecture/LayerBoundaryTests.cs`.
Domain compiles alone. Application depends only on Domain. Presentation does
not touch SQLite, Win32, or provider adapters.

Provider pipeline: descriptor → ordered fetch strategies → snapshot →
account checks → persistence → UI. Two rules that are easy to break:

- Fallback is selective. A strategy declares its own `FallbackPolicy`. Do not
  catch-all and try the next source; that is how an auth failure becomes
  another account's numbers.
- A snapshot states completeness. `Authoritative` means the listed meters are
  *all* the meters; missing ones are deleted. A partial parse must be
  `Partial` so previous meters stay with a Stale badge.

```csharp
// CORRECT: this source only saw some meters
return FetchOutcome.Snapshot(snapshot with { Completeness = SnapshotCompleteness.Partial });

// WRONG: missing meters will disappear from the card
return FetchOutcome.Snapshot(snapshot with { Completeness = SnapshotCompleteness.Authoritative });
```

CSharpier + `.editorconfig` are the format/naming contract (PascalCase types,
`I`-prefix interfaces, `_camelCase` fields). New `.cs` files start with
`// SPDX-License-Identifier: Apache-2.0`. Pin packages in
`Directory.Packages.props`; do not add a version on the `<PackageReference>`.

Do not invent colors, type sizes, or spacing. Stale/failed/unknown are
first-class states; never present a cached quota as live.

## Testing

Add or update a test when behavior changes. Provider and persistence bugs
need a regression in `tests/AiLimits.Tests`. Cross-component SQLite/refresh
behavior belongs in `tests/AiLimits.IntegrationTests`.

Do not call real provider APIs or commit credentials, cookies, or raw
provider payloads. Fixture adapters and temp databases are the harness.
History queries have an N+1 budget in `SqliteHistoryQueryBudgetTests`
(three statements, not 1+2N). Diagnostics strings go through
`DiagnosticRedactor`.

## Safety

- Do not add telemetry, analytics, crash reporting, or a QuotaBoard backend.
- Do not log tokens, cookies, or session material. Redact first.
- App-refreshed secrets stay in Windows Credential Manager. `.env` is
  gitignored; this app does not need one.
- Do not hand-edit `app/`. Do not pass `publish-ai-limits.ps1 -OutputPath`
  outside `app\` unless the caller passed `-AllowExternalOutputPath`.
- Leave `CodexBar/` and `cb_changelog.md` alone (vendored reference clone).
- Version lives in `Directory.Build.props`. Release tags must match it.

## Verification

For the files you touched, run the focused `dotnet test --filter` first.
Before you finish a behavior change:

1. `dotnet csharpier check src tests`
2. The relevant `dotnet test` project(s) above
3. `./scripts/invoke-quality-gates.ps1 -SkipFormat` if you changed scripts
   or added sizeable source

A publish is required only when the change needs a running window
(`./scripts/publish-ai-limits.ps1` then `explorer ./app/win-x64/QuotaBoard.exe`).
`scripts/drive-app.ps1` can drive the published exe over UIA.

Privacy and no-telemetry: `PRIVACY.md`. Vulnerability reports: `SECURITY.md`.
