# 10 — Review remediation plan

Handoff plan for the two findings raised in the July 2026 review of
`fix/quotaboard-review-findings`, after validation against the code, the local
usage database, and the repository's GitHub configuration.

Both findings are real. Neither is P1. The re-rating and the reasoning behind
each fix are recorded below so the engineer picking this up does not have to
re-derive them.

## Summary

| ID | Item | Reported | Validated | Effort |
|----|------|----------|-----------|--------|
| U1 | Chart overflow series share the Others brush | P1 → | **P2** | ~half day |
| U2 | `ChartSeries5Brush` is a twin of `ChartSeries3Brush` in the XAML fallback | not reported | **P3** | ~15 min |
| C1 | Cline session store races across processes | P1 → | **P2** | ~1–2 days |
| A1 | High-contrast chart ramp uses palette colors, not system colors | not reported | **investigate** | ~1 hr to triage |

Suggested order: U2 → U1 → C1. U2 and U1 are contained UI work in one area;
C1 needs a design decision.

---

## U1 — Overflow chart series are indistinguishable

### Problem

`ChartSeriesBrushResolver.ResolveResourceKey` (`src/AiLimits.Application/Usage/ChartSeriesBrushResolver.cs:32-35`)
returns `ChartSeriesOthersBrush` for every legend index at or past 6.
`UsageAnalyticsQuery.BuildChart` (`src/AiLimits.Application/Usage/UsageAnalyticsQuery.cs:342-351`)
keeps *all* series when the user has explicitly selected categories, and
suppresses the pooled Others bucket in that case. `UsagePage.RenderChart`
(`src/AiLimits.Presentation.WinUI/Pages/UsagePage.xaml.cs:458-464`) colors
legend markers and bar segments from that map.

With eight or more explicitly selected categories, indices 6 and 7 both render
in the Others gray, so two real categories are visually identical in both the
legend and the stacked bars. With exactly seven, the seventh real category is
painted in the Others color, which reads as a pooled bucket that does not exist.

This violates two rules already written down in
[08 — Design tokens](08-design-tokens.md#2-chart-series-rules): rule 1 ("no
twins") and rule 3 ("Others is not a series — if Others ever looks like a
seventh category, the ramp failed").

### Reachability

Confirmed against the local database at
`%LOCALAPPDATA%\QuotaBoard\ai-limits.db`: `daily_usage` holds 41 distinct
`raw_model_id` values, 14 distinct services, and 7 distinct providers. The
facet lists are `SelectionMode="Multiple"` (`UsagePage.xaml:18`) with no cap, so
eight selections is eight clicks.

### Fix

Cap the distinctly-colored series at the six-slot ramp and pool the remainder
into a real Others bucket **even when the selection is explicit**. Do not extend
the ramp past six: the ramp is duplicated across ten palettes in `ThemeCatalog`,
and rule 1 forbids twins in any of them — finding eight to ten mutually distinct,
mode-correct hues for ten palettes is a much larger job with a worse failure mode.

In `BuildChart`, `explicitSelection` should stop controlling *how many* series
are kept and control only the cut-off:

- implicit selection: `totals.Take(3)`, remainder pooled into Others (unchanged)
- explicit selection: `totals.Take(6)`, remainder pooled into Others (new)

The `othersTokens` computation and the Others legend entry then apply in both
branches; the existing segment-level pooling at `:372-374` already handles the
rest correctly because it is driven by `primaryKeys`.

Because pooling can now hide a category the user explicitly asked for, the
Others legend row must say so. Add a localized suffix or tooltip on the Others
entry when it exists under an explicit selection — e.g. "Others (N more
selected)" — so the user can tell the difference between "everything else" and
"the tail of what I picked". New resw strings must be added to every language
file; remember that an empty `value` element breaks the resw loader.

Design rule 4 ("color is never the only channel") is worth a look while in here:
the stacked bars carry hover detail but no direct labels or legend shapes. Out
of scope for this item unless it is cheap — raise it as a separate backlog entry
in [09 — Usage critique](09-usage-critique.md) if not.

### Files

- `src/AiLimits.Application/Usage/UsageAnalyticsQuery.cs` (`BuildChart`)
- `src/AiLimits.Presentation.WinUI/Pages/UsagePage.xaml.cs` (Others legend label)
- resw files for the new string

`ChartSeriesBrushResolver` needs no change: once no more than six non-Others
entries reach it, the `legendIndex >= SeriesKeys.Length` branch becomes
unreachable defensive code. Leave it and keep its test.

### Acceptance

- Selecting 8+ models with Series = Model yields at most 7 legend rows: six
  distinctly colored plus one Others.
- The Others row is labeled so it is clear it pools selected-but-untinted
  categories.
- Bar segment totals still sum to the bucket total (no double counting through
  the pooled bucket).
- Implicit selection behavior (top 3 + Others) is unchanged.

### Tests

Extend the existing `UsageAnalyticsQuery` tests: eight explicitly selected
categories produce seven legend entries, exactly one of which has
`IsOthers == true`, and the sum of segment tokens per bucket equals
`bucket.Tokens`. Assert the implicit path still yields top 3 + Others.

---

## U2 — `ChartSeries5Brush` duplicates `ChartSeries3Brush` in the XAML fallback

### Problem

`src/AiLimits.Presentation.WinUI/Themes/PrecisionObservatory.xaml` declares
`ChartSeries3Brush` and `ChartSeries5Brush` with identical colors in both
variants: `#FFB15C00` at lines 38/40 (light) and `#FFFF966C` at lines 77/79
(dark).

This is the "Tokyo Night series 5 was a second orange" twin that
[08 — Design tokens](08-design-tokens.md#2-chart-series-rules) records as fixed
in July 2026. The fix landed in `ThemeCatalog.Tokyonight` (series 5 is now
`#73daca`/`#118c74`, `ThemeCatalog.cs:32`) but the static XAML dictionary was
never updated with it.

### Why it is only P3

`ThemeService.Apply` merges the generated dictionary onto
`Application.Current.Resources` last (`ThemeService.cs:84-91`), so the catalog
values win at runtime and the twin does not reach the screen. It surfaces only
if theme application throws — the whole block is wrapped in a catch-all at
`ThemeService.cs:111` — and at design time.

Verified by reading `Apply`, not by running the app.

### Fix

Set `ChartSeries5Brush` to the catalog's Tokyo Night series-5 color:
`#FF118C74` in the light dictionary (line 40) and `#FF73DACA` in the dark one
(line 79). While there, diff every `ChartSeries*Brush` in the XAML against
`ThemeCatalog.Tokyonight` — this file is the default/design-time mirror of that
palette and may have drifted elsewhere too.

### Tests

Add a guard so the two cannot drift again: a test that parses
`PrecisionObservatory.xaml` and asserts each `ChartSeriesNBrush` in the light
and dark dictionaries equals the corresponding `ThemeCatalog.Tokyonight`
`ChartSeries[N-1]` color for that mode. A cheaper alternative is a test over
`ThemeCatalog` alone asserting no palette contains two identical series colors
in either mode — worth adding regardless, since it enforces rule 1 for all ten
palettes.

---

## C1 — Cline session store races across processes

### Problem

`ClineSessionStore` (`src/AiLimits.Infrastructure/Providers/Cline/ClineSessionStore.cs`)
stages a session under one fixed set of keys and commits it with one fixed
marker, with no locking and no per-save identity. Two QuotaBoard processes
refreshing Cline at the same time can interleave:

1. Process A stages access `A1`, expiry `E1`, refresh `R1`, sets the commit marker.
2. Process B stages access `A2`, overwriting A's staging key.
3. A promotes, reading `A2` + `E1` + `R1` into the live keys.

The result is a live session pairing one response's access token with another
response's expiry and refresh token.

A second interleaving the original review did not mention: A's
`CleanupStagingAsync` (`:238-244`) can delete B's staging keys between B's phase
1 and B's `PromoteStagingAsync` (`:213-236`). B then reads four nulls, promotes
nothing, deletes the commit marker, and `TrySaveAsync` returns **`true`**.
`MigrateLegacyCacheAsync` deletes the plaintext cache on that `true` (`:179`,
`:187`). The practical loss is small — the competing writer stored an equally
valid session for the same single account — but the method's success contract is
violated and the migration path's safety argument rests on it.

### Reachability

- **Cross-process: yes.** There is no single-instance guard in the repository.
  The only `AppInstance` reference is `AppInstance.Restart` at
  `SettingsPage.xaml.cs:379`; there is no `FindOrRegisterForKey` and no named
  mutex. Two `QuotaBoard.exe` processes write the same Credential Manager keys.
- **In-process: no, today.** Cline discovers exactly one account
  (`ClineProviderAdapter.cs:34`), `LiveDashboardDataSource._loadGate` serializes
  loads (`:106`), and the only `RefreshRequest` is `Force: true` from `:366`.
  One latent door: `RefreshCoordinator`'s dedup key is
  `(Account, Revision, Force)` (`RefreshCoordinator.cs:14`), so a `Force:false`
  and a `Force:true` request for the same account are distinct keys and would
  run concurrently. No caller does this now; a future scheduled-refresh caller
  would open it.

Severity is P2 rather than P1 because the window is two processes refreshing
within milliseconds of each other, refresh only fires inside a 2-minute skew of
a ~55-minute token lifetime, and the damage is recoverable: the strategy prefers
the cached refresh token (`ClinePassLimitStrategy.cs:82`) and never clears it on
failure, so the Cline card sits in auth failure until the CLI writes a later
expiry — which any CLI-side refresh or re-login does.

### Fix

**Do not** just wrap save and recovery in a lock. Cline rotates refresh tokens
(`ClinePassLimitStrategy.cs:171-178`), so two processes refreshing concurrently
invalidate each other's refresh token *server-side* no matter how atomic the
store is. Store-level atomicity fixes the symptom and leaves the cause.

Take a named cross-process mutex (`Global\` or `Local\` per the decision below)
around the whole **load → decide → refresh → save** sequence in
`ClinePassLimitStrategy.FetchAsync`, not around `ClineSessionStore` alone. The
second process then blocks, and on acquiring the lock re-reads the store and
finds the session the first process just wrote — already fresh, so it skips the
refresh entirely.

Points to settle during implementation:

- **Scope.** `Local\` is per-session and does not cover a second user session on
  the same machine writing the same per-user credential store; `Global\` does but
  needs care over the ACL. Per-user credentials suggest a name derived from the
  user SID.
- **Timeout.** The lock must never hang a refresh. Pick a bounded wait (a few
  seconds — the refresh HTTP call has a 15-second timeout) and, on timeout,
  proceed without the lock exactly as today rather than failing the fetch. A
  best-effort lock that degrades to current behavior is strictly better than the
  status quo and cannot introduce a new hang.
- **Abandoned mutex.** A process killed while holding it raises
  `AbandonedMutexException` on the next acquirer. Treat it as acquired and let
  the existing commit-marker recovery in `LoadAsync` (`:72-78`) finish the
  interrupted promotion.
- **Placement.** The lock belongs in the strategy, but `ClineSessionStore` is
  where the invariant lives. Whichever is chosen, document it in the store's
  class comment — that comment currently explains atomicity in single-writer
  terms and will otherwise mislead the next reader.

Separately, fix the false success: `TrySaveAsync` must return `false` when
`PromoteStagingAsync` found no staging values to promote. That is cheap and
independent of the locking decision, and it makes the migration path's
file deletion honest.

If a named mutex is rejected, the alternative from the original review still
works for the store: generation-specific staging keys plus a commit pointer that
names the generation, switched atomically. It does not address the server-side
token rotation, so it should be paired with something that serializes the
refresh itself.

### Files

- `src/AiLimits.Infrastructure/Providers/Cline/ClinePassLimitStrategy.cs`
- `src/AiLimits.Infrastructure/Providers/Cline/ClineSessionStore.cs`

### Acceptance

- Two concurrent refreshes never produce a live session mixing fields from
  different responses.
- The second of two concurrent refreshes reuses the first's session instead of
  performing its own refresh round-trip.
- A save that promotes nothing returns `false`, and the legacy plaintext cache
  survives it.
- Lock acquisition failure or timeout never fails or hangs a fetch.

### Tests

The existing `ISecretStore` fake makes the interleavings testable without a real
vault: drive two `TrySaveAsync` calls with a scripted interleaving and assert
the live keys hold one coherent session. Add a test for the
promoted-nothing-returns-false case, and one for the migration path keeping the
file when that happens. Cross-process mutex behavior itself does not need an
integration test; a unit test around the timeout-degrades-to-unlocked path does.

---

## A1 — High-contrast chart ramp (investigate)

`ThemeDictionaryBuilder.Build` sets the `HighContrast` theme dictionary from the
palette via `BuildVariant(palette, dark: true)` (`ThemeDictionaryBuilder.cs:39`),
and that generated dictionary is merged last. The XAML high-contrast dictionary,
which maps every `ChartSeries*Brush` to `SystemColorHighlightColor`
(`PrecisionObservatory.xaml:110-116`), is therefore overridden — high-contrast
users appear to get palette colors rather than system ones.

Not verified by running the app under a high-contrast theme, and it is not clear
which behavior is intended: the XAML mapping makes all six series identical,
which is its own problem. Triage first — decide what high contrast should show,
then file it properly. Note that whichever way it goes, design rule 4 ("color is
never the only channel") matters more here than anywhere else.

---

## Do not do

- Do not fix C1 with a lock around `ClineSessionStore` alone. It leaves the
  server-side refresh-token rotation race untouched. See C1.
- Do not extend the chart ramp past six slots to fix U1. Ten palettes would each
  need new twin-free hues in two modes. See U1.
