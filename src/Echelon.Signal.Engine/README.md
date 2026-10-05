# Echelon Signal Engine Boundary

This directory owns application meaning for Echelon Signal.

Production domain state, legal transitions, scoring, validation, privacy rules,
capabilities, obligations, effects-as-data, and view projection belong on this
side of the Limen boundary. The production implementation is F#/.NET WebAssembly.

Browser APIs and DOM authority do not belong here.

| File | Contents |
|---|---|
| `View.fs` | The shape of Limen's view state. |
| `Assessment.fs` | Answers, the frequency scale and dimension scoring. Pure. |
| `Pilot.fs` | The pilot assessment (SDRA D01-D03), from the draft item bank. |
| `Session.fs` | The respondent session: transitions and view projection. Pure. |

The Limen protocol and the Aegis boundary live one tier out, in
`src/Echelon.Signal.Application`.
