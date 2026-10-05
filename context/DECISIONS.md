# Echelon Signal decisions

Material decisions use `DF-` records under `research/decisions/`. This compact
table is a navigation view, not a replacement for those records.

| Date | Decision | Status | Rationale | Record |
|---|---|---|---|---|
| 2026-08-31 | Use ROS 1.2.1-main.16.1 as a measured greenfield pilot. | provisional | Test portability and operational value on a real beginning project. | Not yet promoted to a `DF-` record |
| 2026-10-05 | Declare Aegis, Forma and Folio not yet applicable. | superseded | Signal had no .NET tier, browser surface or document output. | `DF-SIGNAL-FND-2026-0001` |
| 2026-10-05 | Build Signal the same way as the other Echelon applications: F# engine on WebAssembly behind Limen, Aegis at its boundary, Forma and Folio from the pinned `echelon-current` releases; all foundations required. | accepted | Owner: "we want all apps built the same way." | `DF-SIGNAL-FND-2026-0002` |
