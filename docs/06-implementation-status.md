# AI Limits for Windows — Implementation Status

Date: 2026-07-13

This report records what is implemented and verified in the first production-oriented baseline, and what still blocks a public v1. It intentionally distinguishes implemented provider engines from live UI integration.

## 1. Delivered baseline

### Solution and boundaries

The repository now contains the planned layered .NET 10 solution:

- `AiLimits.Domain`: provider-independent accounts, dynamic usage meters, balances, snapshots, exact token events, model resolution, pricing quotes, and diagnostics.
- `AiLimits.Application`: provider/repository contracts, snapshot merging, refresh coordination, model normalization, explicit resolution, API-equivalent pricing, and alert evaluation.
- `AiLimits.Infrastructure`: SQLite, migrations, repositories, dynamic JSON parsing, pricing catalog/cache, provider adapters, credential readers, database discovery, and exact-token scanners.
- `AiLimits.Platform.Windows`: Windows Credential Manager storage and isolated WebView2 profile lifecycle.
- `AiLimits.Presentation.WinUI`: native WinUI pages, controls, themes, accessibility metadata, view models, and fixture-backed observatory data.
- `AiLimits.App`: unpackaged WinUI executable, application resources, composition boundary, and self-contained publish profiles.

The main implementation plan is preserved in `docs/05-implementation-plan.md`.

### Dynamic limit pipeline

The core does not contain fixed primary, secondary, five-hour, weekly, or model-specific lanes.

- Meter identity prefers provider IDs and otherwise derives stable keys from provider/path/scope/model information.
- Unknown JSON objects with recognizable usage/reset fields are extracted as meters.
- Friendly labels are adapter aliases; display text is not identity.
- Authoritative snapshots retire missing meters.
- Partial snapshots retain missing meters as stale.
- Historical rows remain after a live meter is retired.
- The dashboard enumerates meters and shows the two most urgent per provider with an `N more limits` affordance.
- The Codex fixture is weekly-only and renders no placeholder five-hour meter.
- The Claude fixture demonstrates a newly discovered Fable-like meter without a UI code path for that name.

### Refresh, persistence, and pricing

Implemented behaviors include:

- Account/configuration authority checks before publication.
- Monotonic refresh generations so late work cannot overwrite an account switch.
- Equivalent-request coalescing and a global provider concurrency cap of four.
- Last-known-good cache preservation after transient failures.
- Redacted fetch-attempt diagnostics.
- SQLite WAL mode and versioned schema migration.
- Snapshot, meter, balance, fetch-attempt, scanner cursor, deduplication fingerprint, aggregate, catalog, resolution, and alert-state tables.
- Exact-only token events and daily aggregates; no quota-to-token conversion.
- Explicit model aliasing with normalized service-specific raw IDs and no fuzzy pricing guesses.
- Separate service, upstream model vendor, and pricing-catalog identities.
- API-equivalent pricing lanes for input, output, cache read, cache write, reasoning, and long-context data when the catalog supplies them.
- Unresolved models remain in token totals but are excluded from dollar totals.
- Provider-reported service cost remains separate from API-equivalent cost.
- models.dev conditional refresh metadata, SHA-256 catalog identity, validation, 24-hour refresh policy, and last-valid-cache retention.

### Provider engines

| Provider | Limit acquisition implemented | Exact telemetry implemented | Current integration state |
|---|---|---|---|
| Amp | CLI and access-token endpoint strategies | No token inference | Engine present; live sign-in/UI wiring pending |
| Claude Code | CLI-owned OAuth usage strategy | JSONL scanner with streaming-chunk deduplication | Engine and scanner present; ConPTY fallback and live UI wiring pending |
| Codex | OAuth credential usage strategy | Session/archive JSONL scanner with cumulative-counter deduplication | Engine and scanner present; app-server fallback and live UI wiring pending |
| Droid | Factory billing/usage strategy | Limits only | Engine present; WorkOS/WebView sign-in and real-account validation pending |
| Copilot | Dynamic quota/entitlement parsing and device-flow service | Reported credit/model data only where available | Engine present; registered client ID, UI flow, and contract validation pending |
| OpenCode Zen/Go | Local database discovery and local Go fallback | Exact message/part token scanner | Engine and scanner present; live WebView session and UI wiring pending |

Provider parsers are tolerant of unknown/reordered fields. The automated baseline covers representative dynamic-meter, scanner, persistence, pricing, refresh-concurrency, and deduplication behavior. Real provider accounts were not used to produce fixtures in this pass.

## 2. Native dashboard delivered

The presentation is native XAML. WebView2 is not created by the dashboard.

Implemented pages:

- Overview with exact-token, current API-equivalent, and coverage summaries.
- Reset Horizon with chronological cross-provider resets.
- Dynamic provider cards and urgent-meter ordering.
- Usage with daily telemetry, token classes, model resolution, reported service cost, and unresolved models.
- Providers with account/auth/health/coverage visibility.
- Diagnostics with privacy boundary, catalog/scanner/database status, and redacted attempts.
- Settings with appearance, startup, tray, alerts, privacy, retention, and update concepts.

The visual system uses graphite/porcelain resources, restrained provider accents, Familjen Grotesk, Azeret Mono, Mica with a solid fallback, visible status text, and compact adaptive navigation. The default palette is Tokyo Night among ten OpenCode-derived palettes.

The 700-pixel verification pass found and fixed clipped summary cards. The overview now stacks those cards at content widths below 820 pixels while preserving the horizontally scrollable Reset Horizon.

## 3. Verification evidence

### Automated

- `dotnet test`: 17 passed, 0 failed, 0 skipped.
- NuGet vulnerability audit: no known vulnerable direct or transitive packages.
- Debug x64 app build: succeeded.
- Release x64 self-contained publish: succeeded.
- Clean publish contains 12 compiled XBF layouts and `AiLimits.App.pri`.

The publish command used for the verified x64 artifact is:

```powershell
dotnet publish src/AiLimits.App/AiLimits.App.csproj `
  --configuration Release `
  -p:Platform=x64 `
  -p:PublishProfile=win-x64.pubxml
```

The output is `src/AiLimits.App/bin/x64/Release/published/AiLimits.App.exe`. The ARM64 profile is present but was not executed on ARM64 hardware.

### Interactive Windows smoke test

The clean self-contained Release executable was launched with `DOTNET_ROOT`, `DOTNET_ROOT_X64`, and `DOTNET_ROOT_X86` cleared.

Using the AI Limits window's Windows UI Automation tree, the verification exercised:

- Overview → Usage → Providers → Diagnostics → Settings → Overview navigation.
- Page-specific rendered content after each navigation.
- `Refresh all` through its invoke accessibility pattern.
- Responsive resize from the default 1280×840 window to 700×720.
- Automatic compact navigation at narrow width.
- Vertically ordered exact-token, API-equivalent, and coverage cards after resize.
- Continued process responsiveness after all interactions.

The final result was five of five pages rendered, refresh invoked, a 700-pixel accessible window, correctly stacked summary rows, and a responsive process.

The requested Computer Use plugin was installed during this task, but its callable controls were not exposed to the already-running Codex task. The equivalent app-only verification was completed through Windows UI Automation and app-window-only visual captures. No whole-desktop capture was used.

## 4. Security and privacy posture

The baseline enforces or models these boundaries:

- Credential storage uses Windows Credential Manager for app-owned secrets.
- CLI credentials are read in place.
- WebView profiles are isolated by provider/account and have a deletion path.
- SQLite stores normalized snapshots, aggregates, cursors, hashes, and redacted diagnostics—not prompts, completions, source code, cookies, OAuth tokens, or raw session records.
- Pricing never guesses through fuzzy model matching.
- Fetch diagnostics accept sanitized summaries instead of raw provider responses.
- The patched SQLite native bundle is pinned through central MSBuild metadata; the final NuGet audit is clean.

These rules still require adversarial/security tests around every completed live auth flow before public release.

## 5. Public-v1 blockers

The codebase is a working tracer-bullet implementation and native UI, not a release-complete eight-provider product. The following work remains mandatory:

1. Replace `DashboardFixture` with repositories, refresh streams, account filters, pricing results, and observable live view models in the app composition root.
2. Build the provider connection/disconnection screens and app-owned WebView2 OAuth sessions with strict origin allowlists.
3. Add Claude ConPTY `/usage` and Codex `app-server` fallbacks.
4. Complete Copilot device authorization using a registered GitHub client ID and persist it through the secret-store boundary.
5. Implement tray icon behavior, startup registration, session-lock suppression, native notifications, threshold/reset-cycle deduplication, and on-by-default alert and tray consent (disable in Settings).
6. Add provider-specific fixture matrices for every documented 401/403, malformed, renamed, partial, reordered, and account-mismatch case.
7. Run explicit real-account contract checks for all eight providers and redact the resulting fixtures.
8. Add scanner backfill scheduling, locked/truncated-file stress tests, and measured large-history performance.
9. Produce x64 and ARM64 portable archives with checksums, SBOMs, and provenance, then test extracted launches on Windows 10 22H2 and current Windows 11.
10. Execute the full accessibility matrix: Narrator, keyboard-only use, high contrast, 200% scaling, reduced motion, and localization.
11. Validate ARM64 on physical or virtual ARM64 hardware.
12. Measure the 500 ms cached-dashboard startup gate on the chosen reference machine.

Public v1 should remain blocked until those items and the eight-provider acceptance gates in `docs/05-implementation-plan.md` are satisfied.

## 6. Recommended next slice

The next vertical slice should wire one provider end to end—Codex is the best candidate—through account discovery, secret-safe acquisition, refresh coordination, SQLite snapshots, exact token aggregation, models.dev pricing, and live Overview/Usage binding. Once that seam is proven, Claude and OpenCode can reuse the same UI and orchestration path before adding the three limits-only providers.
