# Echelon Signal handoff

## Objective

Build Signal toward the charter's first bounded outcome (template, live URL,
anonymous finalization, import, deterministic results) on the declared
foundations (Limen, Forma, Folio, Aegis, Ordo, Praxis).

## Platform (2026-10-08)

- echelon-current 1.22.0 (WI-0098, 2026-10-13; 1.21.0 in WI-0096, both Conditor 0.10.1; 1.20.0 in WI-0095 and 1.19.0 in WI-0094 through Conditor 0.10.0 `upgrade --current --stepwise`; 1.18.0 in WI-0082, 1.12.0 before) through Conditor, repository-only install
  path (`conditor init --manifest conditor.json`; Conditor's
  `upgrade --current` plan's workstation effects go to an isolated `--home`,
  never the shared workstation profile; WI-0082 applied echelon-current
  1.18.0 that way): Praxis 3.11.0, Ordo 1.7.0 (stamp-only for the
  repository; `ordo verify` from the isolated home's 1.7.0 CLI passes), Visual Engineering
  1.0.1, and as attested NuGet release assets in `vendor/nuget` (mapped in
  `NuGet.config`, pinned in `Directory.Packages.props`): Arca 0.5.0 (Core,
  GitHub, Limen), Fides 0.2.0 and limen-fsharp 0.9.0. `conditor verify`
  passes. `EchelonFoundry.Arca.Limen` is pinned, but no project references it:
  Arca's IndexedDB write queue and read cache were evaluated and declined
  (DF-SIGNAL-2026-0001 decision 5, WI-0074): per-respondent data stays off
  the device. Forma (design system) 0.6.1 and Folio (print components)
  0.4.0 are npm tarball pins declared in `conditor.json` (the Conditor
  0.9.0 web-binding opt-in), so Conditor writes their pins, lockfile entries
  and `.echelon/foundations.json`.
- Administrator domain (`src/Echelon.Signal.Admin`, pure F# over Arca.Core,
  WI-0038 onward): `Deployment` (closed, validated deployment configuration:
  environment, Fides identity, storage profiles, datasets and their bootstrap
  administrators by GitHub numeric id), `Storage` (Signal's namespace
  `<base>/signal`, dataset folders `datasets/<id>` in their profile's
  repository, set-up operations, opening against Arca's manifest),
  `DatasetManifest` (ADM-005 storage manifest), `ProviderContract` (ADM-003
  capability knowledge, ADM-073 write modes, profile verification, ADM-026
  failure meanings with no automatic retry), `Loading` (staged untrusted
  loading: verify manifests, load records, grant writes), `Growth` (ADM-058).
  The application's `StorageFaults` classifies GitHub failures through Aegis's
  GitHub integration (AER-002, AER-032). Tests run against Arca's in-memory
  provider and conformance suite (`StorageTests`, `ProviderTests`,
  `LoadingTests`).
- Administrators and sign-in (WI-0040): `Access` (capabilities per dataset,
  grant templates, person-only capabilities, the last-administrator rule),
  `AdministratorRecord` and `RosterStore` (the roster as Arca records; one
  command, one commit), `Credential` (ADM-056 credential states, memory-only
  retention, offline withholds every mutation with no queue, cross-tab
  notices only downgrade). Application: `Identity` (the real Fides client;
  the administrator is `github:<numeric id>`), `Store` (opening sets the
  namespace and dataset up only for a configured administrator, verifies,
  loads untrusted, pins the repository; roster commands are conditioned on
  revisions and the change token, decided again after a conflict, and
  reconciled after an unknown outcome), `Flow`. There is still no
  administrator page: the Limen host for Arca's GitHub adapter and Fides'
  ports is WI-0047.
- Durable import (WI-0041): `GroupRecord` (group configuration, exact
  template by hash, submission retention), `ResultRecord` (immutable
  contributions at `records/signal.result/<group>/<shard>/<identity>.json`,
  provenance without person identity), `Intake` (quarantine, order-independent
  batches with a stable id, promotion, resumable batch records),
  `Incremental` in the engine (incremental aggregation equal to full
  recomputation), `ReportState` (AdminReportState, embedded-or-stored by
  size, integrity, resume), `ContributionIndex` (Arca derived index).
  Application `GroupStore`: create, open, import in chunks (concurrent
  distinct imports commute; same identity decided again; unknown outcomes
  reconciled; offline stops and resumes, nothing queued), report state
  persistence, index validate/rebuild.
- Administrator page (WI-0047): `web/admin/index.html` on Limen and Forma,
  engine `AdminApp` (state, messages, effects) and `AdminView` (projection)
  in `Echelon.Signal.Admin`, with `GroupLifecycle` (close, reopen, finalize
  with obligations, seal, supersede), `AdminState` (states, capabilities,
  obligations) and `Conflicts` (versioned configuration changes, conflict
  workspace). The application wires it through Limen: `AdminProtocol`
  (Http, Storage, Navigation, `limen.schedule`, `signal.host`), `Bridge`
  (async Fides and Arca work inside the request/reply loop), `AdminPorts`
  (Fides ports and Arca's GitHub host; a 401 is reported to Fides),
  `AdminWork`, `AdminWire`, `GroupAdmin`; `Runtime.dispatchAdmin` and the
  `DispatchAdmin` export. `web-kernel/host.js` is the `signal.host` pack.
  The deployment's `web/admin/signal.deployment.json` is local (no store);
  a real deployment replaces it. Tested end to end in F# (`AdminPageTests`,
  a fake browser, the real Fides client, Arca's in-memory provider) and in
  Chromium (`tests/browser/admin.spec.js`: start-up and GitHub sign-in).
- Analytics (WI-0048): `Analysis` (measures with prerequisites, typed
  unavailability, distributions, AnalysisExpression v1 with limits),
  `Comparison` (semantic comparability with versioned continuity
  declarations, change, effect size, lineage traces), `Dependencies`
  (derived-state graph, closure invalidation, impact preview, semantic diffs,
  template upgrades), `AnalysisState` (URL-safe analysis state, saved
  analyses, inverse roster commands). The page shows each open group's
  calculated measures and lineage.
- Visualization (WI-0049): `Visualization` (typed specification compiled
  and validated in F#: legal shapes, suitability warnings, explicit missing
  values, withheld totals, accessible description and table, palette with
  patterns and symbols, contrast), `Dashboard` (versioned definitions,
  legal edits, device projections, limits on read), `Locale` (presentation
  only: numbers, percentages, dates, plurals, right-to-left), `Regression`
  (semantic assertions and what a change broke). The page explores the open
  group (section chart, drill to a distribution, sort, counts or
  percentages) with the exploration in the address. A checkbox event that
  arrives unchecked carries an empty value (`AdminProtocol`).

## Identity, landing page and start-up (WI-0106, 2026-11-09)

- Platform: echelon-current 1.37.2 through Conditor 0.10.23 (`conditor upgrade --current`, isolated `--home`); only Forma 0.13.1 -> 0.14.0 changed (owner approved the channel, 2026-11-09).
- Identity: the owner-approved Signal mark (source of truth `kemiller2002/echelon-design-system` `brand/logos/signal`, approval in its `catalog.json`) is the favicon of every page (`web/brand/signal-mark.svg`) and is inlined with light and dark variants (Forma `.ef-app-identity`). `web/brand/identity.json` records the approval.
- Landing: `web/home/` is a static, script-free page (EDS-UX-LANDING-001); the site root now redirects there instead of to the assessment. Respondent links to `web/` are unchanged.
- Start-up: each engine page carries Forma `.ef-startup` in its HTML; `web-kernel/limen-wasm.js` hides it once the kernel runs, marks `failed` when start-up throws and `timed-out` after 60 s. It has no controls, so Limen strict verification is unaffected.
- Tests: `tests/browser/experience.spec.js`; the Forma pin in `FoundationsConformanceTests` moved to 0.14.0.

## Forma 1.x and the Echelon theme (WI-0108, SIG-FORMA1-001, 2026-11-09)

- Platform: echelon-current 1.38.0 through Conditor 0.10.24 (`conditor upgrade --current`, linux-x64 set `sha256:b92b3be8…`, isolated `--home`, plan `sha256:5418b83d…`, which discloses the Forma 1.0 break); only Forma 0.14.0 -> 1.0.1 changed, and `echelon-theme` 1.0.0 was opted in. Second-plan drift: none. `conditor verify` passes.
- Theme: `@echelon-foundry/echelon-theme` 1.0.0 is pinned at its GitHub release asset (package.json URL, lockfile sha512 matching the verified tarball) and declared in `conditor.json`. Every page stylesheet imports Forma's `all.css`, then the theme's `dist/echelon.css`; every page sets `data-forma-brand="echelon"` on `<html>`. The theme supplies colours and fonts only (it ships no font files, so Manrope/Newsreader render only where installed, as before); Signal keeps its own approved mark.
- Namespace: Forma's codemod (`tools/NamespaceMigrator`, 1.0.1) rewrote 446 lines in 14 files under `web`, `web-kernel`, `tests`, `tools`. Folio 0.4.1 still owns its `ef-print-*` elements (its print CSS selects those element names), so those names were restored by hand. Historical records (`research/`, `docs/modernization/`, `docs/verification/`, `input-documents/`, processed inputs, older HANDOFF sections) and the two canonical requirement documents in `docs/requirements/` keep the names they were written with.
- Codemod `--check` (corrected in WI-0109): `--check` never exits 0 in Signal while Folio keeps its prefix. As merged in #131, `--check web web-kernel tests tools src` exited 3 with 63 lines: 54 Folio lines plus 9 deliberate Forma 0.x test fixtures. WI-0109 moved every deliberate fixture into `tests/fixtures/forma-zero-names.json`, so a successor runs, with the released 1.0.1 codemod:

  ```bash
  M=node_modules/@echelon-foundry/design-system/tools/NamespaceMigrator/NamespaceMigrator.fsproj
  dotnet run --project "$M" -- --check --exclude tests/fixtures/forma-zero-names.json web web-kernel tests tools src
  ```

  It exits 3 and lists 61 lines in 4 files, every one a Folio `ef-print-*` name: `web/index.html` 17, `web/admin/index.html` 30, `tests/browser/assessment.spec.js` 4, and `tests/Echelon.Signal.Tests/FoundationsConformanceTests.fs` 10 (the Folio composition markers and the explicit Folio allow-list). Any other line is a regression. **Never run the codemod in write mode over `tests/` (or over any path holding Folio markup):** it would rename Folio's elements, breaking print and the Folio allow-list, and rewrite the fixtures, so the no-Forma-0.x guard and the verify-deployed adversarial tests would no longer test anything.
- Guards: `FoundationsConformanceTests` pins Forma 1.0.1 and the theme, checks import order, the brand attribute, Signal's own mark, and that no Forma 0.x name remains in `web/`, `web-kernel/`, `tests/`, `tools/` or `src/` (WI-0109: only the fixture file is skipped, and the only exemption is the explicit list of the 27 elements Folio 0.4.1 registers, itself checked against the installed Folio). `tools/pages/verify-deployed.mjs` also checks the brand attribute, that each page's stylesheet imports the theme after Forma, that the served theme is the Forma 1.x Echelon brand, `.forma-startup`, and no Echelon Foundry logo in place of Signal's.
- Next: when Folio adopts a `forma-`-era namespace, migrate its `ef-print-*` names; other consumers (Forma Studio, Specula, …) migrate when next worked on.

## Per-action pending state, loading regions and state chips (WI-0111, SIG-VIS-PENDING-001, 2026-11-09)

The behaviour and data under the owner-approved "foundry ledger" refinement. It adds `echelon-` classes and `data-echelon-tone` but depends on none of them rendering: the Echelon theme 1.1.0 that styles them is not released, so chips and loading containers look plain until it lands.

- Pending state: `Admin/Operations.fs` replaces the page-wide `Busy` flag with the operation in flight (`Running`), the latest change's outcome (`LastWrite`: confirmed, partial, not done, unknown) and an owed reload. `AdminUpdate.update` settles the running operation on its first answer (`AdminApp.answer`); a download's or a copy's result answers nothing.
- The guard (decision): one store operation in flight, writes and reads alike, exactly the safety the busy flag gave. Store answers carry no correlation, so two in flight could not be told apart; and every write starts from the dataset/group handles and catalog revisions the wire last read, which a concurrent read replaces and a concurrent write races. A second activation (the same button or another) is ignored, never queued; this is now enforced in the update, not only by `disabled`. Local actions (editing, preview, navigation, test links) are not operations. The guard was tightened, not loosened: the group lifecycle, rebuild-index, export and first retire buttons were never disabled before.
- Feedback: `ActivityView` projects, per control, `{prefix}Busy` (bound to `aria-busy` and `aria-disabled`; the running button keeps focus), `{prefix}Disabled` (the guard) and `{prefix}Label` (the present participle while it runs). `#operation-pending` (Forma spinner, `role="status"`, `.echelon-loading`) names what runs; `#persistence` says saved only after the store confirmed, and partial, failed or unknown otherwise.
- Answer order: snapshot, report definition and dataset lifecycle now confirm before their reread; a created group that cannot be read back is confirmed, then the failure follows; a roster change whose outcome stays unknown is reported with `SIGNAL.STORAGE.OUTCOME_UNKNOWN`, no longer as a refusal.
- Another tab's change during an operation no longer starts a racing reload; the page shows `#reload-owed` and the person refreshes (tracked against reads started afterwards since WI-0112).
- Chips: group phase -> tone by `StateTones.ofPhase` (contract mapping); template lifecycle (draft, in review, reviewed, published, hidden) from `TemplateListing.ReviewState`. Hidden takes `closed` and Reviewed takes `review`; the theme 1.1.0 contract records both. Group chips show plain labels since WI-0112.
- Tests: `OperationsTests`, `StateToneTests`, `tests/browser/pending-states.spec.js` (the fake store's new `holdNext` holds a request). Visual-review evidence is captured from `visual/pending-states` (this branch plus the WI-0110 capture tooling, which is not on main).

### Review follow-ups (WI-0112, SIG-VIS-PENDING-002, 2026-11-10)

- Confirmations: Delete draft and Retire accepted results stay open, with the confirm button busy and the way out disabled (cancel ignored by the engine), until the change is answered; then they close and focus moves to `#activity` through Limen's focus pack (`limen.focus`, now negotiated by `web/admin/admin.js` and `AdminProtocol.focus`). The same focus move follows a hidden version and group or dataset lifecycle changes (`AdminUpdate.focusAfter`). Without the pack, nothing moves and the status still says what happened.
- One live region: `#activity` (always in the page, `role="status"`, `aria-atomic`) sits right above the notice, near the status and Refresh; it holds the running operation (spinner), the outcome line, the owed-reload line and a visually hidden copy of the notice's sentence. `#notice` is no longer a live region; the configuration and survey loading regions keep `role="status"`.
- Faults: `Bridge.Drain` returns calls, messages and failures together; `AdminWire.settle` applies the other messages, then `AdminApp.OperationFaulted` settles the running operation as unknown; `handle` presents an operational fault through Aegis and keeps the rest of the message. A programming defect is still re-raised by Aegis (fails loudly) and the kernel reports it.
- Reconcile before resend: after an unknown outcome, `Operations.canStart` refuses every change until a dataset read started afterwards succeeds (`Reconcile`: Owed -> Checking -> Clear). `Reload` tracks another tab's change the same way. A successful dataset read clears the last outcome line.
- Saves: `DraftSaved` carries the draft that was sent; `Authoring.saved` and `ReportBuilder.saved` keep edits made during the save and keep them unsaved.
- Chips: plain phase labels (`StateTones.phaseLabel`), the id in `data-phase`; " · " separates a chip from the text beside it.
- Test knobs on the in-memory `Page` (AdminPageTests): `GoOffline`, `AfterCommits`, `FaultNextWrite`, `CommitsStayUnknown`.

### Second review round (WI-0117, SIG-VIS-PENDING-003, 2026-11-10)

- Faults belong to their operation: `Bridge.StartOperation` runs the engine operation's own work and `Bridge.Start` background work (identity, sign-in, sign-out, the cross-tab receive, local answers); `Drain` tags each failure (`Bridge.Work`). `AdminWire.settle` presents every failure through Aegis but raises `OperationFaulted` only for an operation's, so a failed receive never settles a running save or opens its guard.
- Defects (WI-0114): `AdminWire.handle` returns `Handled { State; Reply; Defect }`. A programming defect inside an operation is still re-raised, by the caller, after it kept the settled state (`Runtime.dispatchAdmin`), so the page is never left running an operation that ended; the kernel sees the corrected view on its next message.
- Focus beside the change (`AdminUpdate.focusAfter`, by operation and outcome): a retire that worked -> `#sources-retired`, otherwise -> the "Retire accepted results…" button (`#retire-sources`; also when there was nothing to retire); group transitions -> `#group-phase`; dataset lifecycle -> `#dataset-lifecycle-heading`; delete draft and hide version -> `#activity`, which otherwise only announces.
- `web/admin/admin.css`: `[data-focus-target]:focus` draws a solid outline of `--forma-color-focus-ring` at `--forma-focus-ring-width`/`-offset` (also for programmatic focus after a pointer click, where `:focus-visible` may not match). Echelon theme: #171a18 on #f2efe7 (light) and #f2efe7 on #171a18 (dark), about 15:1.
- `ReportBuilder.saved` also compares the template pin.
- Test knobs added: `Browser.HoldWakes`/`HeldWakes`, `Page.HoldNextCommit(after)`, `Page.ReceiveThrows()`.

## Echelon application layer adopted (WI-0118, SIG-ECHELON-APP-001, 2026-11-10)

- echelon-current 1.39.0 through Conditor 0.10.25 (`upgrade --current`, isolated `--home`, plan `sha256:36cd12ca…`): Echelon theme 1.0.0 -> 1.1.0 only. The upgrade re-locked the theme descriptor; `conditor init` (run once) left the lock byte-identical. `conditor verify` passes.
- Every page stylesheet (`web/styles.css`, `web/admin/admin.css`, `web/home/home.css`) imports Forma `all.css`, `echelon.css`, `echelon-fonts.css`, `echelon-app.css`, in that order. Fonts are relative to the fonts stylesheet and served by the site (`font-src 'self'`); `assemble-site.mjs` copies the theme's whole `dist/`. No markup changed.
- Capture tooling on main: `tests/visual/capture-session.mjs` (shared session), `capture-public.spec.js`, `capture-admin.spec.js` (split under SDE-STRUCT-001's 500-line limit), and `audit.mjs`, which audits each captured screen (contrast light/dark, Tab focus, 200% text at 320 px, reduced-motion shimmer, forced-colors shot) into the manifest and README. Run it by pushing to `visual/**` or by dispatch.
- Evidence: CI on `ddc929e` green (Build: 692/692 .NET, 185/185 Playwright); `visual-evidence/baseline-ddc929e` (85 screens, 540 shots, none unreached; 70 audited, no finding). Start-up screens (fresh contexts) are not audited; the Tab walk starts where focus last was.
- Next: the staged PR 2 series (landing, admin shell, authoring, respondent, results), stacked on this branch.

### Landing page (WI-0119, SIG-ECHELON-APP-002 (a), 2026-11-10)

- `web/home/` ported from the approved landing mockup: eyebrow, `.echelon-display` h1 (same text), `.echelon-lead`, one primary action (the console), the respondent guidance in an accent panel, the three roles on panels, numbered steps, footer. Signal's own mark. The mockup's sample link and invented "4 of 6 sections answered" are left out.
- Composition only in `web/home/home.css`. New test in `experience.spec.js` (one primary action; no form, field, figure or chip).
- The capture audit's Tab walk now starts at the top of the page.
- Evidence: CI on `cac77d0` green (692 .NET, 186 Playwright); `visual-evidence/baseline-cac77d0` (85 screens, 540 shots; 70 audited, no finding; 944 focus stops all visible).
- Continuity gap: WI-0119 completed without a durable checkpoint. `work checkpoint` was refused because the executor's worktree had an untracked `node_modules` symlink (the `.gitignore` pattern `node_modules/` matches directories only), and the refusal was missed before `work complete`. All work was committed and pushed before completion (`3c2cb37`).

### Administrator shell and dashboard (WI-0120, SIG-ECHELON-APP-002 (b), 2026-11-10)

- `web/admin/index.html`: the Echelon application bar (mark, h1 "Administration" as its context, `#principal`, Copy link) replaces the record header; areas and group tabs are `.echelon-nav-tabs` (one row from 48rem, a list below); the dataset status is the view's title; the overview's unresolved work is a panel with attention only when `hasObligations` (new, `AdminView`), an explicit line when `noObligations`, and the two counts as figures; panels throughout the shell views; Create group and Sign in primary; the retire confirmation's button danger. `#activity`, the notice and #133's focus targets unchanged.
- Review follow-ups of #134/#135: admin `code` in IBM Plex Mono; Manrope and Newsreader preloaded on every page; `.gitignore` `node_modules` (symlinks too); capture rests the pointer before each shot and audits the start-up screens; audit counts opacity, records ratio bands and the lowest ten, and checks contrast under forced colors.
- WI-0119 record notes (no history rewritten): its completion event recorded `"paths":["node_modules"]` (the untracked symlink), and commit `3c2cb37` is titled "checkpoint and complete WI-0119" although no durable checkpoint exists (see the WI-0119 entry above).
- CI caught two layout regressions on `2b0444f` (tabs wrapping at 768 px; nowrap tab names overflowing at 320 px), fixed in `e9eed4e`.
- Admin tables keep whole words and scroll in their region on a phone (`d2ab83d`).
- Evidence: CI on `d2ab83d` green (693 .NET, 191 Playwright); `visual-evidence/baseline-d2ab83d` (85 screens, 540 shots, all 85 audited, no finding; 959 focus stops, all visible).
- The improved audit on `e9eed4e` found admin panels overflowing with 200% text at 320 px (fixed in `afee558`, now also asserted by the shell test), an unavailable report link at 2.42:1 from an old `opacity: 0.6` (secondary colour now), and light shots taken mid colour-transition after a dark one (the capture now waits for finite animations, `98532cc`).

### signal#136 second re-review (WI-0129, 2026-11-10)

- `AdminState.describeObligation` (ReconcileUnknownWrite): "N import(s) did not finish or could not be confirmed: import the same links again." True on the overview and in finalize's BLOCKED_BY_OBLIGATIONS refusal (stored part-way or blocked batches); a Refresh reconciles nothing.
- The empty state: such an import "is not always listed here; when it is, only until the page is refreshed or reopened" (only NeedsReconciliation is counted; part-way, offline and blocked imports show on the persistence line).
- The release record written after an import has its own unknown-outcome sentence (`AdminWork.releaseStopped`), never "whether the import was saved"; the import's own notice is emitted last so it is the one shown.

### signal#137 review round 2 (WI-0133, 2026-11-10)

- The long views' outcome copies show the persistence, reload and notice lines only for the view's own latest change (`ActivityView`: `draftOutcome` for save, publish, review, approve and delete; `assessmentsOutcome` for hide; `reportOutcome` for a report definition). A retire's line no longer sits in the draft's Outcome group; the shell's persistence line still says it.
- The warnings' acknowledgement names them from the findings ("1 warning: CODE", `draftWarningSummary`) and has "See them under Validation" (`draftJumpValidation`); Validation is in the draft parts.
- `outcome-beside-actions.spec.js` runs at 1280 and 390 px; the draft parts wrap on a phone.
- #136 follow-ups folded in: the release record's sentence adds "If newer results are still withheld, import the same links again."; the obligation adds that blocked items resume only once their template or encoding can be read.

### signal#137 visual-gate review (WI-0130, 2026-11-10)

- Template header: title, lifecycle chip and the one action group (Delete draft… danger, Submit for review and Approve as reviewed tertiary, Save draft, Publish primary), the warnings' acknowledgement, the delete confirmation and the outcome copy directly under it; no action is left in a panel.
- Draft parts: Questions, Conditions, Scoring, Presentation, Translations, Test fixtures, Review (`Authoring.draftParts`). Each button sends `draftJump`, which moves focus to the panel's heading (`draft-part-<key>`, Limen focus scrolls it into view); the draft, the route and the address are unchanged.
- Fields fill their column up to 40rem: Forma's stack aligned the panel body's grid track to the start, sizing it to the widest field.
- Question rows: the question, then section and kind tags, then Remove (tertiary) named "Remove question <id>: <prompt>".
- Panels two to a row from 64rem (reading order), the wide ones (Presentation, Scoring, Test fixtures, Validation) across both with their fields in columns.
- Follow-ups: WI-0131 (three-column layout, live preview, per-question edit view; the phone height), WI-0132 (the persistence line follows the person across views).

### signal#137 authoring (WI-0121, 2026-11-10)

- Draft editor on Echelon panels (Details … Validation, Review), one primary (Publish), danger on Delete draft's confirmation; assessments and report definitions on panels; the validation findings in a labelled scroll region (`.admin-table-scroll`) with whole-word cells.
- The findings table no longer sets words as columns of letters: the capture audit's collapse check is clean on every draft screen at 1440, 834, 390 and 320 px with 200% text (`visual-evidence/baseline-76e96ac`), and `ui-conformance-admin-draft.spec.js` asserts it at 834, 390 and 320 px with 200% text.
- #136's review rounds are merged forward (outcome copy at the end of the Validation panel, before Review).

### signal#136 re-review wording (WI-0128, 2026-11-10)

- Finalizing does **not** wait for an unknown-outcome import: the chunk is never stored, so an import of 25 links or fewer leaves the whole batch or nothing, and `unfinishedBatches` finds nothing. The resolution now says "Resume before finalizing the group: finalizing does not wait for it." (`AdminView.resumeImport`, overview and both places on Imported surveys).
- The empty state says an import that did not finish or whose outcome is unknown is listed only while the page is open, until it is refreshed or reopened (`AdminView.noUnresolvedWork`). WI-0126 stays the fix.
- An import's unknown-outcome notice says how to resume (`AdminWork.importStopped`), not the provider's general "it is checked before anything is sent again".
- `.admin-outcome` is `role="group"` named "Outcome"; still not live.

### signal#136 review round 2 (WI-0127, 2026-11-10)

- **Outcome beside the actions on long views.** It restores #133's SIG-VIS-PENDING-002.2.
  - The shell keeps #activity and #notice at the top of `<main>`.
  - The draft editor (between Save/Delete and Review/Publish, and in the draft's place once it is deleted), Assessments (after the versions' Hide buttons) and the report builder now show `.admin-outcome`: a non-live copy of the persistence line, the reload line, the editor's refusal and the notice.
  - #activity stays the one announcer.
  - Deleted-draft and hidden-version focus moves to that copy (`data-focus-target="outcome"`), not to #activity.
  - Tests: `tests/browser/outcome-beside-actions.spec.js`; `pending-states.spec.js` (delete draft, and hide focus within 400 px of the Hide button); `OperationsFollowUpTests` (Focus "outcome").
- **Unresolved work is stated truthfully.**
  - On open and refresh, groups are summarised with `GroupAdmin.summary 0 None`. An import whose outcome is unknown is therefore listed only when it happens on this page.
  - The empty state now says so, and that finalizing still checks every stored import (`AdminView.noUnresolvedWork`).
  - The resolution now says to import exactly the same links again (`AdminView.resumeImport`, also shown on the Imported surveys tab). Batches are identified by their exact link set.
  - Follow-up WI-0126 will read stored batches on open and refresh.
- **Page-flow test.** `ShellProjectionTests` renders an obligation through an import whose commit stays unknown. The AdminLinkTests comment now cites it.
- **New captures:**
  - `admin-overview-unresolved`;
  - `admin-assessments-version-hidden`;
  - viewport shots of `admin-draft-saved`, `admin-publish-refused` and `admin-draft-deleted`, scrolled to their action (the capture `anchor` field).

### signal#136 review round (WI-0125, 2026-11-10)

- REQUEST_CHANGES at `977ffb9` (visual gate). Fixed in `4227355` and `8d48eae`: the notice's "Technical detail" no longer collapses at 834 px; the Retention panel says why nothing can be retired (`RetentionScreen.retentionNote`); unresolved work is one item per obligation per group with its group, a path (the group's imported surveys, or Storage) and what resolves it (`AdminView.obligationsOf`, `obligationPath`; `ShellProjectionTests`); order is dataset title, activity, notice, then tabs; danger on Delete draft's confirmation and the Retire / Delete from active state targets; group filters labelled as the chips; `admin.areas` declared top from 768, left below.
- `tests/visual/audit.mjs` flags text narrower than a word (3+ lines, under 4 characters a line) at 1440, 834, 390 and 320 px with 200% text. Shell screens are clean on `8d48eae`; it still reports the draft findings table (authoring, #137) and the group results, scoring and report tables (results, 2e) at 390 / 320@200%, to be fixed in those PRs.
- Follow-up WI-0124: Remove administrator has no confirmation.
- Evidence: CI on `8d48eae` green (699 .NET, 191 Playwright); `visual-evidence/baseline-8d48eae`.

## Modernization audit (WI-0100, 2026-11-08)

- Reports and machine-readable coverage: `docs/modernization/` (start at
  `FINAL-REPORT.md`). Verdicts: requirements 76/204 independently verified
  (NOT PASSED); readiness NOT PASSED (admin, auth and storage blocked on owner
  production resources); overall NOT ACCEPTED.
- `requirements-coverage.json` is generated by `tools/modernization/coverage.fsx`
  and checked in CI (`--check`). Never edit it by hand: change the gap
  analysis, `requirements-supplementary.json` or the verdicts, then regenerate.
- Independent verification moved 24 groups from tested to partial in the gap
  analysis; WI-0101 holds the remediation. SEC-01 (record hash never compared
  on load, ADM-025/046) must be fixed before production storage.
- `ArchitectureTests` enforce the four-tier rules (Engine and Admin reference
  and effect rules; the C# shim only forwards).
- Next: C1, the Conditor upgrade to echelon-current 1.37.0
  (`docs/modernization/EXECUTION-PLAN.md`).

## SEC-01 record integrity (WI-0104, 2026-11-09)

- C3 part 1 of WI-0101. `RecordIntegrity` (Admin) is now the only place a
  record is encoded, decoded or validated (`ArchitectureTests`): every body
  Signal writes carries `bodyHash`, the `sha256:` of the rest of the body
  (Arca `Json.contentHash`), and every read verifies it. A canonical edit made
  outside Signal, or a record without the hash, is
  `SIGNAL.STORAGE.ALTERED_RECORD` and held aside; the roster, lifecycle and
  every other folder then open read-only.
- WI-0105 (review follow-up): the hash covers the whole Arca envelope (id,
  type, schema version, mutability) with the body, and an untrusted
  import-batch record makes `GroupAdmin.unfinishedBatches`, and so
  finalization, fail closed instead of dropping the record.
- Records written before WI-0104 carry no hash and are refused; no production
  dataset existed. Residual risks: rollback to an earlier sealed version of a
  mutable record is not detected (needs an outside anchor such as Arca commit
  history), and the hash is unkeyed (an editor who recomputes it is not
  detected; that needs a keyed signature).
- Not in this item: ARP-005, ACR-008/AUT-004, SEC-04, manifest child hashes
  (ADM-025 SHOULD) and cross-object version compatibility (ADM-046). ADM-025
  and ADM-046 stay `partial` until independently re-verified.

## Current state (2026-10-07)

- Requirement coverage is tracked per requirement group in
  [`docs/requirements/implementation-gap-analysis.md`](docs/requirements/implementation-gap-analysis.md)
  (baseline and current columns; `GapAnalysisTests` holds the counts to the
  rows). Ledger groups: 5 tested, 63 partial, 114 missing, 1 n/a (baseline
  0 / 39 / 143 / 1).
- Engine (`src/Echelon.Signal.Engine`, pure F#):
  - `Scoring`: built-in scoring catalog as data (WI-0035).
  - `Assessment`, `Pilot`: the SDRA pilot, scored through the catalog.
  - `Canonical`: canonical template form and TemplateHash (WI-0031).
  - `UrlState`: ResponseEncodingVersion 1 envelope codec (WI-0031).
  - `LiveUrl`: fragment-first live URL and resume decisions (WI-0032).
  - `Submission`: identified/anonymous finalization (WI-0033).
  - `Import`, `Aggregation`: import pipeline and SurveyGroupResult (WI-0034).
  - `Session`: respondent state machine and view.
  - `Template`, `TemplateCanonical`, `Drafts`, `Validation`, `TemplateDiff`,
    `Publication`: the generic canonical
    template, its canonical form `signal-template/1` and hash, and pure
    authoring, validation, fixtures, diff and immutable publication into a
    catalog (WI-0042). `Pilot.content` is SDRA in that form; the respondent
    page still runs on `Assessment`.
  - `RuleModel`, `Rules`, `RuleChecks`, `RuleCanonical`: the shared typed
    rule model (facts, flow, validation, completion, recommendations), its
    deterministic evaluation, publication-time checks and canonical form
    (WI-0043). `Groups`: roles, dependencies and group completion.
  - `ResultModel`, `Expression`, `Composite`, `Interpretation`, `Registry`,
    `Keyed`, `Benchmark`, `Compatibility`, `SurveyResult`, `GroupScoring`:
    overall scoring as an explicit composite or a typed custom expression,
    explanation traces from the same evaluation, interpretation separate from
    scoring, answer-key/ranking/allocation/pairwise scoring, benchmarks,
    selector-scorer compatibility, the canonical SurveyResult with display
    policy, and group scoring (WI-0044), built against the proposed answers in
    DF-SIGNAL-2026-0002.
  - `Primitives`, `Selectors`, `PrimitiveChecks`, `GenericEnvelope`,
    `Matrix`, `Navigation`, `Banking`, `Timers`: every closed-ended answer
    primitive with a bijective value index, the SCS-010 selector catalog and
    SCS-018 component map, item keys that score them in sections, the
    generic URL codec (bit-for-bit the SDRA layout for SDRA), invitations
    bound to an exact version, matrices, forward-only navigation, seeded
    banking and timers (WI-0045). The respondent page does not render
    generic templates yet (WI-0073).
  - `GroupResult`, `ReportModel`, `Report`, `ReportExport`: the generic
    group result with its hash, the format-neutral reporting contract
    (definitions, blocks, privacy classes, value states, warnings, status,
    comparisons), JSON/CSV exports, report snapshots and the V1 report
    families (WI-0046). Rendering through Folio is WI-0062.
- Application (`src/Echelon.Signal.Application`): Limen protocol (navigation
  replace and clipboard writeText effects), Aegis dispatch boundary, the
  entropy edge.
- The respondent page keeps the URL equal to the answers, resumes from it,
  and lets an invited respondent submit and copy the submission link.
- There is no administrator UI yet: import and aggregation are domain
  functions with tests only.

## Deep linking (WI-0066, 2026-10-08)

- Every administrator view has a canonical hash route (SIG-LINK-001..012,
  `docs/requirements/SIGNAL-DEEP-LINKING.md`, DF-SIGNAL-2026-0003). The route
  model and codec are `src/Echelon.Signal.Admin/Routes.fs` and
  `AdminNavigation.fs`; the inventory is `.echelon/routes.json` (a test holds
  it equal to the table).
- Routing is Limen 0.9.0's `EchelonFoundry.Limen.Routing` (WI-0071), installed
  by Conditor from echelon-current 1.11.0 into `vendor/nuget`. Limen's vectors
  (`tests/limen-routing`) test it, and `RouteSchemaTests` validates
  `.echelon/routes.json` against the shipped `contract/routes.schema.json`.
- DF-SIGNAL-2026-0003 decision 4 is closed (WI-0070): the respondent's `#r=`
  answer link is an accepted, scoped, disclosed exception.
- Browser suite: `SIGNAL_TEST_PORT` moves it off 4321 when that port is taken;
  the `pages` project serves the assembled site under `/signal/`.

## Template catalog (WI-0057, 2026-10-08)

- `TemplateDecodeCore`/`TemplateDecode` (Engine) read a template's canonical
  form `signal-template/1` back to the typed `Content`; every form
  round-trips to the same bytes and hash (`TemplateDecodeTests`).
- `TemplateRecord` (Admin) defines three Arca record types: the immutable
  `signal.template` (canonical bytes, re-hashed on every read), the mutable
  `signal.template-draft` per survey, and the mutable
  `signal.template-catalog` holding the hidden versions.
- `TemplateStore` (Application): `load`, `saveDraft` (`EditDrafts`, at a
  revision), `publish` (`PublishTemplates`, `Publication.publish` then one
  `Create`), `hide` (`PublishTemplates`). `TemplateStoreTests` run against
  Arca's in-memory provider.
- Not yet wired: the console still starts groups from the pilot, and there
  are no catalog or draft screens (WI-0073, with the authoring and
  respondent UI remainders).

## Report definitions, snapshots and exports (WI-0050, 2026-10-08)

- `ReportLibrary` (Admin, pure): versioned report definitions (an unused
  version is replaced, a used one never changes, an edit makes the next),
  exact dependency pins (template, or latest while drafting; visualization,
  result, aggregate and export schemas), immutable formal snapshots carrying
  their canonical report data (the clock is evidence outside every hash),
  `openSnapshot` (its own data or an explicit unresolved state, never
  current state) and `export` (JSON, sections CSV and lineage from the same
  ReportData; no locator or credential can reach it).
- `ReportRecord` (Admin): `signal.report-definition` (mutable, every version
  of one definition) and `signal.report-snapshot` (immutable, verified on
  read). `ReportStore` (Application): `load`, `saveDefinition` and
  `takeSnapshot` (`BuildReports`; snapshot and used definition in one
  commit), `export` (`ExportData`).
- Screens, Folio print/PDF, configuration packages and policy packs: WI-0075.

## Administrator privacy (WI-0051, 2026-10-08)

- `Disclosure` (Admin, pure): anonymous-group thresholds for groups, cells,
  distributions (with diversity) and comparisons; complementary suppression;
  a release ledger that withholds a view nesting with an earlier release and
  differing by fewer than the minimum (repeated snapshots freeze until enough
  responses arrive).
- `Audit`: PII-free records; ids must use Signal's prefixes or be OpaqueIds,
  hashes are `sha256:`, reasons are codes, no free text, clock outside the hash.
- `Retention`: the nine ADM-045 states and legal transitions (audited),
  retention per class, provider-honest claims (GitHub deletion is never
  "permanently erased"), retired datasets read-only, and ADM-064
  reconstructability with recorded limitations.
- WI-0076 applies them: every store operation writes its audit record in the
  same commit (`GovernanceRecord`, `GovernanceStore.audit`); the dataset
  lifecycle is stored and only an active dataset grants changes
  (`GovernanceStore.transition`); a finalized group's accepted results can
  be retired (`retireSources`) with the limitation recorded. Retirement
  removes results from Signal's state (a `signal.group-retirement` marker)
  and then, when the provider declares Arca's Erase capability, erases them
  from the current tree in one tombstone commit (WI-0077,
  DF-SIGNAL-2026-0021). The claim names only what landed
  (`Retired.Tree`); Git history still holds the content and is never claimed.
  The console (WI-0077 slice 2, `RetentionScreen`, `RetentionView`,
  `RetentionWork`) shows the dataset's lifecycle in the Storage view with
  its legal moves (never verified provider deletion), usable while the
  dataset is read-only, and retires a finalized group's results from its
  overview only after a confirmation. Page work must stay on the caller's
  context: never `Async.Sequential` or `Async.Parallel` (see `AdminWork`).
- Survey page selectors (WI-0081): every input settles through
  `GenericInputs` (`typed`, `pickedHeld`, `toggled`), so a value the link
  cannot read back (an unfinished ranking, a multi-choice count the rule
  refuses) is held on the page, never written. The visual selector variants
  wait for nested view lists in Limen (limen#108, DF-SIGNAL-2026-0022,
  WI-0099); never fixed option slots.
- Survey page presentation (WI-0081, VER-004): question numbers, the
  template's progress mode and the review before submitting live in
  `GenericReview`; the review is page state (`Response.Reviewing`), never in
  the link. Only drafts are deleted (`TemplateStore.delete`); a published
  version is hidden, never deleted (AUT-006 §§58-59).
- Question timers (WI-0081, ARX-013): a question's start is link terms entry
  kind 3 (SIG-LINK 1.5.0, DF-SIGNAL-2026-0015 §9), stamped when the question
  is first shown. Starts are whole minutes; the console says so. Any new
  entry kind must keep every committed fixture byte-identical
  (`QuestionTimerTests`) and refuse an unknown kind whole.
- Template resolution (WI-0081, ADM-055): groups resolve their exact template
  through `TemplateResolution` (built-in, then the dataset's catalog, then
  copies verified at the last good read), never by a plain lookup; the
  resolver returns the outcome's reason (`GroupRecord.TemplateResolver` is a
  `Result`). Every coded link error keeps a safe description
  (`DecodeErrorDescriptionTests`).
- Scoring explanations (WI-0081, DF-SIGNAL-2026-0024): `ScoringPreview.explain`
  renders `SurveyResult.compute`'s traces for the author; never add a second
  scoring path for display. Answer-key reasoning comes from
  `Template.keyedExplanation`, which `keyedNumber` itself uses.
- Draft preview (WI-0081, DF-SIGNAL-2026-0025): `DraftPreview` runs the draft
  through `GenericSession` as a test artifact in the console; its view is the
  survey page's own, under `preview`-prefixed names, with the survey row
  markup copied into web/admin/index.html. Keep the two row templates in step
  when either changes (the binding inventories hold both).
- Disclosure in the page (WI-0076): an anonymous group with a minimum above
  one shows its last released state (`Releases.shown`, recorded after each
  import as a `signal.group-release` of opaque keys) and says how many newer
  responses are held back; distributions are withheld when they could single
  respondents out; snapshots go through the same ledger.

## Catalog and authoring screens (WI-0073, 2026-10-09)

- The console's catalog is the built-in pilot plus the dataset's published
  templates (`TemplateCatalog`); groups resolve their template through it.
  Only templates with the group pipeline's shape start groups
  (`Pilot.assessmentOf`); the assessments area lists every version and
  draft with its status and, otherwise, why not.
- Authoring screens (`Authoring`, `AuthoringView`, `AuthoringWork`, route
  `#/templates/{survey}/draft`): start or derive a draft, edit metadata,
  sections, five-point questions and fixtures, see validation and the
  publication summary, save (EditDrafts), publish and hide
  (PublishTemplates).
- Stored custom expressions are held to the publication limits on load.
- Generic templates end to end (group pipeline, respondent page) and the
  rest of the authoring surface: WI-0078.

## Reports, snapshots and configuration (WI-0075, 2026-10-09)

- Report view: take a formal snapshot (BuildReports) of what the page shows;
  list a group's snapshots; download a snapshot's report JSON, sections CSV or
  lineage through `limen.files` (ExportData). The report prints through Folio.
- Report builder (`#/reports`): saved definitions by closed choices, checked
  and previewed against a group, saved as the next version once used; saved
  definitions render reports and are snapshotted (`ReportBuilder`).
- Policy packs (`PolicyPack`, built-ins StrictAnonymous and
  ExecutiveReporting) and configuration packages (`ConfigPackage`,
  `ConfigStore`): canonical, hash-identified, quarantined, activated
  explicitly with provenance.
- Remaining screens and enforcement: WI-0079.

## Validation

```bash
dotnet build Echelon.Signal.sln -c Release
dotnet test Echelon.Signal.sln --no-build -c Release
npm run build:wasm && npx playwright test
./praxis validate
```

### Merging

Merge only through the gate: `gh api -X PUT .../pulls/N/merge -f sha=<head>`
once every check run and workflow run on the head has completed and passed.
Query the runs with the **full 40-character head SHA**: the Actions
`runs?head_sha=` filter matches only a full SHA, so a short SHA returns no
runs and a waiting gate never sees them (WI-0081, 2026-10-13).
Signal's gate script is `signal-gate.sh <full SHA>` in the session
scratchpad, which refuses a short SHA. Don't use a shared name such as
`gate.sh`: another session overwrote that file with a script for another
repository on 2026-10-13. From 2026-10-14 the gate waits up to 60 minutes
(`signal-gate-60.sh`, the same checks with 180 polls), because Echelon
foundations runs queue behind other branches' runs; a timeout while runs
are still queued is not a failure, and the merge condition (every check on
the exact head green) is unchanged.
- Scoring authoring (WI-0081, DF-SIGNAL-2026-0026): `ScoringAuthoring`
  offers named models that compile to the scorer algebra; it never emits
  custom code.

## Unresolved questions

1. Where invitation links are minted: an administrator surface (WI-0011)
   must create `AnonymousInvitation`/`IdentifiedInvitation` envelopes.
2. Whether entropy should move from the WASM runtime's CSPRNG to a
   negotiated Limen entropy capability pack (ARX-004).
3. Measured URL budgets for 25-200 item surveys (CAN-004 §26).

## Next action

Highest-value remaining gaps, in order: administrator import UI and storage
(WI-0011, WI-0012, WI-0013) over the existing `Import`/`Aggregation` core;
template authoring/publication (WI-0003); flow, validation and
recommendations (WI-0006); report data contract (WI-0009, SRPP).
