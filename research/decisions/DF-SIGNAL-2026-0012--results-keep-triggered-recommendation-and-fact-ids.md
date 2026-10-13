---
id: DF-SIGNAL-2026-0012
title: Accepted results keep the ids of the recommendations they triggered and the yes/no facts that held; groups count them, withheld below an anonymous group's minimum
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
  - research/decisions/DF-SIGNAL-2026-0011--console-rule-authoring-uses-plain-conditions.md
  - research/decisions/DF-SIGNAL-2026-0004--report-state-holds-only-reportable-aggregates.md
  - docs/requirements/implementation-gap-analysis.md
tags: [results, recommendations, facts, aggregation, privacy, storage]
provenance:
  contributions:
    EXE-20261009T042446720Z-a28d9579:
      operations: [created]
      at: 2026-10-13T10:56:12.719Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Results keep triggered recommendation and fact ids (ARP-001, WI-0081)"
---

# DF-SIGNAL-2026-0012: Recommendations and facts in results

- **Date:** 2026-10-13
- **Status:** accepted. A routine design choice the Signal agent made under
  the coordinator's standing approval. It stays within the existing
  disclosure policy, so it involves no new privacy decision.
- **Work item:** WI-0081. **Requirements:** ARP-001 (§14 derived facts,
  §15 recommendations, §16 SurveyGroupResult, §25 recommendation frequency).

## Decision

1. **Ids, not text.** An accepted `SurveyResult` keeps the ids of the
   recommendations it triggered, most urgent first, and the ids of the
   yes/no facts that held. Titles, descriptions and priorities are resolved
   from the immutable template when a report is built (ARP §15). Number and
   category facts, and false facts, are not kept (ARP §14: only what group
   aggregation and reporting need).
2. **Stored additively.** The contribution record writes `recommendations`
   and `factsTrue` only when non-empty, so a result without them is stored
   byte for byte as before. Records written before this read as having none.
   A present but malformed list refuses the record. The record schema
   version stays 1: an older Signal would refuse only records that carry
   these fields, which it could not interpret anyway.
3. **Counted once per result.** The group result counts each id at most once
   per accepted result. The incremental accumulator, the stored-scores path
   and the full evaluation path give the same counts and the same hash. The
   group hash text gains `fact` lines only when there are facts, so a group
   without them hashes as before.
4. **Withheld with the scores.** Below an anonymous group's minimum
   reportable size, the recommendation and fact counts are withheld exactly
   when the score aggregates are, because a count of a fact can disclose an
   individual's answer.
5. **Group-level rules** (ARP §27, e.g. "more than 40% score below 50") are
   not added here.
