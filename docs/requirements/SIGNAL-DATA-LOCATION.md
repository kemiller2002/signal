---
id: SIG-DATALOC
title: Signal data location, application-owned namespace and permission separation
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - signal
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [requirements, storage, data-location, arca]
provenance:
  contributions:
    EXE-20261008T074805556Z-fd72229f:
      operations: [created]
      at: 2026-10-08T07:49:07.809Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record user decisions of 2026-10-08 and the per-application data-location requirement"
---

# SIG-DATALOC — data location, namespace and permission separation

These requirements come from user decisions of 2026-10-08. They apply to
Signal and to every Echelon application that stores data through Arca
(`kemiller2002/arca`, requirements `ARCA-LOC-001..010`, decision
`DF-ARCA-2026-0002`).

**SIG-DATALOC-001 Configurable data location.** The data repository (owner,
repository, branch, base path) MUST be configured per deployment. Signal
MUST NOT hard-code a repository, owner, branch or root path.

**SIG-DATALOC-002 Application-owned namespace.** Signal MUST create and own its
own folder structure (namespace) in the configured repository and keep every
read and write inside it.

**SIG-DATALOC-003 Shared repositories.** Signal MUST NOT assume it is the only
application using the repository, or that it owns the repository root.

**SIG-DATALOC-004 Separable permissions.** Signal's data MUST be separable from
other applications' data under different permissions (Signal and Summa, for example,
have different permissions). GitHub permissions apply per repository, not
per folder, so separation is achieved by pointing Signal at **its own
repository** through SIG-DATALOC-001. A shared repository is allowed only when the
applications in it may share permissions.

**SIG-DATALOC-005 No at-rest encryption for now.** Signal MUST NOT add
application-level at-rest encryption. Per-application encryption is a
deferred, possible future Arca item, not a requirement.

Related Signal requirements: ADM-004 (configurable repository, branch, root path and namespace), ADM-057 (multiple storage profiles), ADM-025 (storage security; optional encryption stays deferred).
