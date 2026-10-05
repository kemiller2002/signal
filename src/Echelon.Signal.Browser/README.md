# Echelon Signal Browser Boundary

This directory is the thin browser-facing side of the Limen boundary: the
.NET WebAssembly shim (`Echelon.Signal.Browser.csproj`) whose single
`[JSExport]`, `SignalWasm.Dispatch`, forwards each Limen message, as JSON, to
the F# engine and returns its reply.

Code here may perform mechanical loading, serialization, transport wiring, and
browser integration through Limen. It must not own or re-derive Signal domain
decisions, scoring, validation, privacy rules, capabilities, or application state.

The generic browser kernel itself is supplied by Limen
(`@echelon-foundry/limen`); the page that starts it is `web/`.
