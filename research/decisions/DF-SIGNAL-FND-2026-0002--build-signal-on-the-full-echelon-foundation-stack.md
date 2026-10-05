---
id: DF-SIGNAL-FND-2026-0002
title: Signal is built the same way as the other Echelon applications, on Limen, Forma, Aegis and Folio
status: accepted
version: 1.0.0
created: 2026-10-05
updated: 2026-10-05
owners:
  - repository-governance
review_cycle: on-trigger
supersedes:
  - DF-SIGNAL-FND-2026-0001
superseded_by: []
related_documents:
  - .echelon/foundations.json
  - aegis-boundaries.json
  - limen.config.json
  - package.json
  - Directory.Packages.props
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - .github/workflows/build.yml
  - .github/workflows/echelon-foundations.yml
  - research/decisions/DF-SIGNAL-FND-2026-0001--aegis-forma-folio-not-yet-applicable.md
tags: [governance, foundations, aegis, forma, folio, limen, architecture]
---

# DF-SIGNAL-FND-2026-0002: Signal is built the same way as the other Echelon applications

- **Date:** 2026-10-05
- **Status:** accepted
- **Decision type:** architecture and applicability (supersedes an applicability declaration)
- **Work items:** `WI-0022` (Limen boundary), `WI-0023` (Aegis), `WI-0024` (Forma), `WI-0025` (Folio)
- **Authority:** repository owner instruction, 2026-10-05: "we want all apps built
  the same way … Apply it to signal and chrona too."

## Context

[`DF-SIGNAL-FND-2026-0001`](DF-SIGNAL-FND-2026-0001--aegis-forma-folio-not-yet-applicable.md)
declared Aegis, Forma and Folio `required: false` because Signal had no
.NET/F# tier, no browser surface and no document output, and bound each
capability's restoration to the first change that added the boundary it
governs. The owner has now directed that every Echelon application be built
the same way. Under the repository's authority order (`AGENTS.md`: explicit
user instruction first) that settles the timing: this change adds the first
slice, and with it all three boundaries, so all three restoration triggers
fire together.

## What "built the same way" means

The pattern is the one Summa adopted in
`DF-SUMMA-FND-2026-0002` (kemiller2002/summa#13), itself taken from the
applications fully on the foundation stack (Vigila, Forma Studio, Tekmerion)
and from the Praxis foundations verifier (`src/Praxis.Cli/Foundations.fs` at
the pinned `a95dbf238e561eaac4b38ca7011efc1a496cf1c6`):

- every foundation `required: true`, at the versions the
  `echelon-registry` `echelon-current` channel selects (Aegis 1.0.0, Forma
  0.3.0, Folio 0.3.0, Limen 0.7.0);
- an F# engine compiled to .NET WebAssembly behind Limen's browser kernel,
  through a C# `[JSExport]` shim;
- Aegis at the engine's operational boundary, with `aegis-boundaries.json`;
- Forma for all interactive presentation and Folio for the printable
  projection, both consumed from the pinned release packages.

## The first slice

The boundaries need code that owns them; installing packages into an empty
repository would fail the verifier's intent ("merely adding a package does
not satisfy the contract"). The first slice is therefore a real, small
respondent path, chosen from requirements already in the repository:

- **Assessment:** the first three dimensions (D01-D03, items CORE-001 to
  CORE-015) of the Software Delivery Reality Assessment, copied verbatim from
  the draft item bank
  (`input-documents/software-delivery-reality-assessment-v1-item-bank.json`,
  SDRA 0.1.0-draft; a test holds the copy to the bank). Publication and
  canonicalization (WI-0003) replace the literal later.
- **Scoring:** the item bank's rule: mean of numeric answers / 4 x 100, at
  least three numeric answers per dimension. "Don't know", "Not observed" and
  "Not applicable" are answers but not numbers, and an unscored dimension
  says so (missing data is never zero by implication). Coverage travels with
  each result.
- **Output:** a result report printed through Folio: results at a glance,
  result integrity (coverage), scoring and methodology, and the responses.

Nothing is persisted: the session lives in the WebAssembly engine for the
life of the page. Storage, URL state, identity and the administrator
console remain the captured work items they were (WI-0004, WI-0005, WI-0008,
WI-0012).

## Decision

1. **All foundations required.** `.echelon/foundations.json` sets Aegis
   1.0.0, Forma 0.3.0 and Folio 0.3.0 to `required: true` (Limen 0.7.0, Ordo
   and Praxis unchanged). Folio's earlier `sourceCommit` pin is dropped because
   the dependency is now the immutable `v0.3.0` release artifact.
2. **Layout** (the directories the repository already reserved):
   - `src/Echelon.Signal.Engine` (F#, pure): the assessment, scoring, the
     respondent session's transitions and its view projection.
   - `src/Echelon.Signal.Application` (F#): Limen protocol codec, handshake
     (Core only; the slice negotiates no capability pack and requests no
     effect), and the Aegis boundary.
   - `src/Echelon.Signal.Browser` (C#, WebAssembly SDK): a one-method
     `[JSExport]` shim.
   - `web-kernel/limen-wasm.js`: the Limen `BrowserKernel` start-up, which
     also registers Folio; `web/` holds markup and page CSS only.
   - `limen.config.json` replaces `boundary.notApplicable` (WI-0021/WI-0022)
     with the two F# projects as engine and the shim and the two browser
     directories as kernel. `verify --strict` passes.
3. **Aegis** (`EchelonFoundry.Aegis.Core` 1.0.0, central package pin) is
   configured once and validated (`Bootstrap.validate`), and every kernel
   message runs inside `Aegis.capture` with one classifier:
   `SIGNAL.BOUNDARY.MESSAGE_INVALID` (a message that is not Limen's shape, or
   names an item or answer code the assessment does not define) and
   `SIGNAL.BOUNDARY.UNEXPECTED`. Asking for results with questions unanswered
   is a typed refusal shown as an ordinary alert, not a fault. Faults reach
   the page only as safe presentation, through Forma's fault component.
   Programming defects (an event name the engine does not know) fail loudly.
4. **Forma** (`@echelon-foundry/design-system` from the `v0.3.0` release
   tarball, locked with its integrity hash) is imported from the installed
   package (`dist/all.css`). Markup uses Forma's assessment patterns
   (question, ordinal scale, special choices, survey progress) and record
   header, alert, fault-inline, data-grid and status-lozenge, inside inert
   `<ef-*>` wrappers. State cues are `data-*` attributes set by the engine
   (`data-answer-state`, `data-state`); there are no inline styles and no
   `data-bind-style`, which Limen 0.7.0 refuses.
5. **Folio** (`@echelon-foundry/print-components` from the `v0.3.0` release
   tarball) supplies `print.css` (print media) and `register.js` (imported by
   the kernel). The page carries an `<ef-print-document>` projecting the same
   results and answers as the screen; nothing is rescored for print.
6. **Verification** is part of the build (`.github/workflows/build.yml`):
   .NET tests (scoring, the session, the Limen protocol and Aegis boundary
   with a collector sink, foundation conformance, and an HTML/engine binding
   agreement), Limen `verify --strict`, a WebAssembly publish, and a
   Playwright suite driving the page in Chromium. `npm test` runs the Limen
   check and the .NET tests, so `echelon-verification.yml` (which has no
   browser) exercises them too; `npm run test:browser` runs the browser suite.

## Consequences

- `foundations verify` checks Aegis, Forma and Folio for real; a regression in
  any of them fails the gate and the conformance tests.
- Building Signal now needs the .NET 10 SDK and Node 24 (Folio declares
  `>=24 <25`), as the other applications do.
- WI-0022 to WI-0025 are completed by this change; the review rule in
  `DF-SIGNAL-FND-2026-0001` no longer applies.

## Alternatives considered

- **Install the packages with an empty F# project.** Rejected: it would
  satisfy the gate without owning any boundary, which the verifier and this
  repository's evidence rules reject.
- **Pin Forma 0.2.0 and Folio commit `273b18f` as Vigila does.** Rejected: the
  instruction was to use what the `echelon-current` channel selects, and
  Summa uses the same releases.

## Revisit trigger

A new `echelon-current` channel selection for Aegis, Forma, Folio or Limen; a
Praxis foundations-verifier change; or the first persistence, identity or
administrator slice, each of which adds Aegis boundaries to declare.
