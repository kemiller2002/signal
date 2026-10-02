namespace Echelon.Signal.Host.Wasm

open Echelon.Signal.Semantic
open Echelon.Signal.Engine

/// Browser-boundary vocabulary only. The host maps semantic events to closed
/// engine commands; it does not decide survey legality, scoring, or permissions.
type BrowserEvent =
    | PublishRequested
    | ResponseSubmitted of Submission
    | ScoreRequested of ResponseId

module Boundary =
    let toCommand = function
        | PublishRequested -> Publish
        | ResponseSubmitted submission -> Submit submission
        | ScoreRequested responseId -> ScoreResponse responseId
