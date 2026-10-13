# Signal UI layout and interaction conformance — 2026-10-13

Standard: `visual-engineering/UI-LAYOUT-INTERACTION@1.0.0` (rule IDs ULI-L01..L13,
ULI-A01..A10). Adopted under [signal#104](https://github.com/kemiller2002/signal/issues/104)
(rollout: visual-engineering#47), Praxis work item `GH-104`. Statuses are the
contract's: PASS, FAIL, UNKNOWN (no evidence), NOT_APPLICABLE, EXCEPTION.

Runner: `npm run ui:conformance` (publishes the WASM engine, runs the pure
helper tests with `node --test`, then the `ui-conformance` Playwright project,
`tests/browser/ui-conformance-actions.spec.js` and `ui-conformance-layout.spec.js`, sharing `ui-conformance-fixtures.mjs`). CI runs both halves in
`.github/workflows/build.yml`. Helpers: `tests/browser/ui-conformance-helpers.mjs`,
which mirrors the API of Forma's reference helpers
(`forma/tests/site-browser/ui-conformance-helpers.mjs`, branch
`feat/universal-ui-conformance-2026-10`) so it can be replaced by the published
version. Renderer: Chromium (Playwright 1.63.0). WebKit and Firefox: UNKNOWN for
this spec (the cross-browser workflow does not run it).

## Classification and surfaces

Product application UI (not a site, not a demo catalogue). Static pages served
from the repository root (`npm run serve`) and published to GitHub Pages under
`/signal/` (`tools/pages/assemble-site.mjs`). Every value is projected by the F#
engine through Limen bindings; Forma owns presentation, Folio owns print.

| Surface | Route | States reached in the browser |
| --- | --- | --- |
| Assessment | `web/index.html[#r=…]` | questions, refusal, results, nothing scored, submitted, print (existing spec) |
| Survey | `web/survey/#r=…` | each fixture kind, paged, test link, no link, template unreachable, submitted |
| Administration | `web/admin/#/…` | no store, public catalogue (assessment/section/question), not found, sign in, store unreachable |

## Baseline defects and repairs

| Rule | Surface | Evidence (before) | Repair | After |
| --- | --- | --- | --- | --- |
| ULI-L05, L04, L13 | Administration, every route and width | Page text began at x = 0 CSS px: `.admin` had no page container or gutter (screenshots at 320 and 1280) | `web-kernel/page.css`: `.admin` uses Forma's `--ef-layout-width-wide`, `--ef-layout-gutter`, `--ef-space-*` | PASS: heading inside a ≥16 px gutter at 320/390/768/1280 |
| ULI-L01, L09 | Survey best-worst rows | "Fits best" radio left edges 70.38 / 71.56 / 90.98 CSS px, delta 20.6 at 320, 390 and 1280 (columns staggered by option length) | Rows use Forma's `.ef-best-worst__row` composition | PASS: delta 0 at every width |
| ULI-A06 | Survey best-worst radios | Three radios per question each named exactly "Fits best" (and "Fits least"): the option was not in the name | A visually hidden copy of the option inside each label: names "Calm Fits best", … | PASS |
| ULI-L04 | Survey best-worst rows (introduced by the row above) | Column names read "Fits bestFits least" at 320 | `.survey-pair-choice` spacing from `--ef-space-2` | PASS (visual) |

## Action inventory (ULI-A01, A02, A09, A10)

Exactly these enabled, visible non-field actions appear on each surface; the
inventory test fails on any addition or removal.

| Surface/state | Action (role, name) | Owner | Outcome asserted |
| --- | --- | --- | --- |
| Assessment questions | radio per answer | engine | progress text and address change |
| | button "See results" | engine | refusal alert and no Results while unanswered; Results heading and three rows once answered |
| Assessment results | button "Edit answers" | engine | questions with answers retained |
| | button "Start over" | engine | progress 0 of 15, answers cleared |
| | button "Submit answers" (invitation only) | engine | Submitted heading; link equals address; editing gone |
| Assessment submitted | button "Copy link" | engine + browser clipboard | "copied" only when the clipboard accepted; refusal says so |
| Survey | answer fields, best-worst radios | engine | checked state, one best per question, progress |
| | button "Next" / "Back" | engine | refusal while required answers missing; page label moves; answers kept |
| | button "Submit answers" | engine | Submitted (existing survey spec) |
| | button "Copy link" (submitted) | engine | existing survey/assessment specs |
| Administration | button "Copy link" | engine + clipboard | notice "Link copied" or "did not allow copying"; Dismiss removes it |
| | 7 area links (Overview … Storage) | engine route codec | address = href, `aria-current="page"`, destination heading |
| | catalogue, section, question links | engine | question view |
| | link "Go to the overview" | engine | address `#/`, Overview current |
| | radios "On this page only" / "In this tab until I close it", button "Sign in" | engine (Fides) | disclosure changes; provider redirect (admin.spec.js) |
| | button "Dismiss", button "Sign out" (store unreachable) | engine | notice removed; sign-in screen |
| | dataset button, every signed-in view (groups, imports, invitations, lifecycle, reports, snapshots, exports, report builder, drafts, administrators, storage) | engine | see "Signed-in administration (GH-113)" below: exercised against a test-only fake GitHub store |

No inert, placeholder, `javascript:` or fake-success control was found on any
reached surface. Every failure path exercised (unanswered results, refused
clipboard, unreachable template, unreachable store, missing required answers)
reports honestly and shows no success state.

## Declared alignment groups (ULI-L01, L02, L09, L10)

Measured after `document.fonts.ready` and two stable frames; tolerance 1 CSS px.
Each measurement is attached to its test result as JSON.

| Group | Rule | Edge | 320 | 390 | 768 | 1280 |
| --- | --- | --- | --- | --- | --- | --- |
| assessment.q1.ordinal (five scale labels) | L01 | left below 44rem, top above | 0 | 0 | 0 | 0 |
| assessment.q1.ordinal.end | L01 | right / bottom | 0 | 0 | 0 | 0 |
| assessment.q1.other (special choices) | L01 | top (single row only from 768) | n/a: wraps | n/a: wraps | 0 | 0 |
| assessment.q1.start (scale and special choices) | L01 | left | 1 | 1 | 1 | 1 |
| assessment.results.actions | L10 | top | 0 | 0 | 0 | 0 |
| assessment.results.actions.height | L02 | bottom | 0 | 0 | 0 | 0 |
| survey.best-worst."Fits best" / "Fits least" columns | L09 | left | 0 | 0 | 0 | 0 |
| survey.best-worst.<row> (best vs least) | L01 | top | 0 | 0 | 0 | 0 |
| survey.ranking (Speed, Quality, Cost) | L02 | left and right | 0 | 0 | 0 | 0 |
| admin.areas | L01 | left | 0 | 0 | 0 | 0 |

## Other evidence

- ULI-L05/L09: no horizontal page scroll on 13 states at 320/390/768/1280; 200%
  root text at 390 on all three pages; long translated title, field labels and
  area names at 320.
- ULI-L06: area links keep their names with long text; nav reaches real views.
- ULI-A08: assessment scale by Space and arrow keys, See results by Enter,
  best-worst by Space, area links by Enter.
- ULI-L08: with reduced motion the ordinal marker's transition is ≤ 1 ms.
- ULI-L12 (print): Folio print surface asserted by `assessment.spec.js`
  (print media); PDF geometry and the administrator report print are UNKNOWN.

## Exceptions

| Rule | Surface | State | Reason | Owner | Evidence | Review trigger |
| --- | --- | --- | --- | --- | --- | --- |
| ULI-L05 | Survey best-worst row at 320 | option text containing a word wider than its cell | Forma's `.ef-best-worst__row` option cell (design-system 0.6.1) has `minmax(0, 1fr)` and no `overflow-wrap`; Signal may not restyle Forma components (ULI-L03; `FoundationsConformanceTests`) | Forma | `test.fail` in `ui-conformance-layout.spec.js` (page scrolls by ~78 px with a 35-character word) | next Forma upgrade: the test then fails and becomes an ordinary assertion |

## Remaining debt and unknown coverage

- UNKNOWN: WebKit/Firefox for these specs; administrator report print/PDF
  geometry; a deployment with several datasets (the fixtures configure one).
- Pre-existing: survey answer options are content-width boxes of differing
  widths within one question (left edges align; ULI-L02 recommended review);
  survey questions are not grouped in a `fieldset` (radios are described by
  their prompt through `aria-describedby`, not grouped by a legend);
  administration area navigation is an unstyled list in a one-child
  `.ef-navigation-shell`; `.app`'s padding predates the token rule (ULI-L13).
- Pre-existing environment note: on a slow machine the kernel can take longer
  than the 5 s default assertion timeout to start; `survey-max-link.spec.js`
  failed that way in the baseline runs here (before any change) and passed in
  the final full run. The new spec bounds its start-up wait at 30 s.

## Runs (rebased on main 64f025a)

- `dotnet test Echelon.Signal.sln -c Release`: 558 passed, 0 failed.
- `npm run check:limen`: passed (strict).
- `npm run ui:conformance:unit`: 6 passed, 0 failed.
- `npx playwright test` (chromium, mobile, pages, ui-conformance): 114 passed,
  0 failed; this includes the `ui-conformance` project's 37 tests (36 passed,
  1 expected failure: the Forma exception above).

## Signed-in administration (GH-113, follow-up on signal#113)

`tests/browser/fakes/github-store.js` is a test-only, in-memory fake of the
GitHub REST API that Arca's GitHub provider (0.5.0) uses, installed with
Playwright route interception. The page, engine, Fides client and Arca run
unchanged; blob SHAs are real Git blob SHA-1s. `failNext` injects one
failure. Specs: `ui-conformance-admin.spec.js`,
`ui-conformance-admin-content.spec.js`, `ui-conformance-admin-draft.spec.js`
and the markup guard `ui-conformance-markup.spec.js`.

| Area | Actions exercised (outcome asserted on the page and in the store or a download) |
| --- | --- |
| Dataset | sign-in sets the dataset up (administrator, manifest, audit records), Refresh, Sign out |
| Failure/recovery | a 503 on the ref update: read-only, "nothing is queued", no group stored; Refresh, then the same create succeeds once |
| Groups | create, list, status filter in the address, compare (one group refused with its reason, two give rows) |
| Lifecycle | Finalize refused while the index is stale; Rebuild the contribution index; Close incomplete, Reopen, Close incomplete, Finalize, Seal, each changing the stored lifecycle record |
| Invitations, imports | Create links, Save the links (download), a respondent answers an invitation and submits, Import (accepted and rejected), the same link again (duplicate) |
| Results, scoring, report | sort, percentages, All sections, scoring section link, report family and language (fr, right-to-left Arabic, no overflow) |
| Snapshots, exports | Take a snapshot (stored), Report (JSON), Sections (CSV), Lineage (each a download) |
| Report builder | start, Move up, Detail, Save definition (stored version), Open |
| Drafts | start, every editor action (section, questions, condition, fact, recommendation, validation rule, time limit, question bank, translation, accessibility text, numbering) added and removed, an impossible answer refused, Save draft, Submit for review, Approve, Publish refused until warnings are acknowledged, Publish, For the respondent site (download), Test links, Hide from new groups |
| Administrators | a login refused, Admit by account number (stored), Remove |
| Storage | verification shown, Rebuild the contribution index |

Defects found and fixed (all pre-existing, all in Signal):

| Rule | Defect | Fix |
| --- | --- | --- |
| ULI-A01 | Close incomplete, Reopen, Finalize and Seal were refused ("'' is not available for this group now") and the snapshot exports did nothing: Limen sends no value for a button, and these buttons carried their meaning in `value` | each button sends its own event (`closeGroup` … `exportLineage`), mapped in `AdminUpdate.fs`; F# test and markup guard |
| ULI-A01, L08 | The survey draft editor rendered as its heading alone, two of the three export buttons and two of three fields of each validation rule never rendered: a data-if template mounts only its first root element | one root element per template; markup guard |
| ULI-L08 | The group view and its lifecycle buttons, and the printable report, stayed on other areas after leaving them: a bare nested `<template>` as a data-if root stays mounted | a wrapper element; print rule follows it |
| ULI-A01 | The Administrators area, and the published-versions list, failed to render (Limen bridge error): row buttons bound the page-wide `busy` inside a row | the engine projects `busy` into those rows |
| ULI-A07 | While the only dataset opened after sign-in, its button stayed enabled; a second open raced the first and reported its own setup as "Something changed elsewhere" | the engine ignores an open while one is under way and projects `opening` to disable the button; F# test |
| — | CI: Ordo strict verification (SDE-STRUCT-001) refused the 542-line spec | split into actions, layout and fixtures files |

Recorded, not fixed here:

- Limen (upstream): a data-if template with several root elements, or a bare
  `<template>` root, is accepted silently instead of refused; a button's
  `value` is never sent. Signal now guards both in its own markup.
- Copy (engine wording, product owner): finalizing an incomplete group says
  "The group's completion changed elsewhere and needs a decision", though
  nothing changed elsewhere; report-builder options show raw names such as
  `AdministratorAudience`.
- ULI-A06: survey questions are not fieldsets with legends. The survey view is
  one flat list of rows, so a question's choices have no common element; this
  needs an engine view-model change and is captured as WI-0099.
