---
id: DF-SIGNAL-2026-0005
title: Respondents read published generic templates from a static, same-origin catalog on the site, verified by hash before use
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
  - research/decisions/DF-SIGNAL-2026-0001--storage-through-arca-sign-in-through-fides-built-after-summa.md
  - research/decisions/DF-SIGNAL-2026-0003--deep-linking-through-limen-routing.md
  - docs/requirements/implementation-gap-analysis.md
tags: [templates, respondent, distribution, integrity, csp]
provenance:
  contributions:
    EXE-20261009T021311168Z-03fdc8e6:
      operations: [created]
      at: 2026-10-09T03:07:42.253Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Respondents read published templates from a static same-origin site catalog, verified by hash (WI-0078)"
---

# DF-SIGNAL-2026-0005 — Published templates on a static site catalog

- **Date:** 2026-10-09
- **Status:** accepted. Decided by the coordinator on the owner's standing
  instruction ("take the recommendation and continue"), 2026-10-09.
- **Work item:** WI-0078. **Requirements:** CAN-003, URLC-004, URLC-005,
  SCS-010..SCS-014, VER-004.

## Context

A generic template's respondent link carries only the template's compact
reference (the first 8 bytes of its hash) beside the encoded answers. The
respondent page has no access to the dataset's storage (DF-SIGNAL-2026-0001),
and the whole template does not fit the URL budget. The page needs the exact
published template, from somewhere it can trust.

Options considered: (a) a static catalog on the site; (b) fetching from the
dataset's repository, which works only when that repository is public, and
production repositories need not be; (c) pilot-only respondent links until
later.

## Decision

1. **The site serves each published template's canonical JSON as a static
   file** at `published-templates/<reference>.json`, where `<reference>` is
   the template's compact reference in lowercase hex (the first 8 bytes of
   its canonical hash). Templates are immutable per version, so the files may
   be cached for a long time.
2. **The respondent page fetches it from its own origin only** (a relative
   URL; the page's Content-Security-Policy sets `connect-src 'self'`), and
   uses it only after verifying it: it must be the canonical form of a
   template (`TemplateDecode.decode` refuses any other spelling), and its
   canonical hash must begin with the reference the link carries.
   - A missing file, a file that is not a canonical template, or one whose
     hash does not match the reference is refused with a clear message.
     There is no fallback to any other template.
3. **Publishing produces the file.** The administrator console offers each
   published version's site file for download ("For the respondent site").
   The site's Pages build copies `published-templates/` from the site
   repository into the site.
   - The dataset lives in the administrator's (possibly private) data
     repository, which the site's build cannot read without a credential it
     must not hold, so the build cannot yet fetch the export itself. Until a
     credential-free export channel exists, **the operator step is: download
     the file from the console and commit it to `published-templates/` in the
     Signal repository.** The Pages build then publishes it. The file holds
     the template only: no response data, no storage location, no credential.
4. **The link format does not change.** A generic template's links are the
   same `#r=<envelope>` fragment on the generic respondent page
   (`web/survey/`), so the deep-link contract (DF-SIGNAL-2026-0003) is not
   bumped; the pilot's links on `web/` are unchanged.

## Consequences

- A respondent can complete any published generic template the site carries,
  with the same guarantees as the pilot: answers stay in the fragment, never
  in a request.
- A template that is published but not yet on the site cannot be answered;
  the page says so and does nothing else.
- 64 bits of reference plus canonical decoding make a substituted template
  infeasible to pass as the referenced one; the page shows the full hash it
  verified.
