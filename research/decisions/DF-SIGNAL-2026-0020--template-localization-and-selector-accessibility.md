---
id: DF-SIGNAL-2026-0020
title: Templates carry translations keyed by stable ids and selectors carry accessibility metadata, both written only when present so every existing template keeps its hash
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [template, localization, accessibility, canonical-form]
provenance:
  contributions:
    EXE-20261013T170114560Z-a503dddb:
      operations: [created]
      at: 2026-10-13T17:01:20.216Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Template localization and selector accessibility (WI-0091)"
---

# DF-SIGNAL-2026-0020: Template localization and selector accessibility

- **Date:** 2026-10-13
- **Status:** accepted. These are routine design choices, taken under the coordinator's standing approval. They are additive, follow the extension rule `rules` and `results` already use, and change no published template or hash.
- **Work item:** WI-0091. **Requirements:** CAN-001 §2 (Localization in the canonical template) and §7 (selector AccessibilityMetadata); ACR-006 §17 (localization principles) and §18 (accessibility).

## Context

CAN-001 lists a Localization area in the canonical template and AccessibilityMetadata on selectors. ACR-006 §17 limits what a translation may affect: question text, instructions, section titles, answer labels, result labels and recommendation text. It must never affect identity, option identity or order, scoring, cardinality or semantics. Neither area existed, and `UsesLocalization` was refused.

## Decision

1. **Translations are keyed by what they translate.** The key is a stable id (`question/<id>/prompt`, `question/<id>/label/<index>`, `question/<id>/option/<optionId>`, `result/<interpretationId>/<label>`, and so on), never a position in a rendering.
   - A `Localization` is a BCP 47 locale plus a map from key to text.
   - Ids never contain `/`, so only a result label (the last part) may.
2. **The canonical form is extended, never changed.**
   - A selector's `accessibility` member (`name`, `description`) and the template's `localizations` member are written only when present.
   - Every template without them keeps its exact bytes and hash. The SDRA pilot's pinned hash and all eight published files are unchanged.
   - Locales are written in ordinal order and texts in key-spelling order, so the same translations always hash the same. Decoding returns locales in that order.
3. **A translation can only translate.** Publication refuses, under the new Localization category:
   - a key for something the template doesn't have, such as an unknown question, a label index past the selector's labels, an unknown option, help or accessibility text the question lacks, or a result label no interpretation produces (LOCALE-REFERENCE);
   - an empty translation (LOCALE-BLANK);
   - a locale that translates nothing (LOCALE-EMPTY);
   - a malformed tag (LOCALE-TAG);
   - a repeated locale (LOCALE-DUPLICATE);
   - blank accessibility text (ACCESSIBILITY-BLANK).
4. **Privacy covers translations.** Translated text and accessibility text go through the same PII-like wording check as the text they translate (PRIVACY-PII-LIKE-TRANSLATION). The pattern is English, so it catches only the obvious cases in other locales.
5. **No effect on meaning.**
   - Translations don't enter scoring, the encoding layout or the link.
   - A translation change is diffed as `LocalizationChanged`: no scoring or encoding impact, and comparable.
   - An accessibility change is a `SelectorChanged`.
   - A new translation still needs a new published version, because the artifact is immutable (§17).
6. **`UsesLocalization` is supported.** Declaring it is optional, like every capability.

## Consequences

- Showing the respondent's locale on the survey page, applying accessibility text in the UI, and authoring both in the console are the next WI-0091 slices.
- Report-definition localization (AUT-005 §66) stays with the report builder.
