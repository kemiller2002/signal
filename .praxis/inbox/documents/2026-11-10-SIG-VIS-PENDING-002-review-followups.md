# Pending Praxis intake: SIG-VIS-PENDING-002 review follow-ups to SIG-VIS-PENDING-001 (PR #133)

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-10
- Source: independent review of kemiller2002/signal#133 at 3636805 (REQUEST CHANGES), relayed by the coordinator.

## Requirements

**SIG-VIS-PENDING-002.1** (high) Delete draft and Retire accepted results keep their confirmation, with its busy confirm button, until the operation settles; then the confirmation closes and focus moves to a visible status element near the action. Browser tests (focus included) and capture screens for both.

**SIG-VIS-PENDING-002.2** The recovery and outcome lines (reload owed, unknown-outcome guidance, partial import) sit next to the notice and status where the person acts.

**SIG-VIS-PENDING-002.3** An exception inside an operation settles it as unknown (never failed or not saved); other messages of the same drain are kept. Tests.

**SIG-VIS-PENDING-002.4** Edits made while a draft or report definition saves are never reported as saved nor discarded. Tests.

**SIG-VIS-PENDING-002.5** After an unknown outcome the UI blocks resending until a reload reconciles, matching its message. A test of the roster wire path.

**SIG-VIS-PENDING-002.6** One always-present live region announces each operation start and settle once; the notice is not a second live region.

**SIG-VIS-PENDING-002.7** Tests: failed and unknown asserted separately; answer ordering (snapshot, definition, dataset lifecycle, created group); roster unknown mapping; hide, export and version lifecycle exercised in a browser.

**SIG-VIS-PENDING-002.8** Reload-owed tracked against reads started after the change; the last change's outcome cleared by a reload; "group created" and its read-back failure kept together; per-action pending wording.

**SIG-VIS-PENDING-002.9** Human phase labels derived exhaustively from GroupPhase; chips separated from following text; the PR body records Hidden -> closed and Reviewed -> review for the theme contract.

**SIG-VIS-PENDING-002.10** Remaining follow-ups recorded with `praxis add`; completion evidence lists every changed file.
