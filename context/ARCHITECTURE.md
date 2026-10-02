# Echelon Signal architecture

## Accepted baseline

Signal uses the SDE/Ordo four-tier dependency direction:

```text
Tier 4  Echelon.Signal.Host.Wasm
            |
            v
Tier 3  Echelon.Signal.Application
            |
            v
Tier 2  Echelon.Signal.Engine
            |
            v
Tier 1  Echelon.Signal.Semantic
```

Dependencies point downward only.

### Tier 1: Semantic

Owns durable domain vocabulary and identities. It has no browser, storage, GitHub, Limen, Forma, or provider dependency.

### Tier 2: Engine

Owns legal transitions and invariants. The first slice proves that publishing without a draft is illegal, publication creates a versioned immutable survey representation, responses must target the published identity/version, duplicate responses are rejected, answer sets are validated, and scoring is a separate legal command.

### Tier 3: Application

Orchestrates use cases and projections. It does not perform browser I/O directly.

### Tier 4: Host / browser boundary

Maps open browser events to the engine's closed command vocabulary. It may request or receive effects through Limen but cannot reinterpret domain legality.

## UI baseline

Forma is the canonical UI vocabulary. Applications consume a pinned Forma release. Do not copy Forma CSS into Signal.

The browser boundary follows Limen:
- HTML owns semantic structure;
- Forma CSS owns presentation and responsive behavior;
- Limen owns browser mechanics/effects;
- F# owns state, decisions, projections, and domain meaning.

## Operational failures

Aegis is used at network, storage, migration, registry, and browser-interop operational-failure boundaries. Typed Signal/Ordo outcomes remain domain outcomes rather than Aegis faults.

## First vertical slice

See `docs/implementation/FIRST-VERTICAL-SLICE.md`.
