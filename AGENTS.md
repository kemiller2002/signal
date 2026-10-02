---
id: GV-START-001
title: Agent Startup Guide
status: canonical
version: 1.0.0
owners:
  - repository-governance
created: 2026-07-22
updated: 2026-07-22
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - docs/00-governance/README.md
tags: [governance, agents, startup]
---

# Agent Startup Guide

## Mission

The Repository Operating System (ROS) makes research, engineering, decisions, and handoffs durable without relying on conversation history or tribal knowledge.

## Start Here

1. Read [the governance index](docs/00-governance/README.md).
2. Identify the task's scope and operating mode.
3. Locate the applicable canonical domain records; inspect the repository and user changes before editing.
4. State or record material unknowns, constraints, assumptions, and risks.
5. Use the smallest process that preserves correctness, traceability, and continuity.
6. Execute, validate, update affected records, and leave a handoff.

Detailed rules are in the [Agent Operating Manual](docs/00-governance/Agent-Operating-Manual.md). Research packages follow the [REP Specification](docs/00-governance/Research-Execution-Package-Specification.md); engineering follows the [Engineering Standards](docs/00-governance/Engineering-Standards.md).

## Authority

Apply, in descending order: explicit user instruction; applicable safety, legal, and platform constraints; canonical governance; accepted domain REPs and theory; accepted architecture and decision records; current implementation; local convention; agent preference. A higher authority cannot authorize a violation of an applicable safety or legal constraint. When same-level sources conflict, prefer the narrower and newer accepted record and document the resolution; escalate if the outcome materially changes the authorized goal.

## Core Rules

- Never fabricate evidence, file reads, approvals, commands, test results, or certainty.
- Preserve user work. Inspect before modifying; do not destroy or irreversibly migrate without authorization.
- Make reasonable, reversible, in-scope decisions. Escalate high-impact irreversible, security/privacy-sensitive, legally ambiguous, or materially out-of-scope decisions.
- Research by testing hypotheses against confirming and falsifying evidence. Engineering by establishing a baseline, defining acceptance criteria, making the smallest robust change, and testing in proportion to risk.
- Important claims cite `EV-`, `HY-`, and `TH-` records when those records exist. Material decisions use `DF-`, which canonically means **Decision Record**.
- Do not silently change canonical policy. Propose or record the change, its evidence, consequences, version, and migration path.
- Do not claim a test passed unless it ran and passed. Name skipped or unavailable checks and their implications.
- Not every edit needs a REP. Use the artifact threshold in the Agent Operating Manual.

## Handoff

For substantial work, record: objective; work completed; files changed; decisions and assumptions; tests run and results; evidence added; unresolved questions; risks; and next recommended action. A capable successor must be able to continue without the originating conversation.

## Work Protocol

Before meaningful mutation, identify the external work item and run `./ros work begin ID`. Inspect `./ros work context ID` for allowed actions and required evidence, perform the bounded work, gather configured evidence, request a legal transition with `work complete`, then run `./ros registry build` and `./ros validate`. Use `work block --reason` and `work resume` rather than hand-editing context. Use `./ros status` when resuming unfamiliar work. Meaningful committed changes require machine-readable attribution; see `docs/work-protocol.md`.

No externally-assigned ID yet? Check `./ros work ready` for capturable, unblocked repository work before assuming none exists, and use `./ros add "..."` to record a newly discovered obligation instead of leaving it as an unfiled comment or dropped observation. `./ros work start ID` promotes a ready backlog item into the protocol above. This local backlog is repository-scoped triage, not a project-management system; see the "Local backlog" section of `docs/work-protocol.md`.

<!-- BEGIN echelon:visual-engineering -->
## Visual Engineering UI research

Managed by `npx @echelon-foundry/visual-engineering`. Do not edit inside this block.

Before designing, implementing, or reviewing UI:

1. Run `npx @echelon-foundry/visual-engineering verify` and stop if it reports a failure.
2. Read `.visual-engineering/AGENT-INSTRUCTIONS.md`.
3. Read `.visual-engineering/UI-FOUNDATIONS.md`.
4. Read `.visual-engineering/UI-DECISION-CHECKLIST.md`.
5. Read `.visual-engineering/UI-ANTI-PATTERNS.md`.
6. Consult `.visual-engineering/RESEARCH-INDEX.md` for provenance and deeper evidence.
7. Inspect the product and its existing design system.
8. Apply the research as decision criteria, not as a visual style.
9. Report the context version, source commit, principles applied, verification
   performed, and justified deviations.

Do not copy Visual Engineering research into this repository by hand.
<!-- END echelon:visual-engineering -->

<!-- echelon:communication-engineering:start -->
## Communication Engineering

Communication Engineering is installed as evidence-bounded operational guidance.
Before producing consequential communication, read:

- `.communication-engineering/COMMUNICATION-FOUNDATIONS.md`
- `.communication-engineering/COMMUNICATION-DECISION-CHECKLIST.md`
- `.communication-engineering/PURPOSE-OUTCOME-MATRIX.md`
- `.communication-engineering/COMMUNICATION-ANTI-PATTERNS.md`
- `.communication-engineering/RESEARCH-STATUS.md`

Treat research maturity as a constraint. Do not turn provisional findings into universal rules, optimize persuasion at the expense of user autonomy, or substitute style for proof obligations.
<!-- echelon:communication-engineering:end -->

## Aegis operational failure handling

Echelon Signal requires Aegis. Implementation work MUST import and actively use `EchelonFoundry.Aegis.Core` at architectural operational-failure boundaries; merely referencing the package is not sufficient.

Because phase-1 administrator storage uses GitHub, use `EchelonFoundry.Aegis.Integration.GitHub` for GitHub failure translation where applicable. If Aegis lifecycle/fault events are persisted in GitHub, use `EchelonFoundry.Aegis.Store.GitHub` rather than creating a Signal-specific event format.

Before implementing fault handling, read the current Aegis consumer guidance in `kemiller2002/aegis`, especially `docs/aegis/AGENT-INTEGRATION.md`.

Apply these boundaries:

- Aegis handles unexpected operational failures at network, storage, registry, file/browser interop, backup/restore, migration, and future service boundaries.
- Signal/Ordo typed domain outcomes remain domain outcomes; do not route validation, duplicate, privacy, comparability, sealed-state, capability, or legal-transition results through Aegis.
- Aegis recovery may propose a transition; Ordo/SDE authorizes it.
- Unknown writes remain `OutcomeUnknown` and require reconciliation; Aegis must not turn them into blind retries.
- Limen renders Aegis presentation intent; it does not create a parallel operational-fault taxonomy.
- Fault context/events/sinks must contain no credentials, PII, respondent URL answer state, or suppressed data.
- Programming defects fail loudly under Aegis policy.
- Tests must prove declared boundaries actually use Aegis and use replaceable/collector sinks for deterministic assertions.

The authoritative repository-wide requirement is ARX-015. The administrator-specific requirement is ADM-077.

## CI observation discipline

Commit and push incremental recovery points. Keep working after pushes when independent work remains. Do not wait for remote CI after every push. Check remote CI at the final implementation boundary by default, or earlier only when the result is needed to proceed safely. Ordinary commit-driven CI should wait for a 10-minute quiet period so nearby commits batch together.
