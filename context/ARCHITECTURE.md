# Echelon Signal architecture

## Current architecture

The first slice (a respondent completing the first three SDRA dimensions and
printing the result report) is built the same way as the other Echelon
applications; see `DF-SIGNAL-FND-2026-0002`:

| Tier | Path | Role |
|---|---|---|
| Limen engine (pure) | `src/Echelon.Signal.Engine` | Assessment, scoring, session transitions, view projection. |
| Limen engine (administrator domain) | `src/Echelon.Signal.Admin` | Deployment configuration, storage profiles, Signal's Arca namespace and storage manifest (pure, over Arca.Core). |
| Limen engine (application) | `src/Echelon.Signal.Application` | Limen protocol, handshake, Aegis boundary. |
| Limen kernel (WASM shim) | `src/Echelon.Signal.Browser` | One `[JSExport]`; no decisions. |
| Limen kernel (browser) | `web-kernel/`, `web/` | `BrowserKernel` start-up; Forma markup and Folio print surface. |

The repository currently separates:

- governance and operating contracts;
- compact current context;
- research evidence, hypotheses, experiments, and packages;
- architecture and decision documentation;
- generated registries and derived outputs;
- implementation and tests, once the first slice is selected.

## Required first decision

After selecting the first vertical slice, record the smallest architecture that
can deliver it and the boundaries that would be costly to reverse.

## Architectural constraints

- Canonical records must remain independent of any model vendor or chat.
- Secrets and sensitive communication content must not enter fixtures, logs, or
  prompts.
- Generated views must not silently replace canonical source records.
