/// Selector-to-scorer compatibility (SCS-015): publication refuses a scorer
/// that does not fit the answer semantics it consumes, with a stable reason
/// code, instead of producing a number that means nothing. Answers without a
/// single number (multi-choice, ranking, allocation, best-worst, ranges) are
/// scored only through a declared item key whose kind fits the primitive.
module Echelon.Signal.Engine.Compatibility

open Echelon.Signal.Engine.Scoring
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

/// The range of numbers an unkeyed question can contribute.
let valueRange (q: Question) : (float * float) option =
    match q.Answer with
    | Boolean -> Some(0.0, 1.0)
    | Ordinal points -> Some(0.0, float (points - 1))
    | BoundedNumber b -> Some(b.Minimum, b.Maximum)
    | SingleChoice _
    | Hierarchical _ ->
        match options q.Answer |> List.choose _.Score with
        | [] -> None
        | scores -> Some(List.min scores, List.max scores)
    | _ -> None

/// Whether a primitive yields one number without a key.
let hasSingleNumber =
    function
    | Boolean
    | Ordinal _
    | SingleChoice _
    | BoundedNumber _
    | Hierarchical _ -> true
    | _ -> false

let keyFits (key: KeyKind) (answer: AnswerDefinition) =
    let hasOption id = options answer |> List.exists (fun o -> o.Id = id)

    match key, answer with
    | SingleKeyed _, (SingleChoice _ | Hierarchical _)
    | MultiKeyed _, (MultiChoice _ | HierarchicalMulti _)
    | AllocationKeyed _, Allocation _
    | RangeKeyed _, BoundedRange _ -> true
    | RankKeyed(item, _), Ranking _
    | BestWorstKeyed(item, _), BestWorst _ -> hasOption item
    | _ -> false

/// Why a scorer cannot read a question (with its key, if any).
let problem (scorer: Scorer) (q: Question) (key: KeyKind option) : string option =
    let ordinal =
        match q.Answer with
        | Ordinal points -> Some points
        | Boolean -> Some 2
        | _ -> None

    match key with
    | Some k when not (keyFits k q.Answer) -> Some "its item key does not fit the answer primitive"
    | None when not (hasSingleNumber q.Answer) -> Some "the answer has no single number; declare an item key"
    | Some _ -> None
    | None ->
        match scorer.Aggregate, scorer.Scale with
        | NetPromoterScore, _ when not (q.Answer = Ordinal 11 && (q.Selector.Preset = NumericRating || q.Selector.Preset = Nps0To10)) ->
            Some "standard NPS reads only a 0-10 numeric rating"
        | (TopKBox(_, m) | BottomKBox(_, m) | WeightedTopKBox(_, m)), Direct when ordinal <> Some(m + 1) ->
            Some $"a box score over 0..{m} needs an ordinal scale of {m + 1} points"
        | _, Reverse(lo, hi) when (match ordinal with Some n -> (lo, hi) <> (0.0, float (n - 1)) | None -> valueRange q <> Some(lo, hi)) ->
            Some "reverse scoring needs the question's own declared scale bounds"
        | PercentageOfMaximum m, Direct when valueRange q |> Option.exists (fun (_, hi) -> hi > m) ->
            Some $"the item maximum {m} is below what the question can store"
        | PercentageOfRange(lo, hi), Direct when valueRange q |> Option.exists (fun (a, b) -> a < lo || b > hi) ->
            Some $"the range {lo}..{hi} does not cover the question's values"
        | _ -> None

let check (content: Content) : Finding list =
    let block = finding ScoringCategory Blocker
    let keys = content.Results.ItemKeys
    let keyOf (q: Question) = keys |> List.tryFind (fun k -> k.Question = q.Id) |> Option.map _.Key

    [ for k in keys do
          if (tryQuestion content k.Question).IsNone then
              block "KEY-REFERENCE" k.Question $"An item key names unknown question '{k.Question}'."
      for id in duplicates (keys |> List.map _.Question) do
          block "KEY-DUPLICATE" id $"Question '{id}' has more than one item key."
      for s in content.Sections do
          match s.Scoring with
          | None -> ()
          | Some scoring ->
              for q in scoredQuestions s do
                  match problem scoring.Scorer q (keyOf q) with
                  | Some reason -> block "SCORING-INCOMPATIBLE" q.Id $"Section '{s.Id}' cannot score '{q.Id}': {reason}."
                  | None -> () ]
