# Echelon Signal current state

## Repository status

Signal has an accepted implementation baseline for the first product slice.

Current governing stack:
- ROS 3.1.4
- SDE / Ordo 1.3.0
- Limen 0.6.2
- Visual Engineering 1.0.0
- Communication Engineering 1.0.0
- Forma 0.1.0 application-consumption baseline

The repository now contains a four-tier F# skeleton and executable first-slice test.

## Accepted first vertical slice

The first implementation slice is:

1. create one survey draft;
2. publish an immutable version;
3. accept one anonymous response;
4. score the accepted response deterministically;
5. project the scored result for an administrator.

This slice is intentionally smaller than the full Signal requirements. It proves the state and dependency direction before broader scoring, banking, analytics, administration, and visualization work.

## Architectural facts

- Tier 1: `Echelon.Signal.Semantic`
- Tier 2: `Echelon.Signal.Engine`
- Tier 3: `Echelon.Signal.Application`
- Tier 4 boundary: `Echelon.Signal.Host.Wasm`
- Browser behavior beyond native HTML belongs to Limen.
- Forma owns presentation only.
- Survey legality, versioning, response acceptance, scoring semantics, capabilities, and obligations remain F#/Ordo application state.
- Aegis is required at operational-failure boundaries as implementation reaches storage/network effects.

## Next work

Expand from the first vertical slice through ROS work items. Do not replace the skeleton with a framework-specific architecture or move domain rules into browser code.
