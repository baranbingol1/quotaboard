# 08 — Design tokens

The *what* to [07's](07-design-principles.md) *why*: the named values every
visual decision must be expressed in. If a number or color is not in this
document, it does not go in XAML. Principles cited as (P1–P5) refer to
[07](07-design-principles.md).

Three layers exist, and each rule below names the layer it governs:

> **Palette** (`ThemePalette`, per theme) → **semantic brushes**
> (`PrecisionObservatory.xaml`, `{ThemeResource}` keys) → **XAML call sites**

XAML references semantic brushes and named styles only. Palettes may be added
or replaced; semantic keys are stable; hex values at call sites are a review
finding (theme governance, 07).

## 1. Color slots

Every brush key, the palette role that feeds it, and its licensed meaning.
"Licensed" is literal: using a slot for anything else dilutes its meaning
across all ten palettes at once.

| Brush key | Palette role | Licensed meaning |
|---|---|---|
| `PageBackgroundBrush` | `Background` | The canvas under everything |
| `SurfaceBrush` | `BackgroundPanel` | Cards and panels resting on the canvas |
| `SurfaceSecondaryBrush` | `BackgroundElement` | Elements resting on a card (tracks, wells, inputs) |
| `SurfaceStrokeBrush` | `Border` | Resting card/panel outlines |
| `SurfaceStrokeActiveBrush` | `BorderActive` | Hover/focus/selected outlines |
| `TextPrimaryBrush` | `Text` | Values, titles, anything the user came to read |
| `TextSecondaryBrush` | `TextMuted` | Labels, eyebrows, metadata |
| `TextQuietBrush` | `BorderSubtle` | Captions that must not compete (timestamps, hints) |
| `HealthyBrush` | `Success` | "Within limits" state — the only non-neutral color allowed to mean *good* (P3) |
| `WarningBrush` | `Warning` | Action will be needed soon; tied to projected exhaustion, not a raw percentage (P3) |
| `CriticalBrush` | `Error` | Act now: exhausted, failed, or blocking |
| `FocusBrush` | `Primary` | Keyboard focus and interactive emphasis |
| `AccentBrush` | `Accent` | Brand moments and rare highlights — rationed, or it stops meaning anything (P3) |
| `ChartSeries1..6Brush` | `ChartSeries` | Categorical data, ordered by prominence (§2) |
| `ChartSeriesOthersBrush` | — | Pooled "everything else" bucket; always quieter than real series |
| `ChartBarBrush` | gradient | The single-series/volume bar when no breakdown is active |
| `Pill{Positive,Info,Warning,Neutral}{Background,Foreground}Brush` | derived | Status pills; foreground/background ship as pairs and are never mixed across pairs |
| `Incident{Background,Border,Foreground}Brush` | derived | The inline incident/alert banner; the only place warning-grade color may cover a region |

Severity rationing (P3) in one rule: **Healthy, Warning, and Critical brushes
color only state — never decoration, never hierarchy, never "importance."**
Importance is expressed with size, weight, and position (P4).

Dark-mode elevation is lightness, not shadow: `Background` is darkest,
`BackgroundPanel` lighter, `BackgroundElement` lightest. New surfaces must
enter this ladder, not invent off-ladder values.

## 2. Chart series rules

The categorical ramp is the most fragile token set because it repeats across
ten palettes. Four rules, all enforceable by inspection of `ThemeCatalog`:

1. **No twins.** No two series slots in one palette may resolve to the same
   or a neighboring hue, in either mode. *Fixed in July 2026: Tokyo Night
   series 5 was a second orange (now teal `#73daca`/`#118c74`); Everforest
   series 2 was a second green (now yellow `#dbbc7f`/`#dfa000`); Nord
   series 3 was a near-twin of series 1 (now aurora yellow `#EBCB8B`); Ayu
   series 5 was a near-twin of series 3 (now teal `#95E6CB`). Matrix is a
   deliberate monochrome-green ramp and is exempt by theme identity; check
   any new palette against this rule before it ships.*
   The Usage chart and legend bind to `ChartSeries1Brush`…`ChartSeries6Brush`
   by legend order, with `ChartSeriesOthersBrush` for the pooled "Others"
   bucket. The Model Mix list binds to the same ramp by row rank, so a model
   keeps one colour across both surfaces; brand hues cannot serve there,
   because every `claude-*` model normalises to the single Claude orange and
   the list would show a column of identical bars. `ProviderColors` stays the
   source of truth for brand identity on product chrome (provider cards,
   connections, tray) and for the Series-by-provider chart dimension.
2. **Slot 1 is the flagship's data voice.** Series 1 carries the dominant
   category and should be the palette's most legible color against
   `SurfaceBrush` — not its prettiest.
3. **Others is not a series.** `ChartSeriesOthersBrush` must sit visibly
   below the ramp in saturation. If "Others" ever looks like a seventh
   category, the ramp failed.
4. **Color is never the only channel (P1).** Stacked bars get direct labels
   or a legend with shapes; line/mark charts differ in dash or marker when
   series count exceeds three. Verify each palette with a color-vision
   simulator before shipping it.

## 3. Typography

Two families, fixed roles: **Familjen Grotesk** (`ContentFont`) for prose and
labels, **Azeret Mono** (`MetricFont`) for numbers, eyebrows, and anything
tabular. Themes may suggest alternates; the Settings font pick always wins.
Mono is never used for sentences; Grotesk is never used for values (P4).

The scale — eight steps, all others retired:

| Style | Size | Weight | Font | Tracking | Used for |
|---|---|---|---|---|---|
| Eyebrow | 11 | Regular | Metric | +150 | Section labels, `OVERVIEW — ALL PROVIDERS` |
| Meta | 12 | Regular | Content | 0 | Timestamps, chart axes, secondary rows |
| Body | 14 | Regular | Content | 0 | Descriptions, settings text, empty states |
| Lead | 17 | Medium | Content | 0 | Card titles, emphasized rows |
| Section | 20 | SemiBold | Content | 0 | Section titles |
| Subtitle | 24 | SemiBold | Content | 0 | Rare: dialog/page-level subheads |
| Metric | 28 | SemiBold | Metric | 0 | Hero numbers; single line, trims, never wraps (P4) |
| Page title | 36 | SemiBold | Content | 0 | One per page, no exceptions |

Line height: 1.2 for 20px and up, 1.45 below. Weights are Regular / Medium /
SemiBold only — Bold is not in the vocabulary.

**Snap map for the legacy ramp:** `9, 10, 11.5 → 11 or 12` (pick per context,
default 12); `13 → 12`; `16, 18, 19 → 17`; `21 → 20`; `27, 30, 31 → 28`.
The named styles in `PrecisionObservatory.xaml` (`EyebrowTextStyle`,
`PageTitleTextStyle`, `SectionTitleTextStyle`, `MetricTextStyle`) already
implement four of the eight; the remaining four get added as named styles in
the snap pass so call sites stop carrying raw `FontSize`.

## 4. Spacing

Scale (4px base), with the names XAML uses via `x:Double` resources:

| Token | Value | Licensed use |
|---|---|---|
| `SpaceXs` | 4 | Icon-to-label, inline micro gaps |
| `SpaceSm` | 8 | Related items inside a component |
| `SpaceMd` | 12 | Component internal groups; compact-zone stacks |
| `SpaceLg` | 16 | Card padding (compact), list stacks |
| `SpaceXl` | 20 | Card padding (comfortable), section-internal gaps |
| `Space2xl` | 24 | Between cards, grid gaps |
| `Space3xl` | 32 | Between page sections |
| `Space4xl` | 48 | Page-level separation |
| `Space5xl` | 64 | Reserved: empty-state centering, hero offsets |

Context rules from the spacing skill, binding on reviewers: related items get
`Sm/Md`; distinct sections get `2xl/3xl`; a gap larger than its container's
internal padding signals "new group," so it must mean exactly that.

**Density modes** (07): Comfortable is the default; Compact applies to data
zones (breakdown tables, Diagnostics rows, Model mix rows) and drops inset
and stack by one step. A zone declares its mode once; children inherit.

The legacy sediment (~70 distinct values, mode cluster `16/8/6/18/14/10/5`)
snaps to the nearest step in the cleanup pass: `5→4 or 6→Sm`, `6→8`,
`7→8`, `9,10→8 or 12`, `11→12`, `14→12 or 16`, `15→16`, `18→16 or 20`,
`22→24`, `26→24`. Judgment calls (12 vs 16) are decided per zone, not per
call site.

## 5. Radii and borders

Three radii, nothing else: `4` — small controls and pills' inner elements;
`8` — buttons, inputs (retires the current `7`); `12` — cards and surfaces.
Pills are fully round. Border thickness is `1` everywhere; emphasis is
expressed by swapping `SurfaceStrokeBrush` → `SurfaceStrokeActiveBrush`,
never by thickening.

## 6. Motion

Calm panel, so motion informs and then disappears (P3): fades and slides
`120–250ms`, easing out; list items stagger `30–50ms`; shimmer only on
skeletons whose shape matches the incoming content. Content fades in — it
never pops, and layout must not shift when data arrives (reserve skeleton
space). Every animation honors the OS reduced-motion setting by collapsing
to an instant state change. Refresh preserves scroll position and selection.

## 7. Enforcement

- New XAML references `{ThemeResource}` brushes, named text styles, and
  `Space*` doubles only. Raw hex, raw `FontSize`, and raw spacing literals
  in a diff are review findings with this document as the citation.
- The `quotaboard-design` agent skill (`.agents/skills/`) loads this file
  and 07 before any agent touches `Presentation.WinUI`.
- A token is added exactly as often as it is needed twice. Propose in a PR
  against this document; single-use values stay out of the vocabulary.
