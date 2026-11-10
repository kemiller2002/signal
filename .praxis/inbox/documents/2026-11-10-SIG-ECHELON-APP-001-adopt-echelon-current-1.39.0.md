# Pending Praxis intake: SIG-ECHELON-APP-001 adopt echelon-current 1.39.0 and the Echelon application layer

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-10
- Source: the owner-approved "foundry ledger" visual direction (Kevin, 2026-11-09 20:24 local) and its interface contract (Echelon application layer, `@echelon-foundry/echelon-theme` 1.1.0), relayed by the coordinator as the first of a staged series of Signal pull requests. echelon-current 1.39.0 is published and differs from 1.38.0 only by the theme moving from 1.0.0 to 1.1.0; Conditor 0.10.25 qualifies it.

## Requirements

**SIG-ECHELON-APP-001.1** Signal moves to echelon-current 1.39.0 through Conditor's sanctioned path (`conditor upgrade --current` against the digest-verified linux-x64 resolved set, then `conditor init` once to re-lock the changed theme descriptor). No lock, authority or pin is hand-edited. `conditor verify` passes afterwards.

**SIG-ECHELON-APP-001.2** Every page (assessment, survey/respondent, administrator, landing) loads, in this order: Forma `dist/all.css`, the theme's `dist/echelon.css`, `dist/echelon-fonts.css`, `dist/echelon-app.css`. `conditor upgrade --current` does not add these to an established application, so Signal adds them itself.

**SIG-ECHELON-APP-001.3** The strict policy still holds (`style-src 'self'; font-src 'self'`): the fonts are served from the site's own origin at the deployed paths, and the Pages assembly copies them. A browser check proves the fonts load under the policy with no policy violation.

**SIG-ECHELON-APP-001.4** The change is behaviour-neutral apart from the fonts and the brand root's canvas (and the theme's styling of the `echelon-chip` and `echelon-loading` markup that #133 already emits). No page is restructured.

**SIG-ECHELON-APP-001.5** The visual-review capture tooling (WI-0110: `tests/visual/`, `.github/workflows/visual-capture.yml`) comes onto main so visual evidence can be reproduced from main.

**SIG-ECHELON-APP-001.6** Tests move with the change without being weakened: the foundations conformance tests and the deployed-site check assert the new import order and the served theme files.

**SIG-ECHELON-APP-001.7** Visual-review evidence (desktop/tablet/mobile, light/dark) is recorded separately from the functional evidence and compared with the baselines (`visual-evidence/baseline-8ae1c07`, `baseline-7cf0730`).
