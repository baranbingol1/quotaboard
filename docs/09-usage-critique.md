# 09 — Usage page critique and design backlog

The first critique run against [07](07-design-principles.md) and
[08](08-design-tokens.md), July 2026. Method: the seven-dimension pass
(hierarchy, affordance, density, color, typography, composition, consistency)
on the Usage page — the workbench, where friction is severity-one by
definition — with observations beyond Usage where they surfaced. Each finding
cites the principle it violates.

Severity scale: **S1** interrupts reading or misstates data; **S2** adds
friction to exploration; **S3** polish.

## Fixed in this pass

| # | Finding | Principle | Severity | Fix |
|---|---|---|---|---|
| 1 | **Chart series twins.** Tokyo Night series 5 was the same orange as series 3 (Anthropic and Factory read as one band in stacked bars); Everforest series 2 duplicated series 1's green; Nord 3 and Ayu 5 were near-twins of neighbors. | P1, P4 | S1 | Usage chart + legend now bind to `ChartSeries1Brush`…`ChartSeries6Brush` by legend order, with `ChartSeriesOthersBrush` for the pooled bucket. New distinct hues in `ThemeCatalog` (Tokyo Night teal, Everforest yellow, Nord aurora yellow, Ayu teal). Full ramp audit recorded in [08 §2](08-design-tokens.md); Matrix exempt as deliberate monochrome. `ProviderColors` stays for brand identity on product chrome (cards, connections, tray, model-mix accent strips). |
| 2 | **Compare picker clipped** to "Previc…" — two half-width columns in the 210px query panel left ~86px per ComboBox. | P1 (illegible = unknowable state) | S1 | Time grain and Compare are now full-width stacked rows, matching Date range. |
| 3 | **Facet labels clipped** mid-glyph ("Anthropic (Claude (") — `DisplayMemberPath` renders with no trimming in the narrow checkbox lists. | P1, P4 | S1 | Shared `ItemTemplate` on `FacetListStyle`: `CharacterEllipsis` + tooltip with the full label. Covers all four facet lists. |
| 4 | **Duplicated hero metric.** "MATCHING DATA" read "164 rows · 4.04B tokens" directly under the TOKENS OVER TIME hero showing the same 4.04B. | P4 | S2 | Card now reads "164 rows"; the composition detail line below is unchanged. |

## Examined, no change

- **"Nearly empty, vertically clipped" sparkline row (Model mix).** Not
  reproducible from markup: the row template reserves a full 30px track and
  zero-activity days render zero-height bars — which is the honest answer
  (P1), not a defect. The screenshot appearance is consistent with the
  column viewport ending mid-row. Re-flag only if seen in the running app
  with the row fully in view.
- **Breakdown bars share one hue.** The "BREAK DOWN BY" bars use the default
  progress fill rather than series colors. Accepted: the bars encode share,
  not category identity, and linking their color to the chart ramp would
  imply a connection that only holds when the breakdown matches the chart
  grouping (P1). Revisit if cross-highlighting is ever added.

## Backlog, ranked

1. **Spacing snap pass** (S2, P4/consistency): ~70 distinct spacing values
   across the pages collapse onto the 4px scale ([08 §4](08-design-tokens.md)),
   including the four named text styles not yet created. One dedicated PR,
   page by page; not folded into feature work.
2. **Type ramp snap** (S2): 17 raw `FontSize` values (9, 10, 11.5, 13, 16,
   17, 18, 19, 21, 27, 30, 31…) retire to the eight-step ramp; remaining
   call sites switch to named styles.
3. **Meter severity mapping on Overview/Providers** (S1 candidate, P3):
   "Primary 100% left" meters currently render identically at every level,
   so the one provider nearing exhaustion cannot be spotted without reading
   every number. Design: threshold the meter fill to `WarningBrush` /
   `CriticalBrush` on projected exhaustion before reset — never on raw
   percentage — with the exact figure always beside it. Needs the
   projection rule defined first (what counts as "exhaustion before reset"
   per provider cadence); that definition is product logic, not pixels.
4. **Overview stat-row relevance** (S3, P2): the third hero stat ("1 tray
   app") describes the app, not the user's standing. Candidate replacement:
   worst-off provider or next reset. Product call, not made here.
5. **Sparkline zero-day affordance** (S3): if honest zero-bars read as
   "broken" rather than "quiet," consider a 1px baseline tick at zero.
   Defer until 3 lands; the severity colors may already solve the reading.

## Observations beyond Usage

- **Overview** holds up well against P2: hero stats lead, meters sit above
  charts, reset countdowns are unmissable. The greeting line ("Good
  evening") is the only element that doesn't answer a question; it earns
  its place as orientation, not decoration — borderline, watch it.
- **Settings** is a form and behaves like one (P3): no severity color
  anywhere, clear sections. The theme picker is the one place accent color
  is licensed to be playful.
- **Diagnostics** is the strongest page against P1 — per-source answers,
  latencies, and remediation are all visible. It is the reference
  implementation of "show uncertainty."
