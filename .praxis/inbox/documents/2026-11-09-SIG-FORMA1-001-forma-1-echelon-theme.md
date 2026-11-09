# Pending Praxis intake: SIG-FORMA1-001 Forma 1.x and the Echelon theme

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-09
- Source: owner directive EDS-ADR-002 (Echelon branding moves out of Forma into `kemiller2002/echelon-design-system`; Forma's `ef-` namespace becomes `forma-` as one major release). The owner chose a gradual consumer upgrade with Signal first; no other consumer changes with this work.

## Requirements

**SIG-FORMA1-001.1** Signal pins Forma `@echelon-foundry/design-system` 1.0.1 (GitHub release asset `echelon-foundry-design-system-1.0.1.tgz`, sha256 `db4a8bf7bcf6ba7826f1605c4265ea86c456464f2e2a3eef18d026f24c242056`) through echelon-current 1.38.0 (Conditor 0.10.24), applied with Conditor's isolated `--home` and `--authorize <plan digest>` flow. `conditor.json`, `.conditor/*`, `.echelon/foundations.json`, `package.json` and the lockfile agree, and `conditor verify` passes.

**SIG-FORMA1-001.2** Every Forma name Signal owns moves from `ef-` to `forma-` (classes, tags, custom properties, `data-*` attributes, the DOM dataset), using Forma's shipped codemod `tools/NamespaceMigrator` (dry run, write, then `--check`), plus hand fixes for what a text rewrite cannot see. Names owned by other packages (Folio's `ef-print-*` print surface, still on Folio 0.4.1) are not Forma's and keep their names. Historical records (decisions, evidence, processed inputs, audits) keep the names they were written with.

**SIG-FORMA1-001.3** Signal adds `@echelon-foundry/echelon-theme` 1.0.0 at its exact GitHub release asset (`echelon-foundry-echelon-theme-1.0.0.tgz`, sha256 `5d981420cfd4f7a835564a204938eb5db1f1d35350477a755df98daa37ec8394`), declared in `conditor.json`. Every page loads Forma's CSS first, then the theme's `dist/echelon.css`, and sets `data-forma-brand="echelon"` on `<html>`. The theme supplies Echelon colours and fonts only: Signal keeps its own approved mark and identity (EDS-BRAND-APP-001) and does not use the Echelon Foundry logos. The pages keep working under the strict policy (`style-src 'self'`, `object-src 'none'`).

**SIG-FORMA1-001.4** The landing page, the pre-runtime start-up screen, and the survey and administrator pages are checked in light and dark, at 320px and desktop width.

**SIG-FORMA1-001.5** Tests move with the rename without being weakened: FoundationsConformanceTests pins Forma 1.0.1, the browser tests and `tools/pages/verify-deployed.mjs` assert the `forma-` names, and the deployed-site check also asserts `data-forma-brand="echelon"` and that the theme CSS is served. A guard proves no Forma 0.x name remains in Signal's pages.

**SIG-FORMA1-001.6** CI is fully green, including the .NET/WASM and browser suites.
