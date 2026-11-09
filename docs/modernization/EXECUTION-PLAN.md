# Execution plan

Work item: WI-0100 · Ordered by dependency. Each cohort is a coherent PR with its own Praxis work item, evidence and independent check. No cohort claims a requirement complete without independent verification.

| Cohort | Scope | Depends on | Gate to merge | Owner input |
|---|---|---|---|---|
| **C0** (this PR, WI-0100) | Audit, coverage matrix + CI drift gate, architecture tests, gap-analysis corrections, WI-0101 capture | — | full suite, Praxis validate, CI | — |
| **C1** platform currency (MOD-PLAT-001). **Done: WI-0102, WI-0103 (echelon-current 1.37.1)** | `conditor upgrade --current --stepwise` to echelon-current 1.37.0 (Praxis 3.25.0, Ordo 1.20.0, Forma 0.13.0, Folio 0.4.1, Arca 0.7.0); declare `limen` npm 0.9.1 in `conditor.json`; re-run ui-conformance and review visual changes; check Fides.Arca with Arca 0.7; Ordo structural scan ignores `bin/` (ARC-02); remove unused pins (ARC-04) | C0 | build, 634+ tests, 138+ browser tests, `conditor verify`, `ordo verify`, Praxis validate | none, unless Forma visual changes need a design call |
| **C2** Dokimos (MOD-DOK-001) | `dokimos init`; policy; workflow; evidence branch | C1 (Praxis ≥ 3.17 attested completion) | Dokimos gate green | **Yes:** the evidence branch needs `contents: write` on the job; also decide whether to adopt Tutela and Percepta |
| **C3** integrity and correctness (WI-0101, part 1) | SEC-01 (verify record hash on read, canonical-tamper test), ARP-005 (refuse import into a complete group), ACR-008/AUT-004 (reachability of required content), SEC-04; mutation tests for SIGNAL-TEST-001 | C0 (independent of C1) | each group re-verified independently | — |
| **C4** wire library-only capabilities (WI-0101, part 2) | ADM-041 and ADM-054 (impact preview and dependency graph in production paths), SCS-006, SCS-007 and SCS-015 (template-selectable scorers and benchmarks), ARX-002 (derive capabilities from state systems or retire the parallel models through a DF), ARX-013 (section banks), SCS-013 (allocation running total) | C3 | re-verification | scope narrowing only through a DF record with owner acceptance |
| **C5** accessibility, print and field traceability | ADM-017 and SCS-014 negative tests; FOL-04 print-media regression; field-level traceability section in the coverage matrix generated from Limen binding inventories | C1 (Forma 0.13, Folio 0.4.1) | ui-conformance + new tests | — |
| **C6** shared-platform work | Forma data-mark pattern (FRM-02); Folio print region (FOL-03). Filed and implemented **in Forma and Folio**, released, then consumed. | C1 | release in the owning repository; Conditor install | owner to authorize cross-repository work |
| **C7** production readiness (MOD-OPS-001/002, MOD-SEC-001) | production deployment configuration; real-provider journeys; fault collection; header-capable hosting | C3 (SEC-01) | APPLICATION-READINESS gates pass in a deployed environment | **Yes:** GitHub App, Fides exchange, data repository, hosting decision |
| **C8** remaining missing groups | ARX-005, ARX-010, ADM-028/029/034/053/074..076 (migration, backup, restore), ADM-036..040/042/043/059/068, SRPP remainders, SIGNAL-DIST-001 | C3, C7 | per-group independent verification | distribution profile (issue #7) |
| **Owner decisions** | SIGNAL-VIS-001.1 theme and brand; C# shim exception approval (ARCHITECTURE-REVIEW); Tutela and Percepta adoption; accept MOD-* into Praxis | — | recorded DF | **Yes** |

Parallelism: C3 can run beside C1. C4 and C5 wait on their predecessors. C6 needs authorization in the platform repositories, and C7 needs owner resources.

CI efficiency: the coverage drift check runs before the build. Targeted test filters are used during development, and the full suite runs before merge. The browser suite (about 6.5 minutes) runs once per PR.
