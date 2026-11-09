# Requirements completeness gate

Work item: WI-0100 · Date: 2026-11-08 · Matrix: [`requirements-coverage.json`](requirements-coverage.json) (generated, drift-checked in CI)

## Result

> **Requirements completeness = 76 / 204 = 37.3 %** → **NOT PASSED**

| | Value |
|---|---|
| Numerator: active requirements independently verified | **76** |
| Denominator: active in-scope requirements | **204** |
| Exclusions | 7 (listed below) |
| Authorized deferrals | **0**. None has been granted by the owner. |

## Rule

A requirement counts toward the numerator only when **all** of these hold:

1. The implementers claim it `tested` in `implementation-gap-analysis.md`.
2. Every test it names exists in source and passed. The baseline run passed all 627 tests, and all 101 named tests exist.
3. An independent verifier returned `VERIFIED` (`evidence/independent-verification.json`).

Unverified, partial, blocked and owner-decision items **stay in the denominator**. Nothing was deleted, narrowed or excluded to raise the figure.

## Exclusions (visible)

| ID | Reason |
|---|---|
| SCS-019, AER-003 | Scope statements (`n/a`): respected, not built |
| MOD-PLAT-001, MOD-DOK-001, MOD-OPS-001, MOD-OPS-002, MOD-SEC-001 | Proposed by this audit; they count once accepted through Praxis intake. Accepting them will **lower** the percentage, not raise it. |

## Breakdown

| Classification | Count |
|---|---:|
| Complete and verified | 76 |
| Implemented but unverified | 6 |
| Partially implemented | 101 |
| Not implemented | 19 |
| Blocked | 1 |
| Requiring owner decision | 1 |
| **Active total** | **204** |

Of the 103 active rows the implementers claimed `tested`, 76 were independently verified. Independent verification found 24 to be only partial; they are corrected in the gap analysis and planned under WI-0101. The remaining 3 are AER range rows that were not sampled.

## Reproduce

```bash
dotnet fsi tools/modernization/coverage.fsx           # regenerate
dotnet fsi tools/modernization/coverage.fsx --check   # CI drift gate
```
