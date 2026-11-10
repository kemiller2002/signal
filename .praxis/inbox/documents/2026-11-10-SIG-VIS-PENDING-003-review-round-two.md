# Pending Praxis intake: SIG-VIS-PENDING-003 second review round on PR #133

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-10
- Source: independent review of kemiller2002/signal#133 at 771c98f (REQUEST CHANGES), relayed by the coordinator.

## Requirements

**SIG-VIS-PENDING-003.1** (high) A failure settles the running operation only when it came from that operation's own work. Failures of background work (sign-out, a cross-tab receive) are presented through Aegis and leave the operation and its guard alone. Wire test: a held save, a broadcast receive that throws, the save stays running, a Refresh is refused until the save answers.

**SIG-VIS-PENDING-003.2** (high) Focus after an answered change lands beside where it was made: retire on the retired results (`#sources-retired`) when it worked, otherwise on the "Retire accepted results…" button; group transitions on the group's phase; dataset lifecycle on its lifecycle heading. `#activity` stays the announcer; delete draft and hide version may keep it. Browser tests assert the focused element and that it is in the viewport.

**SIG-VIS-PENDING-003.3** (high) Every element focused from code shows a visible `:focus` outline (not only `:focus-visible`) of at least 3:1 contrast in light and dark, from the Forma focus-ring token, in Signal's own CSS; evidence of the ring in both themes.

**SIG-VIS-PENDING-003.4** A programming defect inside an operation never leaves the page running it: the settled state is kept before Aegis re-raises (WI-0114). Test with a planted defect.

**SIG-VIS-PENDING-003.5** A report's template pin changed while its definition saves stays unsaved. Test.

**SIG-VIS-PENDING-003.6** The retire browser test holds one specific request and asserts one definite outcome.

**SIG-VIS-PENDING-003.7** Refreshed visual evidence (retire, group transition, the focus ring light and dark), recorded apart from the functional evidence.
