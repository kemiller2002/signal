---
id: DF-SIGNAL-2026-0006
title: Respondent links carry an invitation's locale and expiry in a new link format 2, inside the link's integrity check; format 1 links are unchanged
status: accepted
version: 1.0.0
created: 2026-10-09
updated: 2026-10-09
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SIGNAL-2026-0003--deep-linking-through-limen-routing.md
  - research/decisions/DF-SIGNAL-2026-0005--respondents-read-published-templates-from-a-static-site-catalog.md
  - docs/requirements/SIGNAL-DEEP-LINKING.md
  - docs/requirements/implementation-gap-analysis.md
tags: [respondent, url, encoding, expiry, locale, integrity]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-09T04:38:25.049Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Respondent links carry locale and expiry in link format 2 (VER-003, WI-0081)"
---

# DF-SIGNAL-2026-0006 — Locale and expiry in respondent links

- **Date:** 2026-10-09
- **Status:** accepted. Decided by the coordinator on the owner's standing
  instruction ("take the recommendation"), 2026-10-09.
- **Work item:** WI-0081. **Requirements:** VER-003 (locale and invitation
  expiry carriage), URLC-003, LURL-001, SIG-LINK-008.

## Context

An invitation can have a language and a last day on which it may be
answered (VER-003). A respondent link (`#r=<envelope>`) is the only thing the
survey page and the importer share, so both facts have to travel in it. The
link format is a contract: existing links, golden vectors and submission
hashes must not change meaning.

## Decision

1. **A new, versioned link format.** A generic envelope whose first byte is
   2 (`GenericEnvelope.TermsFormatVersion`) carries *terms* between the
   binding ids and the item count: one flags byte (bit 0 locale, bit 1
   expiry, other bits zero), then the locale as a length byte and ASCII
   BCP 47 tag of at most 24 characters, then the expiry as a 2-byte count of
   days since 1970-01-01 (the last UTC day the invitation may be used). The
   rest of the layout is format 1's.
2. **Format 1 is untouched.** A link without terms is still written as
   format 1, byte for byte, so every existing link, fixture, golden vector
   and submission hash is unchanged; format 1 links decode and behave exactly
   as before (tests open the committed fixture links). The pilot codec
   (`UrlState`) does not read or write format 2.
3. **One canonical spelling.** Empty flags, unknown flag bits, a malformed or
   over-long locale, or terms that run past the body are refused
   (`InvalidTerms`), never repaired.
4. **Tamper-evident expiry.** The terms are inside the bytes the link's
   integrity check and the SubmissionHash cover: editing the expiry or the
   locale fails the integrity check. That check is an integrity check, not a
   signature; a signed authenticity policy remains WI-0060.
5. **Expiry is enforced at both ends.** The survey page refuses an
   unsubmitted response to an expired invitation with "This invitation has
   expired" and shows no questions; a submitted link still opens sealed.
   Production intake refuses a submission whose invitation expired before
   the intake day with the stable code `rejected:invitation-expired`
   (`Import.InvitationExpired`). Re-reading an accepted submission
   (`Intake.reconstruct`) does not apply expiry.
6. **Locale is presentation only.** It sets the survey page's language; it
   never changes scoring, completion or identity (a test holds the results
   of two locales equal).
7. **Within the URL budget.** The capacity of the largest response now
   includes the largest terms (28 bytes, `Layout.TermsOverheadBytes`); a test
   holds capacity equal to the longest real link.
8. **The link contract is bumped.** SIG-LINK goes to 1.1.0 with
   SIG-LINK-013, which names the respondent link formats.

## Consequences

- Templates near the URL budget have 28 fewer bytes for answers; the
  publication capacity check accounts for it.
- The console does not issue invitations with terms yet; the codec, the page
  and intake support them (`GenericEnvelope.invitationWith`).
