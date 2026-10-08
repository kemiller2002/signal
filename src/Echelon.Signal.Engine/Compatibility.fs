/// Selector-to-scorer compatibility (SCS-015): publication refuses a scorer
/// that does not fit the answer semantics it consumes, with a stable reason
/// code, instead of producing a number that means nothing.
module Echelon.Signal.Engine.Compatibility

open Echelon.Signal.Engine.Scoring
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

/// The range of numbers a question can contribute, before the item scale.
let valueRange (q: Question) : (float * float) option =
    match q.Answer with
    | Boolean -> Some(0.0, 1.0)
    | Ordinal points -> Some(0.0, float (points - 1))
    | SingleChoice options ->
        match options |> List.choose _.Score with
        | [] -> None
        | scores -> Some(List.min scores, List.max scores)

/// Why a scorer cannot read a question, if it cannot.
let problem (scorer: Scorer) (q: Question) : string option =
    let ordinal =
        match q.Answer with
        | Ordinal points -> Some points
        | Boolean -> Some 2
        | SingleChoice _ -> None

    match scorer.Aggregate, scorer.Scale with
    | NetPromoterScore, _ when not (q.Answer = Ordinal 11 && q.Selector.Preset = NumericRating) ->
        Some "standard NPS reads only a 0-10 numeric rating"
    | (TopKBox(_, m) | BottomKBox(_, m) | WeightedTopKBox(_, m)), Direct when ordinal <> Some(m + 1) ->
        Some $"a box score over 0..{m} needs an ordinal scale of {m + 1} points"
    | _, Reverse(lo, hi) when (match ordinal with Some n -> (lo, hi) <> (0.0, float (n - 1)) | None -> true) ->
        Some "reverse scoring needs the question's own declared scale bounds"
    | PercentageOfMaximum m, Direct when valueRange q |> Option.exists (fun (_, hi) -> hi > m) ->
        Some $"the item maximum {m} is below what the question can store"
    | PercentageOfRange(lo, hi), Direct when valueRange q |> Option.exists (fun (a, b) -> a < lo || b > hi) ->
        Some $"the range {lo}..{hi} does not cover the question's values"
    | _ -> None

let check (content: Content) : Finding list =
    [ for s in content.Sections do
          match s.Scoring with
          | None -> ()
          | Some scoring ->
              for q in scoredQuestions s do
                  match problem scoring.Scorer q with
                  | Some reason ->
                      finding ScoringCategory Blocker "SCORING-INCOMPATIBLE" q.Id $"Section '{s.Id}' cannot score '{q.Id}': {reason}."
                  | None -> () ]
