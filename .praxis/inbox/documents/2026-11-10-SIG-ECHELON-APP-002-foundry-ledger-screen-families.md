# Pending Praxis intake: SIG-ECHELON-APP-002 apply the "foundry ledger" direction across Signal's five screen families

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-10
- Source: the owner-approved "foundry ledger" visual direction (Kevin, 2026-11-09 20:24 local), its interface contract (Echelon application layer, `@echelon-foundry/echelon-theme` 1.1.0) and the approved mockups (landing, admin, authoring, respondent, results), relayed by the coordinator as the second stage after SIG-ECHELON-APP-001 (signal#134). Staged as one pull request per screen family, stacked in merge order: landing, admin shell and dashboard, authoring, respondent, results.

## Requirements

**SIG-ECHELON-APP-002.1** Landing: composed from the approved landing mockup (display heading, lead, eyebrows, role panels, numbered steps, one primary action). It keeps Signal's own mark and does not demonstrate administrator functionality: no sample records, no forms, no invented figures.

**SIG-ECHELON-APP-002.2** Administrator shell and dashboard: the dark Echelon application bar, route tabs (`aria-current="page"`), and panels with elevation; the overview's counts and unresolved work come from the engine, and the attention cue appears only when there is unresolved work.

**SIG-ECHELON-APP-002.3** Authoring: the draft editor's parts on panels, in the editor's order; one primary action (Publish); danger emphasis on the destructive confirmation.

**SIG-ECHELON-APP-002.4** Respondent: no administrator chrome; one primary action per view; progress only from Signal's own counts (page, answered of applicable), with no invented time estimate.

**SIG-ECHELON-APP-002.5** Results: each score as a figure from the engine's own values (no invented overall score), and a "How this was calculated" accent panel carrying the engine's methodology or lineage.

**SIG-ECHELON-APP-002.6** Rules across the series: the `echelon-` classes and `data-echelon-*` attributes from the theme; page-composition CSS stays in Signal's own CSS files; Limen binds only classes and `data-*` attributes (no inline styles); no `forma-*` class or token defined outside Forma; chips use `.echelon-chip[data-echelon-tone]` with the contract mapping (Hidden → closed, Reviewed → review), labels from `phaseLabel`, `data-phase` kept; at most one primary action per view, danger emphasis on destructive confirmations; loading regions keep `.echelon-loading` with a text label; every #133 pending-state behaviour and focus target is kept.

**SIG-ECHELON-APP-002.7** Verification per pull request: CI green with #133's tests kept and unweakened (conformance tests change only where markup legitimately changes, each named with its reason); visual-review evidence from the capture workflow (desktop/tablet/mobile, light/dark) compared with the baselines and mockups, recorded separately from functional evidence; the capture's per-screen audit (WCAG AA contrast, keyboard focus, 200% text at 320 px, forced colors, reduced motion); Visual Engineering verify with its context version and the principles applied.
