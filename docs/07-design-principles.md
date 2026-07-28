# 07 — Design principles

The personality QuotaBoard is designed against, and the ranked principles that
resolve design trade-offs. These were derived in a directed interview (July
2026); they codify instincts the app already half-had rather than impose new
ones. Every visual change — human or agent-authored — is judged against this
document.

## Personality

QuotaBoard is a **calm instrument panel**. Three words:

- **Glanceable** — it is a utility users check, not a destination they browse.
- **Honest** — the product's value is showing real provider state: stale,
  failed, and unknown included.
- **Quiet** — a dashboard where everything demands attention teaches users to
  ignore all of it.

## The principles, in priority order

When principles conflict, the higher one wins. The ranking is deliberate:
honesty outranks speed, speed outranks calm, calm outranks elegance.

### 1. Show uncertainty, never fake it

Stale, failed, and unknown are first-class visual states, never silently
hidden behind cached numbers or optimistic placeholders.

- *Application:* a provider whose newest source failed shows the last known
  values with a visible Stale treatment and a timestamp; a provider that has
  never answered says so in words. Diagnostics rows state what to do next.
- *Counter-example:* a spinner that never resolves; a cached quota presented
  as live; an empty chart that could be mistaken for "no usage."
- *Trade-off:* the UI looks messier than tools that fake certainty. That
  messiness *is* the product. This principle already exists in the data layer
  (`SnapshotCompleteness`, selective fallback); it applies to pixels equally.

### 2. Answer in five seconds

Opening the app answers "am I okay?" without scrolling or decoding. The
worst-off provider is findable instantly; exploration comes second.

- *Application:* hero metrics lead the Overview; meters sit above charts;
  reset countdowns are visible without interaction.
- *Counter-example:* burying quota percentages below decorative content;
  greeting banners that push live state off the first screen.
- *Trade-off:* Overview sacrifices narrative flow for speed. That is what
  makes it a utility.

### 3. Calm until action is needed

Severity color appears only when there is something for the user to *do*.
A meter at 100% remaining and one at 2% must never look identical — but one
at 60% should not be orange either. Alarm fatigue kills dashboards.

- *Application:* warning severity is tied to projected exhaustion before
  reset, not to crossing an arbitrary percentage; neutrals carry the default
  state; the exact number is always shown alongside so threshold-watchers
  lose nothing.
- *Counter-example:* six identical "100% left" meters where one struggling
  provider cannot be spotted; red used for information.
- *Trade-off:* some users want raw thresholds regardless of actionability.
  The exact figures remain visible; only the *color* is rationed.

### 4. Numbers are the hero

Metric typography (Azeret Mono) is the visual lead; chrome serves data-ink.
No decoration earns pixels unless it carries information.

- *Application:* bar charts start at zero; series are labelled directly where
  layout allows; the same metric is never rendered twice in proximity.
- *Counter-example:* gradient-heavy chart chrome; a stat card whose value
  repeats the sentence in the card beside it.
- *Trade-off:* less "marketing pretty." The audience reads instruments for a
  living; they call it pretty when it reads well.

### 5. Say it once, then behave it

The local-only, read-only, no-telemetry fact is stated clearly exactly where
trust is decided — first launch, Settings/About, release notes — then proven
by behavior, not repeated in chrome.

- *Application:* one quiet "On-device" badge where data provenance matters;
  plain-language source labels in Diagnostics.
- *Counter-example:* privacy banners on every page; reassuring copy in every
  empty state.
- *Trade-off:* none. Repeating reassurance converts it into noise, and noise
  violates principle 3.

## The two page modes

The pages are designed under different rules because they serve different
moods. Conflating them is the most common design error here.

**Overview is the instrument panel.** Principles 2 and 3 dominate: speed,
calm, severity rationing. Interaction is minimal; the page succeeds when the
user closes the app within seconds, informed.

**Usage is the workbench.** The user hangs out here, asking different
questions each time — volume trends, what is eating tokens, what it would
have cost. The page's job is to make exploration frictionless, not to crown
one hero answer. Principle 4 dominates: dense data is welcome, compact
spacing is the default zone mode, filters and breakdowns get the prime
surface. Defects that interrupt play — truncated labels, indistinguishable
series colors, duplicated metrics, clipped sparklines — are severity-one
design bugs on this page even when they look cosmetic.

Diagnostics is honest paperwork (principle 1), Settings is a form
(principle 3: nothing shouts), Providers is the instrument panel applied to
connections.

## Theme governance

- **Tokyo Night Dark is the flagship.** Screenshots, critiques, docs, and
  release assets use it. Design decisions are made there and inherited by
  the other palettes.
- **Rules are written over semantic slots, never hex values.** "The
  worst-off meter uses `CriticalBrush`," not "#FF757F". The ten palettes are
  user culture — editor themes people already love — so meaning must be
  palette-independent. Themes change; meaning doesn't.
- **Light parity is real for five palettes** (Tokyo Night, Catppuccin,
  Gruvbox, Everforest, One Dark). The other five are dark-only by
  construction; that is accepted, and the theme picker should not pretend
  otherwise.
- **High Contrast stays system-driven** and is never overridden with brand
  colors.

## Spacing and density

One scale, named steps, no orphans: `4 / 8 / 12 / 16 / 20 / 24 / 32 / 48 / 64`
(4px base). New XAML references named steps only; raw numbers in a diff are
a review finding. The legacy surface (~70 distinct values) is snapped to the
scale in a dedicated cleanup pass, not opportunistically.

Two density modes, declared per zone:

- **Comfortable** — default; card grids, Overview, Settings.
- **Compact** — data zones; one step down; breakdown tables, Diagnostics
  rows, Model mix rows.

## What this document is not

Token values, component anatomies, and copy rules live in their own
documents (08 onward). This file decides *why*; those decide *what*; the
XAML decides *how*.
