# Pending Praxis intake: SIG-EXP-001 Signal identity, landing page and pre-runtime start-up

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted for Praxis ingestion: 2026-11-09
- Source: owner assignment "Echelon Forma: Design System Implementation", phase 6 (Signal), and the normative drafts in `kemiller2002/echelon-design-system`: EDS-BRAND-APP-001 (`brand/application-logos.md`), EDS-UX-LANDING-001 (`patterns/application-landing-pages.md`), EDS-UX-WAIT-001 (`patterns/waiting-and-progress.md`)
- Owner decisions (2026-11-09): echelon-current 1.37.2 with Forma 0.14.0 approved ("approved", 07:31 local); the Signal mark in `echelon-design-system` `brand/logos/signal` approved ("Approve", 07:45 local)

## Audit baseline (`main` before this work)

`web/index.html` (the assessment) is what the site root redirects to; there is no landing page. Every page uses `<link rel="icon" href="data:,">`. The pages show nothing until the WebAssembly engine has loaded. Signal has no logo assets.

## Requirements

**SIG-EXP-001.1 Platform.** Upgrade to echelon-current 1.37.2 through Conditor (Forma 0.13.1 -> 0.14.0), with no other component change.

**SIG-EXP-001.2 Application identity.** Use the approved Signal mark: favicon on every published page, and Forma `.ef-app-identity` with light and dark variants on the landing and start-up surfaces.

**SIG-EXP-001.3 Landing page.** A dedicated, static, script-free landing page at the site root explains Signal's purpose, audience and how it works with only real claims, leads to the administrator console, tells respondents to use their survey link, and shows no sample records or demo privileges. Responsive and light/dark.

**SIG-EXP-001.4 Pre-runtime start-up.** Every engine page (assessment, survey, administrator) paints Forma `.ef-startup` from its HTML before WebAssembly loads; the loader hides it when the kernel runs, marks a failed start when the engine cannot load, and a timed-out start after 60 seconds. No fabricated progress.

**SIG-EXP-001.5 Evidence.** Browser tests for delayed and failed engine start, favicon, landing page content, navigation, reflow at 320/390/768/1280 px, dark scheme, and the mark's approval record; existing suites unchanged and passing.
