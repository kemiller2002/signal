---
id: SIG-LINK
title: Signal deep linking - every navigable view has a URL that opens it
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - signal
related_documents:
  - research/decisions/DF-SIGNAL-2026-0003--deep-linking-through-limen-routing.md
  - .echelon/routes.json
  - docs/requirements/implementation-gap-analysis.md
tags: [requirements, routing, deep-linking, limen, privacy]
provenance:
  contributions:
    EXE-20261008T194621846Z-e49f32ce:
      operations: [created]
      at: 2026-10-08T19:55:40.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the owner's deep-linking portfolio requirement as SIG-LINK-001..012"
---

# SIG-LINK — deep linking

These requirements come from the portfolio requirement the owner gave on
2026-10-08: **all navigable state lives in the URL, so a copied link opens
the same view.** Limen states the shared semantics
(`LIMEN-URL-STATE-REQUIREMENTS.md`, LCP-088..112) and ships the shared router
in 0.9.0; Signal matches it now (DF-SIGNAL-2026-0003).

Navigable state is what a person expects a link, a bookmark, Back, Forward
or a reload to bring back: the view, the identifiers of what it shows, and
its view parameters. Ephemeral state (an unsent import, a form being filled
in, a notice) is not navigable state and stays out of the URL.

**SIG-LINK-001 Navigable state in the URL.** Every view of the administrator
application MUST have a URL that opens it, with its identifiers and view
parameters: an assessment and its instrument version, a section and a
question of it; a group's results (the section drilled into, sort, counts or
percentages) and scoring; comparisons of groups; a group's imported surveys
and their outcome filter; the group list's filters; administrators; storage.

**SIG-LINK-002 Hash routes, relative links, one canonical form.** Routes
live in the fragment (`…/web/admin/#/groups/{group}/results?section=D01`), so
a static host such as GitHub Pages needs no fallback page. Links in the
markup are relative (`#/…`). A view has exactly one location: declared
parameters only, in declaration order; defaults omitted; sets sorted and
de-duplicated; every byte outside `A–Z a–z 0–9 - . _ ~` percent-encoded
with upper-case hex, a space as `%20`. A location in another form opens the
same view and is replaced by the canonical one.

**SIG-LINK-003 A pure route model and codec.** The route table, Signal's
typed route and the codec between them MUST be pure, total domain functions
(`src/Echelon.Signal.Admin/Routes.fs`): malformed input is a value, never an
exception, and `parse (format r) = r` for every route. The application edge
only carries locations and Navigation, Clipboard and tab-storage effects.

**SIG-LINK-004 History.** Moving to another place pushes a history entry;
refining the current view (a filter, sort, display, section) replaces it; a
location the browser reports (a deep link, Back, Forward, reload) is adopted
and never answered with a push. Back, Forward and reload restore the view
from the URL alone.

**SIG-LINK-005 Copy link.** Every routed view MUST offer a "Copy link"
control that writes the view's canonical absolute URL with the Clipboard
effect and says whether it was copied. The copied URL carries no credential
and no return target.

**SIG-LINK-006 The link target survives sign-in.** A view that needs
sign-in, opened while signed out, goes to `#/sign-in?returnTo=<location>`;
after sign-in the person lands on that view, with a replace so Back does not
return to sign-in. The target survives the identity provider's round trip
(it is kept in this tab's session storage, never sent to the provider). Only
a single-slash relative location of an eligible route is a target; anything
else resumes at home.

**SIG-LINK-007 Not found is a page.** An unknown route, an unknown
identifier (assessment, instrument version, section, question, group) and an
invalid parameter value MUST each render a clear page that says what was not
found or what is wrong, with a way home. None is a blank page or another
view. The URL is left as it was opened.

**SIG-LINK-008 No answers or respondent data in URLs.** Route URLs MUST
NOT carry answers, respondent-identifying data, names, e-mail addresses or
free text a person typed. Identifiers in routes are opaque (group keys from
random bytes, instrument, section and question ids). Parameter names
reserved for credentials (`token`, `secret`, `key`, `session`, `auth`,
`code` and the rest of LCP-109) are refused when the table is defined. The
group list filters by typed values (status, identity mode, survey), not by
free text. The respondent page's answer document is not a route; see
DF-SIGNAL-2026-0003 decision 4.

**SIG-LINK-009 Route inventory.** `.echelon/routes.json` MUST be the route
table's `echelon.routes/v1` inventory: sorted keys, two-space indentation, a
final newline, byte-identical to what the table renders (a test holds them
equal).

**SIG-LINK-010 Limen's router API.** Signal MUST use Limen's routing API
(`Limen.Routing`: `RouteTable.define`, `RouteCodec.create/parse/format`,
`Navigation.adopt/navigate/refine`, `ReturnTo.capture/resume`, `Link.share`)
and pass Limen's routing conformance vectors, so that adopting Limen 0.9.0 is
a reference swap.

**SIG-LINK-011 Legacy links.** Administrator links published before
SIG-LINK (`#/overview`, `#/groups/{group}/explore/…`) MUST redirect to their
current routes.

**SIG-LINK-012 Verified in a browser.** Browser tests MUST open deep links
cold (a fresh page at the deep link), including against the assembled Pages
site served under a sub-path, and prove sign-in return, not-found pages,
Back and Forward, and Copy link.
