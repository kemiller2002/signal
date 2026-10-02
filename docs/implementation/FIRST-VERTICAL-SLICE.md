# First Vertical Slice: Publish, Respond, Score

Work item: GH-5

## Goal

Prove the complete state direction for the smallest useful Signal workflow before implementing the larger survey-engine surface.

## Flow

1. Start an empty Signal state.
2. Create one draft containing explicit questions.
3. Publish version 1.
4. Reject response submission before publication.
5. Accept a response only when survey identity, version, answer set, and value bounds are valid.
6. Reject duplicate response identity.
7. Score an accepted response through an explicit command.
8. Project the score as an administrator result.

## Non-goals

This slice does not claim the production scoring engine is simple summation. The current calculation is deliberately small test evidence for architecture and transition direction. Production scoring requirements remain authoritative.

This slice does not implement persistence, GitHub effects, report building, assessment batteries, branching, item banking, expression-tree scoring, Aegis effects, or administration.

## Expansion order

After this slice is green:
1. published template/version storage;
2. GitHub-backed persistence through explicit effects;
3. typed selector/response contracts;
4. production scoring families;
5. result persistence and admin projections;
6. authoring/publication workflow;
7. analytics/reporting and visualization;
8. assessment batteries and advanced orchestration.

Each expansion remains ROS-tracked and must use Forma rather than introducing app-local visual primitives where Forma already has a contract.
