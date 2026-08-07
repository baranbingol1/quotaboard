# AI Limits for Windows — Accepted Implementation Plan

Status: **accepted and in implementation**  
Last updated: 2026-08-07

## 1. Product boundary

AI Limits is a local-first Windows desktop application built with .NET 10, C#, WinUI 3, the Windows App SDK, CommunityToolkit.Mvvm, and native XAML. WebView2 is restricted to provider authentication. Local data uses SQLite in WAL mode; app-owned secrets use Windows Credential Manager or current-user DPAPI. Releases are unsigned, self-contained x64 and ARM64 ZIP archives with checksums, SBOMs, and GitHub provenance attestations. The minimum operating system is Windows 10 22H2 build 19045, with Windows 11 visual enhancements when available.

There is no AI Limits cloud account, remote proxy, telemetry backend, browser-cookie import, generic pay-as-you-go API monitoring, or remotely executable provider-rule system. Provider code changes ship only through public releases produced and attested by GitHub Actions. models.dev pricing is the only remotely refreshed application metadata.

The solution is split into these layers:

- `AiLimits.Domain`: provider-independent identities, meters, snapshots, token events, pricing types, and invariants.
- `AiLimits.Application`: refresh coordination, snapshot reconciliation, pricing, history, alerts, and orchestration.
- `AiLimits.Infrastructure`: provider adapters, parsers, HTTP acquisition, SQLite, local scanners, and models.dev.
- `AiLimits.Platform.Windows`: secrets, isolated WebView2 profiles, process/ConPTY integration, tray, notifications, startup, and Windows session/power signals.
- `AiLimits.Presentation.WinUI`: pages, controls, view models, resources, themes, localization, and accessibility.
- `AiLimits.App`: packaged executable and composition root.

## 2. Dynamic limits and snapshots

Providers return arbitrary collections of `UsageMeter`; the UI never addresses a meter by array position or assumes names such as “5-hour”, “weekly”, or “Fable”. Meter identity uses a provider-issued identifier when available. Otherwise it is a deterministic hash of provider, raw JSON path, scope, and model. Adapter aliases add friendly labels for known keys. Unknown objects with recognizable utilization, quantity, window, or reset fields become meters automatically.

An authoritative snapshot may immediately activate or retire meters. A partial snapshot preserves missing prior meters as stale. Retired meters remain in history but disappear from live cards. Newly observed meters receive a temporary **New limit** annotation. A weekly-only Codex response does not create an empty short-window lane.

The canonical snapshot shape carries account identity, meters, balances, completeness, observation time, confidence, and JSON extensions. Provenance records acquisition strategy and a redacted source path without secrets or payload bodies.

## 3. Refresh architecture

`IProviderAdapter` describes account discovery, authentication capabilities, ordered limit-acquisition strategies, and optional exact-token sources. `ILimitFetchStrategy` exposes availability, fetch, typed fallback behavior, and sanitized diagnostics. `ITokenUsageSource`, `IModelResolver`, `IPricingCatalog`, `ISecretStore`, `IAccountRepository`, `ISnapshotRepository`, and `IUsageAggregateRepository` isolate the remaining responsibilities.

`RefreshCoordinator` scopes all state by provider and account. Monotonic generations and configuration revisions prevent obsolete results from overwriting account switches. Equivalent in-flight requests coalesce, global provider concurrency is capped at four, and publication occurs only after an account-authority check. Transient failures retain stale last-known-good data. HTTP, database scanning, process work, and pricing never execute on the UI thread.

## 4. Exact token and price-equivalent accounting

Token totals are exact-only. Provider quota percentages are never converted into tokens. A token event records account, service, raw model identifier, timestamp, input, output, cache-read, cache-write, reasoning, and a stable source-event identifier.

The model distinguishes:

1. The service through which a model was used, such as Copilot or OpenCode.
2. The underlying model vendor, such as OpenAI, Anthropic, or Google.
3. The models.dev entry used as the pricing source.

The models.dev API catalog is fetched at most every 24 hours with conditional requests. Each accepted body is validated, hashed with SHA-256, and cached; failures preserve the last valid catalog. Lookup uses an exact `(pricing provider, canonical model)` table and an explicit adapter-owned `(service, normalized raw model)` alias table. Normalization trims, lowercases, applies Unicode normalization, and collapses separators. Provider prefixes and dated suffixes change only through declared aliases—never fuzzy matching.

Current upstream direct-API list-price equivalents use exact input, output, cache-read, cache-write, reasoning, and long-context lanes. Any non-zero token class lacking a price makes that event unpriced. Unresolved models remain in token totals but never contribute dollars. Historical views recalculate against the current catalog and expose its timestamp and hash. Provider-reported spend and GitHub AI credits remain separate **Reported service cost** values and are never added to the API-equivalent total.

## 5. Persistence and privacy

SQLite stores provider accounts/config revisions; snapshots and dynamic meter rows; daily token aggregates by account, service, raw model, canonical model, and token class; scanner cursors and deduplication fingerprints; pricing catalog metadata and resolutions; redacted fetch attempts; and alert deduplication state. Initial scanners ingest the newest 30 days first and backfill older data at low priority. Daily aggregates remain until explicitly cleared.

Prompts, completions, source code, cookies, OAuth tokens, and raw session records are never written to SQLite. CLI-owned credentials are read in place. Each app-owned browser session has an isolated WebView2 data directory per provider/account, deleted on disconnect.

## 6. Required providers

| Provider | Subscription-limit acquisition | Exact token/cost coverage |
|---|---|---|
| Amp | `amp usage`, then Amp access-token endpoint, then isolated WebView2 | Limits and balances only in v1; never infer tokens |
| Claude Code | CLI-owned OAuth credentials, OAuth usage API, ConPTY `/usage`, optional isolated WebView2 | Exact JSONL scan with streaming-chunk deduplication |
| Codex | `.codex/auth.json` OAuth usage, then `codex app-server` JSON-RPC | Exact active/archived JSONL scan with cumulative-counter deduplication |
| Droid | Official Factory API/session token, then isolated WorkOS/Factory WebView2 | Limits only in v1 until exact telemetry exists |
| Copilot | GitHub device authorization and quota/entitlement fetch; authorized billing enrichment | AI-credit/model reporting where available; no personal token inference |
| OpenCode | One provider for Zen and Go; isolated `opencode.ai` session plus local Go fallback | Exact models/tokens from the read-only OpenCode database |

Copilot represents legacy request quotas and current AI credits as dynamic meters. OpenCode first runs `opencode db path`, then falls back to `%USERPROFILE%\.local\share\opencode\opencode.db`.

Every provider parser has fixtures for added, missing, reordered, malformed, and unknown meters. Multi-account isolation is mandatory across all eight providers.

## 7. Precision Observatory UX

The native adaptive `NavigationView` contains Overview, Usage, Providers, Provider detail, Diagnostics, and Settings. Cached data renders immediately and background updates preserve navigation and scroll state.

Overview presents global period/account filters, exact-token totals, current API-equivalent cost, coverage, provider cards, and the signature **Reset Horizon**: a chronological strip of upcoming resets across all visible accounts. A provider card shows its two most urgent meters and collapses the remainder behind “N more limits”. Usage provides daily provider/model charts, token-class breakdown, model table, reported service cost, and unresolved-model diagnostics. Provider pages expose accounts, auth source, capabilities, health, history, balances, acquisition attempts, refresh, and disconnect. Diagnostics includes only redacted attempts, scanner/catalog state, versions, and an export action.

The design uses graphite/porcelain light and dark themes, restrained provider accents, amber for approaching limits, and vermilion for critical limits. The default palette is Tokyo Night among ten OpenCode-derived palettes. Status is never color-only. Familjen Grotesk is bundled for navigation/content and Azeret Mono for telemetry under their OFL licenses. Mica is optional on supported Windows 11 systems; Windows 10 uses a performant solid background. Motion is coordinated, restrained, and removed when reduced motion is enabled. There are no generic purple gradients, decorative gauges, glass-heavy cards, or fixed meter lanes.

Keyboard navigation, visible focus, screen-reader meter descriptions, localization-ready strings, high contrast, 200% scaling, and a narrow single-column layout are acceptance requirements. A meter announces provider, account, label, used/remaining value, reset, freshness, and confidence.

## 8. Tray, alerts, and lifecycle

The v1 application has a tray icon while running. Left click activates the dashboard; the context menu includes refresh, pause, open, and quit. Its tooltip reports the most urgent meter and data age. Start with Windows is off by default.

Alerts are enabled by default and can be disabled in Settings, with suggested 80% and 95% thresholds and a 30-minute reset reminder. Deduplication keys include provider, account, meter, threshold, and reset cycle. Browser acquisition is suppressed while the Windows session is locked.

## 9. Milestones

1. **Foundation tracer bullet:** layers, contracts, migrations, fake provider, coordinator, diagnostics, native shell, and arbitrary persisted fixture meters.
2. **Dynamic dashboard, Codex, and Claude:** complete visual system, real limits, exact scanners, pricing, model resolution, and multi-account isolation.
3. **OpenCode:** unified Zen/Go limits, isolated sign-in, database discovery, exact token ingestion, and vendor/model resolution.
4. **Amp, Droid, and Copilot:** ordered strategies, auth flows, dynamic meters, balances, AI-credit enrichment, and explicit coverage states.
5. **Windows/release hardening:** tray, alerts, startup, power/session behavior, portable packaging, provenance, compatibility, accessibility, attribution, and release docs.

Public v1 is blocked until all eight providers pass their real-account limit-acquisition release checks.

## 10. Verification and acceptance gates

Automated coverage includes dynamic meter evolution; provider success/auth/timeout/malformed/fallback behavior; account switches and generation races; scanner duplicates/truncation/locked files/backfill; exact and unresolved model aliases; pricing lanes and catalog failures; secret/diagnostic redaction; and native keyboard, screen-reader, theme, scaling, layout, and visual snapshots. CI uses fake processes, HTTP transports, OAuth payloads, databases, and secret stores. Real-account tests are explicit local release checks and captured fixtures must be redacted.

Release gates are:

- A never-before-seen meter renders and persists without WinUI changes.
- Missing/returning Codex short-window meters need no placeholder or UI change.
- All eight providers isolate multiple accounts.
- Codex, Claude, and OpenCode produce exact history when local telemetry exists.
- Unsupported coverage clearly explains exclusions.
- Dollar totals expose catalog time/hash and exclude unresolved models.
- SQLite, logs, crash output, and exports contain no secret, prompt, or source content.
- Cached dashboard render is under 500 ms on the reference machine; WebView2 is absent outside sign-in.
- Release archives pass Windows 10 22H2 and current Windows 11 smoke, accessibility, extracted-launch, and provenance checks.

## 11. Locked decisions

The product/namespace names are `AI Limits` and `AiLimits`. The main UI remains native WinUI 3. OpenCode Zen and Go are one provider. All eight providers and multiple accounts are required for public v1. Subscription access keys and official CLI credentials are allowed; generic API-account monitoring is not. Zen balance is an explicit exception. Token totals are exact-only. The headline dollar metric is today’s upstream API list-price equivalent. Provider adapters update only with published releases. App-owned isolated WebView2 sessions replace external-browser cookie extraction.
