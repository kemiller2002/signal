---
id: DF-SIGNAL-2026-0027
title: Real sign-in on GitHub Pages is accepted without frame-ancestors while the owner is the only person who signs in
status: accepted
version: 1.0.0
created: 2026-11-12
updated: 2026-11-12
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/deployment.md
  - deploy/pages/site.json
  - deploy/pages/signal.deployment.production.json
tags: [security, deployment, pages, sign-in, fides]
provenance:
  contributions:
    EXE-20261112T222245388Z-cea10ab9:
      operations: [created]
      at: 2026-11-12T22:26:53.131Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Owner accepted the Pages framing risk while the only user (WI-0148)"
---

# DF-SIGNAL-2026-0027: Pages framing risk accepted while the owner is the only user

- **Date:** 2026-11-12
- **Status:** accepted (owner decision of 2026-11-12)

## Context

Signal is published to GitHub Pages (docs/deployment.md). Pages cannot send
response headers, so the Content-Security-Policy travels as a `<meta>` element,
and `frame-ancestors` (and `X-Frame-Options`) cannot be delivered that way:
another site can embed Signal's pages in a frame. The deployment guide says
that before real sign-in, Signal must either be hosted where the header can be
sent or accept the risk with a recorded decision.

The owner turned on real sign-in through Fides (the Echelon exchange at
`https://auth.echelonfoundry.com`, the shared `Echelon (production)` GitHub App,
Fides DF-FIDES-2026-0016) and is the only person who signs in.

## Decision

1. Real sign-in is enabled on the Pages site without `frame-ancestors`.
2. The accepted risk is clickjacking: a hostile page frames Signal's
   administrator page and tricks the signed-in person into clicking. Sign-in
   itself happens on github.com by top-level navigation, which GitHub protects;
   the exposure is actions inside Signal's administrator page while signed in.
3. **Condition:** before any person other than the owner signs in, Signal must
   be served where `frame-ancestors 'none'` is sent as a header (for example
   behind CloudFront in front of the Pages site, or another host), or this
   decision must be revisited. This matches Fides's own condition for
   splitting the shared GitHub App (Fides FID-DATA-009).

## Alternatives

- **Move hosting now** (CloudFront with a response-headers policy, or S3 and
  CloudFront): removes the risk, adds infrastructure for a single user.
- **A frame-busting script:** cannot be inline under the current policy, and
  script-based frame busting is bypassable; not adopted.

## Consequences

- `deploy/pages/site.json` names the production deployment configuration and
  allows connections to the exchange and `api.github.com`; the banner says
  sign-in is for the owner only.
- The condition above is the trigger to revisit this record.
