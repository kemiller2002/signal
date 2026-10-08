---
id: DF-SIGNAL-2026-0003
title: Deep linking through Limen's routing semantics and the Limen.Routing package, and the respondent answer document
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - .echelon/routes.json
  - vendor/nuget/limen-fsharp.lock
tags: [routing, deep-linking, limen, privacy]
provenance:
  contributions:
    EXE-20261008T194621846Z-e49f32ce:
      operations: [created, modified]
      at: 2026-10-08T19:55:41.000Z
      last: 2026-10-08T20:47:10.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide deep linking through Limen routing, the interim Limen.Routing copy and the respondent answer document"
    EXE-20261008T213653081Z-8b5e5cfc:
      operations: [modified]
      at: 2026-10-08T21:36:54.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Close decision 4: the respondent answer link is an accepted, scoped exception (WI-0070)"
    EXE-20261008T214707523Z-5fa741d0:
      operations: [modified]
      at: 2026-10-08T21:51:51.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decision 1: Limen.Routing is now the Limen 0.9.0 package (WI-0071)"
---

# DF-SIGNAL-2026-0003 — Deep linking through Limen routing

- **Date:** 2026-10-08
- **Status:** accepted. Decision 4 was confirmed by the coordinator on the
  owner's standing instruction, 2026-10-08 (WI-0070).
- **Work item:** WI-0066. **Requirements:** SIG-LINK-001..012.

## Context

The owner made deep linking a portfolio requirement: all navigable state in
the URL, so a copied link opens the same view. Limen 0.9.0 will ship the
shared router (`EchelonFoundry.Limen.Routing`, LCP-088..112); another agent
is releasing it. Signal was asked to match its API now so that switching
later is mechanical.

## Decisions

1. **Limen.Routing, now the Limen 0.9.0 package.** Signal first vendored
   Limen's F# reference router byte for byte (limen PR #101 at `e935da7`,
   SHA-256 pinned) in `vendor/limen-routing`. Since Limen 0.9.0 (WI-0071) it
   references `EchelonFoundry.Limen.Routing` 0.9.0: an attested release asset
   that Conditor installs into `vendor/nuget` from echelon-current 1.11.0
   (`limen-fsharp`, `vendor/nuget/limen-fsharp.lock`). The swap changed only
   project references, because the namespace, modules and signatures were the
   package's own. Limen's conformance vectors (`tests/limen-routing`, from
   v0.9.0, identical to `e935da7`) run against the package, and
   `.echelon/routes.json` validates against the shipped
   `contract/routes.schema.json`.
2. **Hash mode for the administrator application.** Routes live in the
   fragment of `web/admin/`, links are relative (`#/…`), the canonical form
   is Limen's, and the inventory is written to `.echelon/routes.json`.
   Pages needs no 404 fallback.
3. **The return target crosses the OAuth round trip in tab storage.** The
   provider's redirect drops the fragment, so on "Sign in" the canonical
   target from `#/sign-in?returnTo=…` is kept in this tab's session storage
   (`signal.admin.returnTo`, through the `signal.host` pack) and recalled
   when the page returns with the provider's callback. It is never placed in
   the provider's URL or its `state`. It is only a route location (opaque
   identifiers and view parameters), resumed through `ReturnTo.resume`, and
   removed once used.
4. **The respondent's answer link is an accepted, scoped exception.** The
   respondent page (`web/`) keeps its answers in its fragment, `#r=<envelope>`.
   This is required by LURL-001 and ARX-007: the link is the respondent's own
   hand-off transport, and the finalized link is how a submission reaches the
   administrator (ARP-001). Decided 2026-10-08 (WI-0070) as an accepted,
   documented exception to "never answers in URLs", with this scope:
   - **Where:** the respondent page only, in the fragment, which browsers
     never send to a server.
   - **Never logged:** no Aegis fault record or kernel diagnostic carries it.
   - **Never a route:** no route of the inventory names it.
   - **Never produced by Copy link** on an administrator view.
   - **Disclosed:** the respondent page says plainly that its address and
     the submission link contain the answers.

   `RespondentLinkTests` proves each point. Every other URL follows the
   rule: route URLs never carry answers or respondent data (SIG-LINK-008).
5. **Typed filters replace the free-text group filter.** Filters are
   navigable state, and free text a person typed is the kind of data LCP-109
   keeps out of URLs. The group list filters by status, identity mode and
   survey, which are typed and safe.
6. **The inventory's guard field is `guards`.** The brief described a
   route's `guard`; Limen's inventory writes `guards`, the guards along the
   route's chain (README "Route inventory"). Signal follows Limen's code,
   since the goal is identical output.
7. **Legacy explore links keep the group, not the view settings.** Limen's
   redirect templates carry path parameters; `#/groups/{g}/explore/{…}`
   redirects to the group's results with default sort and display.
8. **Assessments come from the built-in catalog** (the pilot) until the
   stored template catalog exists (WI-0057). Their routes need no sign-in:
   the instrument is already public on the respondent page.

## Consequences

- Moving to Limen 0.9.0 (WI-0071) changed two project files and deleted one vendored folder.
- Every administrator view gains a URL; an unknown identifier or an invalid
  parameter renders its own page.
- The embedded administrator report state (ARP-003) is not a route and no
  route can carry it, but it holds per-respondent dimension scores and, in
  identified groups, invitation-linked identity keys. Replacing those is
  WI-0067.
