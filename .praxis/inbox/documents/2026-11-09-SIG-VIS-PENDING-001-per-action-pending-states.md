# Pending Praxis intake: SIG-VIS-PENDING-001 per-action pending state, loading regions and state chips

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-09
- Source: the owner-approved "foundry ledger" visual refinement (Kevin, 2026-11-09 20:24 local) and its interface contract (Echelon application layer, `@echelon-foundry/echelon-theme` 1.1.0), relayed by the coordinator as the first Signal change. This change is the behaviour and data underneath the visual change; it must not depend on the theme's `echelon-` classes rendering (1.1.0 is not released).

## Requirements

**SIG-VIS-PENDING-001.1** Every asynchronous operation the administrator page starts shows visible feedback (design addendum item 7). The page-wide `Busy` flag is replaced by per-action pending state that records which command is in flight.

**SIG-VIS-PENDING-001.2** The button that started an operation carries `aria-busy="true"` (Forma 1.0.1 draws its ring for `.forma-button[aria-busy="true"]`) and, while pending, a present-participle label in the product's own words ("Saving draft", "Importing", "Publishing").

**SIG-VIS-PENDING-001.3** The safety property of the global flag is kept: where concurrent writes must be prevented, the other actions stay disabled. The conflict decision is derived from the code and documented; no guard is loosened. A second activation while an operation is pending never queues a duplicate write.

**SIG-VIS-PENDING-001.4** The persistence indicator tells the truth: pending, saved, failed, unknown (reconciling). It never shows "saved" before the store confirms.

**SIG-VIS-PENDING-001.5** Covered: SaveDraft, PublishDraft, RequestReview, ApproveReview, DeleteDraft, HideVersion, CreateGroup, IssueInvitations, ImportArtifacts, TransitionGroup, ChangeRoster, RebuildIndex, TakeSnapshot, ExportSnapshot, SaveReportDefinition, RetireSources, TransitionDataset, OpenDataset, Refresh and sign-in, plus downloads and copy-link where they are asynchronous; anything else found is listed.

**SIG-VIS-PENDING-001.6** Text-only loading paragraphs become a Forma spinner with a visible label in a `role="status"` container that also carries `echelon-loading` (survey "Loading the survey…", admin "Loading the deployment's configuration…"), and every other perceptible wait gets the same treatment.

**SIG-VIS-PENDING-001.7** Each group's phase (`AdminState.GroupPhase`) and each template version's lifecycle (draft, in review, published, hidden) are view-model fields bound to `data-echelon-tone` with a visible text label, rendered as `<span class="echelon-chip" data-echelon-tone="…">Label</span>`, using the contract's tone mapping and derived from the existing model (semantic projection: never re-implemented in the view).

**SIG-VIS-PENDING-001.8** Tests: F# unit tests of the pending transitions (start, success, failure, unknown outcome, the concurrent-write guard, rapid repeat) and an exhaustive phase/lifecycle-to-tone mapping; browser tests (existing fakes, the GitHub-store fake extended to delay a response) of the busy button, the guard, loading regions and chips. No existing test is weakened, skipped or deleted.

**SIG-VIS-PENDING-001.9** Visual-review evidence of the busy and loading states from the Visual capture workflow (WI-0110) is recorded separately from the functional evidence.
