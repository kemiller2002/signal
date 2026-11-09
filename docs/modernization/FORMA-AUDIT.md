# Forma audit

Work item: WI-0100 · Signal pin: `@echelon-foundry/design-system` **0.6.1** · Current verified release: **0.13.0** (echelon-current 1.37.0)

## Verdict

**Forma is consumed correctly as a platform, but the pinned version is 7 minor releases behind the current one, and three local style rules belong in shared platforms.** The Forma platform contract is **not yet satisfied** for currency, and SIGNAL-VIS-001 visual governance is open.

## How Signal consumes Forma

- All three pages (`web/index.html`, `web/survey/`, `web/admin/`) import `design-system/dist/all.css` from the installed package and never copy it. There are 49 distinct `ef-*` classes in use.
- Application CSS is 33 lines in total: `web/styles.css` has 11 and `web/admin/admin.css` has 22. Nothing in it restyles Forma internals.
- Every control is a Forma component bound through Limen. Field-level binding inventories are held by tests (`AdminPageTests`, binding agreement tests). A control with no engine meaning fails `limen verify --strict`.
- Rendered-behavior evidence comes from the Playwright `ui-conformance` project (UI-LAYOUT-INTERACTION@1.0.0, issue #104). It runs at 320, 390, 768 and 1280 px, covers layout, actions, markup and admin draft/preview/content, and treats missing evidence as UNKNOWN, never PASS. All tests passed on 2026-11-08 (138/138 across projects).

## Findings

| ID | Finding | Rule | Action |
|---|---|---|---|
| FRM-01 | **Upgraded to 0.13.0 in WI-0102, then 0.13.1 in WI-0103.** It fixed the ULI-L05 best-worst overflow and regressed the record header (FRM-07). Before: pinned 0.6.1. Current is 0.13.0, with no removed classes, tokens or exports, but layout changes: long text now breaks inside 21 containers, and Signal uses `ef-cluster`, `ef-record-header` and `ef-operation-status`. | currency | C1: upgrade through Conditor, then re-run ui-conformance and re-baseline visuals with review. |
| FRM-02 | `admin.css` draws chart pattern fills (diagonal, dots, cross) with hard-coded `rgb(0 0 0 / x)` instead of Forma tokens. The capability, non-colour encoding of data marks (ADM-017), is generic visualization infrastructure. Forma 0.13.0 has 176 patterns but no chart or data-mark pattern. | Shared Component Rule, design tokens | **Forma work item**: a data-mark pattern with tokenized non-colour fills. Signal consumes it once it is released. Until then, the local rule is a documented, temporary exception. |
| FRM-03 | `admin.css` sets a global `prefers-reduced-motion` override (`* { animation: none !important }`). Forma's foundations already handle reduced motion (`foundations.css`), so this duplicates a platform responsibility with a blunter rule. | no app-specific replacement | Remove after confirming Forma 0.13.0 covers every animated component Signal uses (C1). |
| FRM-04 | No explicit theme, brand or palette decision exists (SIGNAL-VIS-001.1). Semantic colour across survey and admin is not governed. | visual intent | **Owner decision**, issue #125, depends on Forma #159 and #160. |
| FRM-05 | Draft preview copies the survey row markup into `web/admin/index.html` (DF-SIGNAL-2026-0025), and the two must be kept in step by hand. | duplication | Covered by binding-inventory tests today. Evaluate a shared template when Limen nested view lists land (limen#108). |
| FRM-07 | **Regression in 0.13.0:** below 24em the `.ef-record-header` type label overlaps the title on the assessment and admin pages (−65 px and −33 px). Found by visual review; the existing conformance suite checked only horizontal overflow. | Shared Component Rule | **Fixed in Forma 0.13.1** (forma#180), adopted via echelon-current 1.37.1 (WI-0103). The Signal test is now an ordinary assertion at 320, 390, 768 and 1280 px. |
| FRM-06 | ADM-017 accessibility: the validator's rejection codes (LOW_CONTRAST, COLOUR_ONLY_DISTINCTION, …) are never shown to fire in tests. Touch-target size and zoom/reflow have no evidence. Forced-colours support is one untested CSS rule. | accessibility evidence | Independent verifier reclassified ADM-017 as PARTIAL. Add negative tests (C3). |

## Component verification matrix (current evidence)

| Dimension | Evidence | Status |
|---|---|---|
| Behaviour and state transitions | F# view and update tests, plus Playwright composed path | evidenced |
| Keyboard accessibility | ui-conformance actions, radio selectors | partial: not every selector family (SCS-014 PARTIAL) |
| Semantic labelling | DF-SIGNAL-2026-0013, markup conformance | evidenced |
| Responsive layout | ui-conformance at 320, 390, 768 and 1280 px; mobile project (Pixel 7) | evidenced |
| Validation and error states | engine-driven; ui-conformance admin content | evidenced |
| Loading and empty states | demo admin shows its "No data store is configured" empty state | partial |
| Overflow and clipping | ui-conformance layout | evidenced on 0.6.1; must be re-run on 0.13.0 |
| Design tokens | FRM-02 hard-coded colours | **fails** (3 rules) |
| Playwright-compatible interaction | 138 browser tests | evidenced |
| Deterministic rendering | engine-produced views; no visual-regression baseline | partial |
| No control implies missing functionality | Limen strict verification: every binding has an engine message | evidenced |
