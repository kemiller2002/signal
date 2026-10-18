---
id: RQ-SIGNAL-VIS-001
title: Govern Signal visual intent and semantic color
status: draft
version: 1.0.0
owners: [signal-maintainers]
created: 2026-10-18
updated: 2026-10-18
priority: critical
related_documents:
  - https://github.com/kemiller2002/signal/issues/125
  - docs/verification/ui-conformance-2026-10-13.md
tags: [visual-intent, color, accessibility, evidence]
derived_from: [INPUT-ef9a3a01aa7f9faa]
provenance:
  contributions:
    EXE-20261018T124842430Z-aa91caa1:
      operations: [created]
      at: 2026-10-18T12:53:00.522Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Canonical ingestion of claimed requirement source"
---

# Govern Signal visual intent and semantic color

This draft canonically records the requirements from Signal issue #125. It
does not approve a palette or prove implementation.

## Finding

Signal imports Forma 0.6.1 correctly, but its static HTML contains no
deliberate `data-ef-brand` or `data-ef-theme` assignment. Inheriting the
default is not inherently a defect, but no product-approved palette decision
is evident. The 2026-10-13 conformance report records strong behavioral and
layout tests alongside unstyled administration navigation and residual
presentation debt.

## Requirements

- **SIGNAL-VIS-001.1:** Make an explicit, reviewable decision whether Signal inherits the Echelon default palette or selects a supported Forma theme/brand. Assess alternatives such as blue/cyan and bronze without prejudging the result. Define light/dark and user-preference behavior, deployment defaults, and rationale.
- **SIGNAL-VIS-001.2:** Govern hierarchy and semantic color across assessment, survey, scoring/results, administrator console, authoring, lifecycle, export/report, and visualization. Danger/warning, answered/selected, pending, immutable/finalized, errors, and permissions MUST retain consistent meanings.
- **SIGNAL-VIS-001.3:** Replace visually unstyled or ambiguous administration navigation with coherent Forma-composed treatment without contradicting accessibility/layout contracts or secretly restyling Forma internals.
- **SIGNAL-VIS-001.4:** Preserve graph and chart meaning without hue alone, using labels, position, and patterns where applicable. Test populated, empty, ambiguous, failed, and lifecycle-transition states.
- **SIGNAL-VIS-001.5:** Produce representative screenshots, computed-color/contrast records, and independent hierarchy review in light/dark modes at narrow/wide viewports, 200% zoom, focus, forced colors, and grayscale/CVD screening. Exercise the real browser plus Limen/F# composed path.
- **SIGNAL-VIS-001.6:** Add regression fixtures and consume new Forma token-validity, Dokimos verification, and Praxis completion checks when available. Preserve rather than replace the current layout/interaction conformance suite.

## Acceptance criteria

- A durable product visual-decision record names the approved theme/brand policy, relevant requirements, source/version, and approved exceptions. Choosing the default is intentional and explicit.
- There are no unsupported Forma tokens or unreviewed colors; rendered contrast and semantic-state checks pass with evidence.
- Independent design review verifies hierarchy and suitability; contrast alone is not visual-quality acceptance.
- Respondent and administrator flows, including non-happy states, have explicit coverage, and UNKNOWN is never treated as PASS.

## Dependencies and authority

Dependencies are Forma issues
[#159](https://github.com/kemiller2002/forma/issues/159) and
[#160](https://github.com/kemiller2002/forma/issues/160), Ordo
[#92](https://github.com/kemiller2002/ordo/issues/92), and Visual Engineering
[#49](https://github.com/kemiller2002/visual-engineering/issues/49).
Implementation work remains governed by Signal
[#125](https://github.com/kemiller2002/signal/issues/125).
