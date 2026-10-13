# Signal measurement baseline: where the evidence lives

This file is an index. It says where Signal's historical delivery evidence
lives and when each Echelon capability arrived in this repository. It copies
no telemetry. It was written for the Echelon measurement amendment
(2026-10-13, item 4) under WI-0083. The program's metric definitions live in
kemiller2002/research-documents; until they are merged, this index records raw
sources only.

Facts here come from this repository's Git history, its `.ros/` records and
the GitHub API, read on 2026-10-13. Anything not recoverable from those is
marked **missing**, with the reason.

## Evidence sources

| Source | Where | What it holds | Range (as of 2026-10-13) |
|---|---|---|---|
| Praxis work items | `.ros/context/current.json` (live state), `.ros/work/queue.json` and `queue.md`, `.ros/work/items/` (early items only) | Work-item identity, state, evidence paths, completion conclusions; `./praxis work show WI-NNNN` renders one | WI-0001 … WI-0083 |
| Praxis events | `.ros/events/events.jsonl` | The append-only lifecycle event log (capture, start, checkpoint, complete) | 210 lines before WI-0083 |
| Praxis execution telemetry | `.ros/telemetry/executions/EXE-*.json`, `.ros/telemetry/metrics.json` | Per-execution identity, steps, status and metrics, as Praxis records them | 64 execution files, first `EXE-20260921T154547323Z-6410d99c`, latest `EXE-20261013T092834455Z-350013c4` |
| Praxis installation | `.ros/installation.json` | The repository operating system package and version installed | `@echelon-foundry/repository-operating-system` 1.2.1-main.16.1, installed 2026-08-31 |
| Praxis instance identity | `.praxis/instance.json` | The repository's Praxis instance id (`pxi-5fdaa929…`), created by Praxis 3.11.0 | From `da4e6ea` (2026-10-13) |
| Provenance | YAML front matter `provenance.contributions` in `docs/` and `research/` documents; `./praxis provenance show` and `audit` | Which execution created or modified a governed document, when, and why | Throughout `docs/requirements/` and `research/decisions/` |
| Decision records | `research/decisions/DF-SIGNAL-*.md`, indexed in `registries/decisions.json` | Design decisions with dates and status | DF-SIGNAL-2026-0001 … 0007 |
| Requirements status | `docs/requirements/implementation-gap-analysis.md` | Per-requirement status with the work items that moved it | — |
| Pull requests | GitHub `kemiller2002/signal` pull requests (REST `pulls?state=all`) | Titles, bodies (later PRs carry a Measurement section), head SHAs, merge SHAs and times | #1 (opened 2026-09-21) … #76 |
| CI history | GitHub Actions runs (REST `actions/runs`); workflows in `.github/workflows/` | Every push and pull_request run with conclusion and head SHA | 1,978 runs on 2026-10-13. Workflows: build, echelon-foundations, echelon-verification, limen-verify, pages, praxis-validation, readiness-work |
| Conditor state | `conditor.json`, `.conditor/authority/resolved-release-set.json`, `.conditor/lock.json`, `vendor/nuget/*.lock` | The selected component versions, the authority set and the installed artifacts' digests | From `31322ab` (2026-10-06) |

## Capability adoption

Commit hashes are this repository's. "Merged in" names the PR where the
history shows one.

| Capability | Version(s) | Adopted at | Notes |
|---|---|---|---|
| Repository operating system (Praxis's predecessor install) | 1.2.1-main.16.1 | `.ros/installation.json`, installed 2026-08-31; present in the earliest commit `637218a` | — |
| Limen (JavaScript kernel) | 0.7.0 → 0.7.1 → 0.9.0 | `057cb11` (0.7.0, PR #10, 2026-10-05); `178e37e` (0.7.1, WI-0026, 2026-10-06); `f53821c` (0.9.0, WI-0071, 2026-10-08) | `11dd989` (2026-10-05) built Signal on Limen, Forma, Aegis and Folio |
| Aegis | — | `11dd989` (2026-10-05) | `aegis-boundaries.json` |
| Forma (design system), Folio (print components) | Forma 0.4.1, Folio 0.3.0 | `11dd989` (2026-10-05); Forma 0.4.1 at `178e37e` (2026-10-06) | — |
| Praxis CLI | 3.7.1 → 3.7.2 → 3.11.0 | `f405989` (3.7.1, WI-0027, PR #13, 2026-10-06); `31322ab` (3.7.2, WI-0029, PR #15, 2026-10-06); `da4e6ea` (3.11.0, WI-0082, PR #76, merge `3a59526`, 2026-10-13) | — |
| Ordo | 1.4.0 → 1.4.1 → 1.4.2 → 1.5.0 | `f405989` (1.4.0, 2026-10-06); `c25b9bd` (1.4.1, WI-0028); `31322ab` (1.4.2); `437fc23` (1.5.0 via echelon-current 1.2.0, WI-0036, 2026-10-08) | Enforced in CI by the "Praxis Ordo Limen and repository verification" check |
| Conditor | — | `31322ab` (WI-0029, 2026-10-06) | Channel moves: echelon-current 1.2.0 (`437fc23`), 1.9.0 (`91968e2`), 1.12.0 (`609f80a`), 1.18.0 (`da4e6ea`) |
| Arca | 0.2.1 → 0.3.0 → 0.4.1 | `91968e2` (0.2.1, WI-0064, 2026-10-08); `609f80a` (0.3.0, WI-0072, 2026-10-08); `da4e6ea` (0.4.1, WI-0082, 2026-10-13) | Arca.Limen is pinned but unreferenced (DF-SIGNAL-2026-0001 decision 5) |
| Fides | 0.2.0 | `91968e2` (WI-0064, 2026-10-08) | — |
| limen-fsharp (Limen.Routing) | 0.9.0 | `f53821c` (WI-0071, 2026-10-08) | — |
| Visual Engineering | 1.0.1 | Pinned in `conditor.json` | **missing:** the adopting commit was not isolated for this index |
| Strict gated merge (every check on the exact head SHA concluded success; merge with `sha=`) | — | **missing** | An operating rule from the coordinating session, applied by a gate script outside the repository. The repository has no record of which merges used it. `main` has no branch protection (GitHub reports "Branch not protected"). PR bodies from #73 on list it among the capabilities used. |
| Ordo construction (ECIR, typed plans, `praxis construct`) | — | Not adopted | — |
| Dokimos | — | Not adopted in this repository | — |
| OS containment | — | Not adopted | — |

## What is missing

- **Token and cost usage**: not recorded anywhere in this repository. The
  agent runtimes that built Signal did not report provider usage to the
  agent. It is not estimated here.
- **Telemetry before 2026-09-21**: none. `.ros/telemetry/executions` starts
  at `EXE-20260921T154547323Z`.
- **Commit dates before 2026-09-30**: the earliest commit (`637218a`) is
  dated 2026-09-30, while PR #1 was opened 2026-09-21 and the installation
  record says 2026-08-31. The earlier history is not in this repository's
  current commit graph.
- **CI wait and repair times for early PRs**: not computed; derivable from
  the Actions runs and PR timestamps listed above.
- **Strict-gate adoption date**: see the table.
