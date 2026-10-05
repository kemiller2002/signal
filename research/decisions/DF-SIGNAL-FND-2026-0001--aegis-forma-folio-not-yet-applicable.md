---
id: DF-SIGNAL-FND-2026-0001
title: Aegis, Forma and Folio are declared not yet applicable until Signal has the boundary each one governs
status: superseded
version: 1.0.1
created: 2026-10-05
updated: 2026-10-05
owners:
  - repository-governance
review_cycle: on-trigger
supersedes: []
superseded_by:
  - DF-SIGNAL-FND-2026-0002
related_documents:
  - .echelon/foundations.json
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - AGENTS.md
  - limen.config.json
  - .github/workflows/echelon-foundations.yml
tags: [governance, foundations, aegis, forma, folio, applicability]
---

# DF-SIGNAL-FND-2026-0001 — Aegis, Forma and Folio are not yet applicable

- **Date:** 2026-10-05
- **Status:** superseded by [`DF-SIGNAL-FND-2026-0002`](DF-SIGNAL-FND-2026-0002--build-signal-on-the-full-echelon-foundation-stack.md) (2026-10-05). The owner directed that every Echelon application be built the same way: "we want all apps built the same way … Apply it to signal and chrona too." Kept for history; it no longer governs.
- **Decision type:** applicability declaration (temporary, with restoration triggers)
- **Work item:** `FOUNDATIONS-APPLICABILITY`

## Context

`.echelon/foundations.json` declared Aegis, Forma and Folio `required: true`.
The Echelon foundations gate (Praxis `foundations verify`, pinned at
`a95dbf238e561eaac4b38ca7011efc1a496cf1c6`) therefore failed on `main` and on
every branch with:

```
[FAIL] aegis    installed=False pinned=False used=False evidence=False expected=1.0.0
[FAIL] forma    installed=False pinned=False used=False evidence=False expected=0.2.0
[FAIL] folio    installed=False pinned=False used=False evidence=False expected=0.3.0
ECHELON-FND-AEGIS-001 aegis is required but is not installed/declared.
ECHELON-FND-FORMA-001 forma is required but is not installed/declared.
ECHELON-FND-FOLIO-001 folio is required but is not installed/declared.
```

What the verifier counts (Praxis `src/Praxis.Cli/Foundations.fs` at that
commit):

| Capability | Installed | Used | Evidence |
|---|---|---|---|
| Aegis | a `PackageReference` to `EchelonFoundry.Aegis.Core` in a `.fsproj`/`.csproj`/`.props`/`.targets` | source contains `open Aegis`, `Aegis.capture`/`guard`, `Bootstrap.validate` or `Sinks.Collector` | the boundary manifest (`aegis-boundaries.json`) exists |
| Forma | `@echelon-foundry/design-system` in `package.json` | source references the package, `design-system/all.css` or an `<ef-*>` control | same as used |
| Folio | `@echelon-foundry/print-components` in `package.json` | source references the package, `<ef-print-*>`, `print.css` or `register` | same as used |

## Evidence: none of the three boundaries exists in Signal yet

- **No .NET/F# project.** The repository contains no `.fsproj`, `.csproj` or
  `.fs` file. `src/Echelon.Signal.Engine/` and `src/Echelon.Signal.Browser/`
  hold only `README.md`. Aegis is a .NET package
  (`EchelonFoundry.Aegis.Core`); `ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`
  §3.1 scopes it to "every .NET/F# application or host tier that owns an
  operational boundary", and `input-documents/signal-aegis-installation-and-usage-requirements.txt`
  AER-001 to "every deployable Signal .NET/F# host". There is no such tier, and no operational boundary
  (GitHub, storage, scoring execution, import/export, browser/WASM) is
  implemented.
- **No interactive browser surface.** There is no `package.json`, `.html`,
  `.css` or `.ts`/`.js` application source; the only scripts are the ROS
  launcher tooling in `tools/`. Forma is scoped (§4) to "every
  interactive browser application surface".
- **No document output.** No report, battery summary, printable export or
  deliverable is implemented. Folio is scoped (§5) to those surfaces.
- **The same condition was already accepted for Limen.** `limen.config.json`
  declares `boundary.notApplicable` because no engine or kernel source exists
  (WI-0021, restoration tracked by WI-0022).

Installing the packages now would mean adding an empty F# project and an
unused npm dependency only to satisfy the gate. The verifier's `used` check
exists precisely to reject that ("merely adding a package does not satisfy the
contract", Praxis `docs/application-foundations.md`), and this repository's
rules forbid fabricated evidence.

## Policy basis

- **Repository requirement.** `docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md`
  §1: "Every existing and future requirement in this repository inherits them
  **when the capability is applicable** … An implementation MAY mark a
  capability not applicable only when it is genuinely outside that feature's
  boundary. The reason MUST be explicit and reviewable." This record is that
  reason.
- **Verifier contract.** Praxis `docs/application-foundations.md`:
  applications own applicability; a capability declared `required: false` is
  reported as `N/A`, not PASS, preserving the difference between "not
  applicable" and "implemented correctly". The foundations schema
  (`echelon-foundations-v1.schema.json`) has no reason field
  (`additionalProperties: false`), so the reason lives here.
- **Portfolio inventory.** `echelon-organization-administration`
  `application-governance/QUALITY-REMEDIATION-INVENTORY.md`, finding SIG-F1:
  remediation is "Install with the first slice, or … an explicit
  deferred/not-applicable state with evidence". XC-16 records that the
  declarations were applied wholesale from the template to pre-implementation
  repositories and that "foundations must be justified by artifacts present".
- **QDI-060** asks that Signal fix its foundation drift before the first
  implementation slice. This decision does so for the part that can be done
  truthfully now: the declaration matches the repository, the gate is green
  rather than permanently red, and the restoration obligations below are
  bound to the first slice instead of being forgotten.

## Decision

In `.echelon/foundations.json`, set `aegis`, `forma` and `folio` to
`required: false`. Keep their `version`, `sourceCommit` and `boundaryManifest`
values unchanged so the baseline to restore is not lost.

This does **not** relax any requirement in
`ECHELON-SHARED-APPLICATION-FOUNDATIONS.md` or the Aegis section of
`AGENTS.md` (ARX-015, ADM-077). Those apply in full to the first code that
owns the boundary in question.

## Restoration triggers (each flips the capability back to `required: true` in the same change)

1. **Aegis:** the first .NET/F# project is added (for example
   `src/Echelon.Signal.Engine/*.fsproj`) or the first operational boundary is
   implemented. That change must reference `EchelonFoundry.Aegis.Core`
   1.0.0 (or the then-current baseline), use it at the boundary, and add
   `aegis-boundaries.json`.
2. **Forma:** the first interactive browser surface (respondent, author or
   administrator UI) is added. It must consume the pinned
   `@echelon-foundry/design-system`.
3. **Folio:** the first printable or document-style output (result report,
   battery summary, administrative report or export) is added. It must
   consume the pinned `@echelon-foundry/print-components`.

Each trigger is recorded as a backlog obligation: WI-0023 (Aegis), WI-0024
(Forma) and WI-0025 (Folio). Reviewers of
a change that crosses a trigger should reject it if `foundations.json` still
says `required: false` for that capability.

## Consequences

- `foundations verify` reports Aegis, Forma and Folio as `N/A` and the gate
  carries signal again: it fails if Limen, Ordo or Praxis drift.
- The gate cannot by itself detect that a trigger was crossed. That gap is
  portfolio-wide (XC-16: no declared-future state in the schema) and is
  mitigated here by the backlog obligations and the review rule above.

## Revisit trigger

Any restoration trigger above; a Praxis foundations schema change that adds an
explicit deferred/not-applicable state with reason (XC-16), at which point this
declaration should move into that state; or a change to Signal's charter scope.
