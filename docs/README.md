# QuotaBoard design notes

Working documents for the Windows app. They record the decisions behind the
shape of the code; the code itself is the specification.

## Contents

- [04 — Windows application blueprint](04-windows-blueprint.md): stack, project
  structure, GUI, provider contracts, storage, and delivery phases.
- [05 — Implementation plan](05-implementation-plan.md): phase ordering,
  acceptance criteria, and the locked product decisions.
- [06 — Implementation status](06-implementation-status.md): what is built, what
  is partial, and what is not started.
- [07 — Design principles](07-design-principles.md): the product personality,
  the five ranked principles that resolve design trade-offs, and the
  theme/spacing governance rules every visual change is judged against.
- [08 — Design tokens](08-design-tokens.md): the named color slots, chart
  series rules, type scale, spacing scale, radii, and motion values that all
  XAML must be expressed in.
- [09 — Usage page critique](09-usage-critique.md): the first critique pass
  against the design docs — what was fixed, what was examined and accepted,
  and the ranked design backlog.
- [Provider release checklist](provider-release-checklist.md): what must be
  true before a new provider adapter ships.

The numbering starts at 04 because the first three documents were research notes
on a different application and were removed when this repository was opened.

## The shape of a provider

Everything under `AiLimits.Infrastructure/Providers` follows one pipeline:

> provider descriptor → ordered fetch strategies → typed normalized snapshot →
> account/authority checks → persistence → UI

Two properties of that pipeline are load-bearing and easy to break.

**Fallback is selective, not general.** Each strategy declares its own
`FallbackPolicy`. "Catch anything and try the next source" looks resilient but
produces confidently wrong numbers: it lets an authentication problem be
answered with a different account's data, or a provider's contract change be
papered over by a stale reading. Only failures that genuinely mean "this source
cannot answer right now" fall through.

**A snapshot states how complete it is.** `SnapshotCompleteness.Authoritative`
asserts that the listed meters are *all* the meters that exist, so anything
absent is deleted from the card. A strategy that could only parse part of a
response must report `Partial`, which carries the previously known meters
forward with a Stale badge. Reporting `Authoritative` by reflex is how a
provider's meters silently disappear.

Diagnostics are held to the same standard: everything persisted or displayed
passes through `DiagnosticRedactor`, and a scan that failed raises
`TokenScanException` rather than returning an empty sequence that reads as
"nothing new".
