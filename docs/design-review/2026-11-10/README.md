# Signal visual refinement review — 2026-11-10

Work: SIG-VIS-REFINE-20261110. Status: **reviewable implementation; whole-application visual acceptance remains open**.

Open [the five-screen comparison gallery](review.html). Screenshots are visual evidence, not proof of functional correctness. Read [functional evidence](FUNCTIONAL.md) separately.

## Verified references and approval

The provisional `echelon-branding` name was not used as authority. The verified source is [`kemiller2002/echelon-design-system`](https://github.com/kemiller2002/echelon-design-system), inspected at `a03c5ac05d99c0bace43df35c90e74c3fe352d02`. Application: [`signal`](https://github.com/kemiller2002/signal), baseline `8d20d51bd548786146d15b89e173f60c5dd0fbbb`. Shared presentation: [`forma`](https://github.com/kemiller2002/forma); print: [`folio`](https://github.com/kemiller2002/folio).

Authority was checked in `brand/AUDIT.md`, `brand/logos/catalog.json`, `brand/application-logos.md`, `docs/CONSUMING-THE-ECHELON-THEME.md`, `theme/echelon-app.css` and the rendered-reference markup in `tests/visual/fixture.html`. The logo catalog records Kevin's approval of the corporate and Signal marks on November 9. The application theme records approval of the foundry ledger direction, self-hosted fonts, and ownership outside Forma. Older bootstrap statements that no approved logo exists are superseded by the dated catalog approval; provisional candidate colors remain unapproved.

Signal also contains the November 10 intake `SIG-ECHELON-APP-002`, recording Kevin's November 9 approval of all five screen-family mockups. This work follows that existing direction; it does not request a second approval or treat the intake itself as a new approval. Exact original mockup image files were not found in this checkout. The released theme and traceable application captures supply the review references.

Pinned consumer contracts: Forma **1.0.1**, Echelon theme **1.1.0**, Folio **0.4.1**, as declared by Signal's package and lock files. Forma is brand-neutral. Its semantic patterns and native controls remain authoritative; Echelon's scoped theme owns colors, fonts, elevations, chips and emphasis. No new Forma capability or Echelon default was added to Forma. `branding-verification.json` proves that the approved light/dark Signal marks and consumed theme match the verified branding source byte for byte; it also records that all six fetched live Forma/theme/mark assets match the pinned local consumer files.

## Live inspection and access limits

Inspected `https://signal.echelonfoundry.com/web/home/`, the assessment at `/web/`, the respondent page using a committed synthetic fixture link, and these administrator routes: Overview, Groups, Compare, Assessments, assessment version detail, Reports, Administrators and Storage. Captures cover desktop, tablet and mobile; exact sizes are visible in the image files and capture scripts. Initial home/admin captures use 1280/768/390 widths; later public captures use 1440/834/390.

The public deployment says GitHub sign-in is not configured. Overview, Groups, Compare, Administrators and Storage resolve to **No data store is configured**; Reports also shows that state. Assessments and its built-in version detail are public. No authentication was bypassed and no production records were created.

Authenticated populated dashboard, authoring, imported group results/scoring, report builder, storage mutations and administrator mutations cannot be inspected live on this deployment. Their comparison images are explicitly **existing test-fixture evidence**, not fresh authenticated live captures. `fixture-before/source.json` and `fixture-after/source.json` preserve revisions, workflow URLs, timestamps and browser versions. The latter comes from the existing admin/authoring refinement, not this change.

## Differences and bounded remediation

| Screen | Observed gap | Treatment / ownership |
| --- | --- | --- |
| Landing | Earlier baseline was flat; current deployed landing already has editorial headings, role panels, steps and a respondent callout | Retain the approved deployed implementation and Signal logo. No administrative demonstration or invented records |
| Admin | Deployed shell has a bordered record header and bulleted links despite importing the theme | Existing `feat/echelon-admin-shell` / authoring stack provides dark chrome, current-route tabs, meaningful overview panels and engine-owned counts. Shown for comparison; not duplicated or merged here |
| Authoring | Earlier editor lacks section separation and action hierarchy | Existing `feat/echelon-authoring` supplies panels in editor order, lifecycle chips, primary Publish and destructive confirmation emphasis. Shown for comparison; not duplicated here |
| Respondent | Text-only in-app identity, weak title hierarchy, options spaced apart like unrelated blocks | Approved light/dark Signal marks, theme display heading, raised question surface, options closer to prompts, primary Next/Submit/Copy. Preserve native controls, labels, DOM order, URL state, progress and review semantics |
| Assessment results | Scores visually compete with prose; methodology is an undifferentiated paragraph | Theme figures for each existing score, raised result surface and an accent “How this was calculated” panel. Keep the scorer's values, coverage, notes and methodology. No invented overall score or favorable/unfavorable interpretation |
| Group results, scoring and reports | Comparison tables collapse words on narrow screens; calculated values and lineage lack hierarchy | Theme result/scoring panels and figures, a lineage accent panel, and named keyboard-focusable table scroll regions. Report table sizing changes apply on screen only; Folio keeps each table as its direct child and retains print sizing |

Results remain a comparison table. At narrow widths and enlarged text it scrolls inside its named, keyboard-focusable region, preserving whole words. Local CSS expresses composition using existing tokens; it introduces no color literals, token defaults or reusable component replacements. Generic survey selector variants remain outside scope while their documented Limen dependency remains unresolved.

## Visual verification

Fresh captures in `after/` show assessment, respondent, results and submitted states at 1440, 834, 390 and 320 CSS pixels, light/dark, plus 320 pixels with 200% root text and forced colors. Synthetic answers only. The final captures use the locally built runtime, served on the loopback address recorded in `after/functional-checks.json`; earlier exploratory captures used the deployed runtime with intercepted local presentation. Neither method deploys changes. The deployed baseline includes the Pages demo banner; the local source presentation does not include that assembly-added banner. This is not a proposed removal. The build and repository tests are reported separately.

The existing Signal audit measured results text (23 nodes per scheme) and respondent text (49 nodes per scheme): **zero measured AA contrast failures**. All visited keyboard stops had a visible indicator and followed document order (3 results, 6 respondent tab stops; radio groups use their native arrow-key semantics). Both screens had zero page overflow at 320 pixels / 200% text and no infinite animation under reduced motion. Forced-color screenshots are supplied. See `after/accessibility.json`; passing these measurements does not certify the whole application.

First-glance review: identity precedes the survey title and version; the question or score is the main work surface; methodology is secondary but discoverable; respondents receive no admin chrome. Literal Draft/Published/Hidden and group-phase text remains authoritative in the existing admin stack; color does not determine lifecycle, permissions or scoring.

Visual Engineering **1.0.1**, source `b648cdef291d39c443ccb10215529ad69afe8b32`, verified successfully before UI changes. Applied principles: relational spacing, proportional emphasis, native semantics, explicit uncertainty, color-independent status and preserved source order.

`group-review/` adds fresh before-and-after results, scoring and report captures using the real runtime with test-only GitHub/Fides/store fakes. The frozen pre-change application markup is in `group-before/` (source `4f6e24e`); it is a reference fixture, never a production UI fork. The test imports a completed survey, then verifies identical chart values, calculated measures and report table values before and after. Its after audit reports zero measured contrast failures for 44 results, 92 scoring and 108 report text nodes in each scheme, zero page overflow at 320 pixels / 200% text, and visible visited focus indicators. It also generates `group-review/after-report.pdf` and verifies that only the report prints. These comparisons use this branch's main-based shell; the dark admin shell remains the separate #136 change.

## Open acceptance items

- Integrate and independently review the existing admin and authoring PRs through their own gates; this branch does not merge them.
- Run the full configured deployment and authentication workflows. Live access requires a configured identity exchange and data store, not merely a login.
- Do not claim every asynchronous operation has been reverified. This change preserves existing branded pre-runtime screens, survey-fetch status, per-operation admin feedback and unknown-write handling. Comprehensive loading/failure coverage remains the responsibility of the existing capture and functional suites.
- Independent visual review of the concrete comparisons remains open. Prior direction approval is recorded; it does not constitute a visual conformance verdict for this implementation.
- Screen-reader testing, real Safari/Firefox, physical touch devices, grayscale/CVD screening, text-spacing stress and full 200% browser zoom remain unverified in this session. Root text enlargement is not a substitute for all zoom testing.

A functionally passing build must not close these visual acceptance items.

## Reproduce and compare

After `npm ci` and `npm run build`, serve the repository with `npm run serve`. The capture scripts require Python Playwright; the optional pixel comparison requires Pillow. The Node audit reuses Signal's installed Playwright and existing audit implementation. The supplied environment used `/usr/bin/chromium`; adjust that executable path if necessary.

```sh
SIGNAL_REVIEW_ORIGIN=http://127.0.0.1:4321 python docs/design-review/2026-11-10/tools/capture-refinement.py
SIGNAL_REVIEW_ORIGIN=http://127.0.0.1:4321 node docs/design-review/2026-11-10/tools/audit_refinement.mjs
python docs/design-review/2026-11-10/tools/visual-regression.py OWNER_APPROVED_REFERENCE CANDIDATE_CAPTURES --output pixel-review.json
```

The group comparison runs with `SIGNAL_TEST_PORT=4361 npx playwright test -c docs/design-review/2026-11-10/tools/group-review.config.mjs`. By default its screenshots go into test results; set `SIGNAL_RESULTS_REVIEW_OUT` explicitly to refresh review artifacts.

The pixel tool rejects missing images, dimension changes and changed pixels (default tolerance zero; a nonzero fraction must be explicitly supplied). Compare the same browser, fonts, fixture state and viewport. The candidate screenshots are **not yet owner-accepted regression baselines**. The saved self-comparison is only a harness smoke check, not a passed application visual-regression gate. `manifest.json` inventories all evidence images with dimensions and SHA-256 values.
