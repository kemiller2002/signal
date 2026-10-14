---
id: DF-SIGNAL-2026-0026
title: The console authors scoring by named built-in choices that compile to the engine's scorer algebra; advanced and custom scoring are offered separately
status: accepted
version: 1.0.0
created: 2026-10-14
updated: 2026-10-14
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [authoring, scoring, presets]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-14T00:28:05.548Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Scoring models authored by name in the console (WI-0081)"
---

# DF-SIGNAL-2026-0026: Scoring models authored by name in the console

- **Date:** 2026-10-14
- **Status:** accepted. These are routine design choices, taken under the coordinator's standing approval.
- **Work item:** WI-0081. **Requirements:** ALG-004 ("Recommended V1 Authoring Choices"), AUT-002, SCS-017 ("expose ordinary built-in scorers before custom expression authoring").

## Decision

1. **Named choices, ordinary scorers.** The draft editor's "How sections score" area (`ScoringAuthoring`) offers ALG-004's primary choices by name. Each compiles to the engine's existing scorer algebra (`Scoring`, as `Registry.Presets` does), never to custom code:

   | Choice | Compiles to |
   |---|---|
   | sum | `RawSum` |
   | average | `Mean` |
   | weighted sum | `WeightedSum`, one weight per scored question |
   | weighted average | `WeightedMean`, one weight per scored question |
   | percentage of the maximum | `PercentageOfMaximum` of the section's highest answer value |
   | average on 0–100 | `Mean` + `LinearNormalize` |
   | mapped values | a `Mapped` scale, one value per answer position, averaged |
   | reverse scoring | a section's `Reverse` scale |
   | threshold / banding | `Bands` interpretations |
   | pass / fail | `PassFail` interpretations, with a direction |
   | domain scoring | sections, as now |
   | profile scoring | sections reported with no overall score |

   The advanced choices median, lowest and highest are offered beside them, as are a weakest-link or minimum-domain overall and declared section weights.
2. **Each edit is shape-checked; publication judges the draft.**
   - Wrong weight or value counts, unknown sections, malformed bands and unknown models are refused with the reason.
   - Overlapping bands are accepted as shape and blocked by publication validation, as every draft problem is.
3. **One section is edited at a time,** chosen in the form. When none is chosen it is the first, which the page shows as selected. A list of per-section selects would need nested view lists (DF-SIGNAL-2026-0022).
4. **Reverse scoring is per section:** the scorer's scale applies to every question the section scores. A section that mixes directions is split, or uses mapped values.
5. **Not in this slice:**
   - **Custom expressions:** the escape hatch, with validation and preview, is the next WI-0081 slice. ALG-004 and AUT-002 stay partial until then.
   - Geometric and harmonic means, penalty, risk accumulation, gates, maturity stages, rubric and confidence adjustment are representable by imported templates but not offered as named console choices.
