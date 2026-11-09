# Pending Praxis intake: SIG-FORMA1-002 review follow-ups to SIG-FORMA1-001 (PR #131)

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-09
- Source: independent review of kemiller2002/signal#131 (merged as `f1081ac`, APPROVE), low findings, relayed by the coordinator.

## Requirements

**SIG-FORMA1-002.1** HANDOFF records the true `--check` result of the released Forma 1.0.1 codemod over `web web-kernel tests tools src`: 63 lines, being 54 Folio `ef-print-*` lines plus 9 deliberate Forma 0.x test fixtures. It gives the exact command, with `--exclude` for the fixture files, that reports only the Folio lines, and warns that write mode over `tests/` would rewrite the guard's fixtures and break the guard and its adversarial tests.

**SIG-FORMA1-002.2** The no-Forma-0.x guard in FoundationsConformanceTests also covers `tests/`, `tools/` and `src/`, excluding only its own fixtures, so a stale `.ef-*` selector in a test cannot pass vacuously.

**SIG-FORMA1-002.3** The guard's Folio exemption is an explicit allow-list of the `ef-print-*` names Folio 0.4.1 defines, not any `ef-print-*`.

**SIG-FORMA1-002.4** `docs/deployment.md` lists the Echelon theme among the published packages and records that the theme (UNLICENSED, the owner's own package) is served publicly from Pages.

**SIG-FORMA1-002.5** The anachronistic Forma 0.6.1 sentence in `tests/browser/ui-conformance-layout.spec.js` is corrected.
