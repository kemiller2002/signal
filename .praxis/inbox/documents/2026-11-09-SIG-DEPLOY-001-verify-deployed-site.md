# Pending Praxis intake: SIG-DEPLOY-001 verify the deployed site after each deploy

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-09
- Source: owner directive "Forma Brand Separation and Logo Asset Repair" (EDS-ADR-002), phase 6, "actual deployed URL checks where deployment is available"; EDS-BRAND-APP-001

## Requirements

**SIG-DEPLOY-001.1** After every Pages deployment from main, a job checks the deployed URL, not the checkout:
- the assessment, survey, administrator and landing pages answer 200;
- every entry page carries Signal's own favicon, never the empty `data:,` icon;
- every engine page carries the pre-runtime start-up screen;
- the landing page carries the application identity;
- `web/brand/identity.json` records the mark as approved, with favicon and dark variants;
- every referenced brand asset resolves as an SVG.

The job retries while Pages propagates, then fails.

**SIG-DEPLOY-001.2** The check is unit-tested against the assembled site, served locally. Adversarial copies must be reported:
- a missing favicon;
- a missing dark mark;
- an engine page without its start-up screen;
- an empty favicon;
- a missing landing page;
- an unapproved mark.
