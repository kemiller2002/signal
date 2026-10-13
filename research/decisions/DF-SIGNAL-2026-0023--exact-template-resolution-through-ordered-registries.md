---
id: DF-SIGNAL-2026-0023
title: Exact template resolution runs through ordered registries with ADM-055's outcomes; a verified copy is used only when a registry cannot be read
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - signal
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [templates, resolution, registry, cache, provenance]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T22:33:50.885Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Exact template resolution through ordered registries (ADM-055, WI-0081)"
---

# DF-SIGNAL-2026-0023: Exact template resolution through ordered registries

- **Date:** 2026-10-13
- **Status:** accepted. This is a routine design choice, taken under the coordinator's standing approval. It adds no foundation, credential or link change.
- **Work item:** WI-0081. **Requirement:** ADM-055.

## Context

Until now, a group's template was found by an incidental lookup: `TemplateResolver` returned an option, and a miss became "not in the catalog". ADM-055 asks for an explicit resolution subsystem. It must distinguish named outcomes, keep negative knowledge scoped to what was searched, fail on a hash mismatch, never replace an exact template with "latest", and keep a verified local copy.

## Decision

1. **`TemplateResolution` (Admin, pure) is the subsystem.**
   - It resolves a request (`ByHash`, or `ByVersion` survey and version) against registries in priority order, plus copies verified earlier.
   - It returns one of the nine outcomes ADM-055 names: `ResolvedExact`, `NotFoundInRegistry`, `RegistryUnavailable`, `RegistryUnauthorized`, `HashMismatch`, `ConflictingArtifacts`, `UnsupportedTemplateVersion`, `CachedVerifiedCopyAvailable` and `ResolutionUnknown`.
   - Only `ResolvedExact` and `CachedVerifiedCopyAvailable` yield a template (`usable`).
2. **Registries, in priority order:** the built-in catalog, then the dataset's stored catalog.
   - The built-in catalog is compiled in, so it is verified by construction.
   - The stored catalog is read through Arca, with every template re-hashed (`Publication.verify`).
   - A template absent from the first is recovered from the second.
   - Each registry reports its state: available, unavailable, unauthorized, or incomplete (read only in part, so absence proves nothing).
3. **Failing resolution:**
   - A template whose content does not hash to its identity fails resolution (`HashMismatch`).
   - Two artifacts claiming one survey and version with different hashes fail it too (`ConflictingArtifacts`), whichever was asked for. Neither is used.
4. **Never a substitute.**
   - A request names an exact hash, or an exact survey and version. A version that isn't held is `NotFoundInRegistry`, listing each registry searched with its revision. It is never answered with another version.
   - A found template that declares a capability this engine lacks is `UnsupportedTemplateVersion`.
5. **The verified local copy.**
   - The console keeps the stored templates verified at the last good read of the catalog (`TemplateCatalog.Loaded.Cache`).
   - When the stored catalog cannot be read, groups resolve from those copies (`CachedVerifiedCopyAvailable`), and the outcome names the registry and why. A copy that no longer hashes to its identity is not used.
   - The cache lives for the console session; it is never written anywhere.
6. **The reason reaches the person.** `GroupRecord.TemplateResolver` now returns the resolution's reason, so a group whose template cannot be used says what was searched and why, for example "…was searched for in the built-in catalog and the dataset's template catalog at r and is not there."

## Scope: what ADM-055's SHOULD list gets here

| SHOULD item | Here |
|---|---|
| One or more configured registries | **Not here.** Signal's registries are fixed: built-in, then the dataset's. An extra configured registry would need its own trusted location and credential path, and no deployment needs one. A future registry joins the ordered list as one more `Registry`, with no change to the outcomes. |
| Exact lookup by hash or by survey and version | Yes |
| Local verified cache | Yes |
| Cache freshness and provenance | **Provenance:** the registry named in the outcome. **Freshness:** "the last good read of this console session". No read time is recorded. |
| Registry capability discovery | Each registry's read state (available, unavailable, unauthorized, incomplete) |
| Priority order | Yes |
| Explicit pinning | Groups pin the exact hash |
| Offline use of a verified copy | Yes |
| Recovery from an alternate registry | By priority order |
| Conflict detection | Yes |

Registry credentials are the dataset's own (Fides, through Arca), so they follow the storage-provider rules unchanged.

## Consequences

- `TemplateResolutionTests` covers every outcome, and the console's catalog path (`TemplateCatalog.resolution`, `resolver`, `unreadable`).
- The durable import test that a missing template blocks a group now asserts the scoped reason.
