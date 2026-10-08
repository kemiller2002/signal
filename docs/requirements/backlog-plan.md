# Signal backlog plan

Captured 2026-10-08 (WI-0037) from the missing and partial groups of [`implementation-gap-analysis.md`](implementation-gap-analysis.md). Each slice refines the broad ledger items WI-0002..WI-0020, which stay as the requirement-accounting record. Build order, per the user (DF-SIGNAL-2026-0001): Chrona first, then Summa, **then Signal** and the rest. Storage and sign-in slices depend on Arca (kemiller2002/arca) and Fides (kemiller2002/fides). `GapAnalysisTests` fails if any group that is not yet `tested` is not named by an open work item.

**Pull-forward (2026-10-08, DF-SIGNAL-2026-0001 amendment 1):** the pure-domain slices that depend on no Arca or Fides slice (orders 5-9: WI-0042..WI-0046) start now, ahead of Chrona and Summa. The rest of the order stands.

| Order | Work item | Slice | Depends on |
|---:|---|---|---|
| 1 | WI-0038 | Signal 01: configurable data location and Signal-owned namespace through Arca (SIG-DATALOC-001, ADM-004 location/namespace, ADM-005, ADM-057) | arca slice 2 (data location, ARCA-LOC) and arca slice 3 (record format, ARCA-REC) |
| 2 | WI-0039 | Signal 02: storage provider contract implemented on Arca's GitHub provider (ADM-003, ADM-004, ADM-006, ADM-025, ADM-026, ADM-044, ADM-046, ADM-058, ADM-073; AER-002, AER-003, AER-032) | WI-0038, arca slice 6 (GitHub adapter, ARCA-API/OUT), arca slice 7 (conformance suite, ARCA-TEST) and arca slice 8 (integrity, ARCA-INT) |
| 3 | WI-0040 | Signal 03: administrator sign-in through Fides - credential lifecycle, browser secret policy, cross-tab coherence (ADM-056, ADM-071, ADM-072) | fides slice 7 (WASM client and Arca token provider, FID-CLI) and arca slice 5 (token-provider port, ARCA-AUTH) |
| 4 | WI-0041 | Signal 04: durable import, concurrency, OutcomeUnknown and rebuildable indexes (ADM-008..011, ADM-027, ADM-060, ADM-061, ADM-067; ARP-003..006; ARX-008; LURL-003, LURL-005) | WI-0039 |
| 5 | WI-0042 | Signal 05: canonical domain contracts, template authoring, publication and versioning (CAN-001, CAN-005, CAN-008, URLC-004, ACR-003, ACR-008, VER-003, VER-004, VER-007, AUT-001..007, ARX-002) | - |
| 5b | WI-0057 | Signal 05b: persist the template catalog through Arca and finish the authoring remainders WI-0042 left (preview isolation, generic pagination on the page, locale/expiry, roles, cross-device checks) | WI-0042 (complete) and WI-0039 |
| 5c | WI-0073 | Signal 05c: the stored catalog wired into runtime resolution and the console, and the authoring and respondent UI remainders of WI-0057 | WI-0057 |
| 6 | WI-0043 | Signal 06: rules, flow, validation, completion and recommendations; group identity semantics (ACR-001, ACR-002, ACR-007, CAN-002, VER-006, ID-001, ID-004 remainders) | WI-0042 |
| 7 | WI-0044 | Signal 07: scoring AST, advanced algorithms and explainability (AST-001..006, ALG-002..004, SCS-002/003 remainders, SCS-004, SCS-006, SCS-007, SCS-015, CAN-006, VER-005, ARX-014) | WI-0042 |
| 7b | WI-0059 | Signal 07b: scoring remainders after WI-0044 (ipsative, confidence- and completeness-adjusted scoring, per-section direction, weakest/strongest and confidence outputs, single-response NPS guard, recommendations over the overall and group result, shared group metadata) | WI-0044 (complete) |
| 8 | WI-0045 | Signal 08: answer primitives and selectors - multi-choice, matrix, ranking/allocation, timers and banking, encodings (ANS-002, ANS-004 remainder, SCS-009..014, SCS-016, SCS-018, ARX-013, CAN-004/LURL-004 remainders) | WI-0042 |
| 8b | WI-0060 | Signal 08b: encoding remainders after WI-0045 (signed submission policy, presence-bitmap evaluation) | WI-0045 (complete) |
| 9 | WI-0046 | Signal 09: reporting contract and Folio renderers - report blocks, group reports, comparisons, the standard results print profile (RPT-001..006, SRPP-001..185) | WI-0044 |
| 9b | WI-0062 | Signal 09b: render ReportData through Folio and the page (print profile, HTML/PDF, localization, remaining report families, benchmark comparisons in reports) | WI-0046 (complete); touches web/ and Folio |
| 10 | WI-0047 | Signal 10: administrator application state and UX on Limen/Forma - groups, read-only and degraded modes, conflict workspace, sealing and finalization (ADM-001, ADM-002, ADM-007, ADM-031..033, ADM-035, ADM-062, ADM-065, ADM-066, ADM-070, ADM-077) | WI-0040 and WI-0041 |
| 11 | WI-0048 | Signal 11: analytics, comparisons, lineage, change-impact and dependency invalidation (ADM-012..014, ADM-019, ADM-020, ADM-041, ADM-049, ADM-051, ADM-054; ARX-012 remainder) | WI-0041 |
| 12 | WI-0049 | Signal 12: visualization grammar, dashboards and accessibility (ADM-015..018, ADM-050, ADM-069) | WI-0048 |
| 13 | WI-0050 | Signal 13: report builder, snapshots, exports, configuration packages and policy packs (ADM-021..023, ADM-047, ADM-048, ADM-063) | WI-0046 and WI-0048 |
| 14 | WI-0051 | Signal 14: administrator privacy, audit without PII, retention and deletion (ADM-024, ADM-030, ADM-045, ADM-064; ARX-009; ID-003 remainder) | WI-0041 |
| 15 | WI-0052 | Signal 15: storage migration, backup and restore, schema evolution and operational repair (ADM-028, ADM-029, ADM-034, ADM-053, ADM-074..076) | WI-0041 and arca slice 10 (derived indexes and migration, ARCA-MIG) |
| 16 | WI-0053 | Signal 16: cross-cutting verification - static analysis, differential and model tests, performance budgets, incremental evaluation, time semantics (ARX-001, ARX-004, ARX-005, ARX-010, ARX-011, ARX-015, CAN-007, ADM-036..038, ADM-059, ADM-068; ACR-006 localization) | WI-0047 |
| 17 | WI-0054 | Signal 17: administrator sandbox, synthetic data generator and optional analytics extensions (ADM-039, ADM-040, ADM-042, ADM-043) | WI-0047 |

The backlog itself lives in `.ros/work/queue.json` and is managed only through the Praxis CLI. This table is a readable snapshot from when the slices were captured.
