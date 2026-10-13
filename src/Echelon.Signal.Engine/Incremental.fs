/// Incremental aggregation (ADM-011): the group result kept up to date one
/// accepted contribution at a time, without rescanning the history.
///
/// The accumulator holds the evidence each statistic needs: per dimension,
/// the scores in ascending order (for the median and the extremes) and the
/// count of respondents who did not score; the answer counts; and the
/// accepted SubmissionHashes by identity. Adding a contribution inserts into
/// these; the result is assembled by the same function the full aggregation
/// uses (`Aggregation.assemble`), so
///
///   Incremental.add (Incremental.ofResults rs) r = Incremental.ofResults (r :: rs)
///   Incremental.result (Incremental.ofResults rs) = Aggregation.aggregate (all of rs)
///
/// hold by construction and are checked by tests over random sequences.
/// Adding the same contribution again changes nothing; a different
/// contribution for an accepted identity is refused, never merged.
///
/// Pure.
module Echelon.Signal.Engine.Incremental

open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Aggregation

/// One dimension's evidence.
type DimensionEvidence =
    { /// Scores in ascending order.
      Scores: float list
      Unscored: int }

/// What the group's accepted contributions add up to.
[<NoComparison>]
type Accumulator =
    { Definition: GroupDefinition
      /// SubmissionHash by identity key.
      Accepted: Map<string, string>
      Evidence: Map<string, DimensionEvidence>
      Answered: int
      NonNumeric: int
      /// How many accepted results triggered each recommendation, and held each yes/no fact.
      Recommendations: Map<string, int>
      Facts: Map<string, int>
      /// Accepted results submitted after an enforced timer ran out (DF-SIGNAL-2026-0015).
      Late: int }

let empty (definition: GroupDefinition) =
    { Definition = definition
      Accepted = Map.empty
      Evidence =
        definition.Template.Dimensions
        |> List.map (fun d -> d.Id, { Scores = []; Unscored = 0 })
        |> Map.ofList
      Answered = 0
      NonNumeric = 0
      Recommendations = Map.empty
      Facts = Map.empty
      Late = 0 }

/// Inserts into an ascending list, without recursion (a group may be large).
let private insert (value: float) (sorted: float list) =
    let smaller = sorted |> List.takeWhile (fun existing -> existing < value)
    smaller @ (value :: List.skip smaller.Length sorted)

/// Why a contribution cannot be added.
type AddRefusal =
    /// The identity already contributed a different submission.
    | DifferentSubmission of identityKey: string * accepted: string

/// Adds one accepted contribution, updating only the evidence it touches.
/// Counts each distinct id once more.
let private counted (counts: Map<string, int>) (ids: string list) =
    ids |> List.distinct |> List.fold (fun (m: Map<string, int>) id -> m.Add(id, 1 + (m.TryFind id |> Option.defaultValue 0))) counts

let add (result: SurveyResult) (accumulator: Accumulator) : Result<Accumulator, AddRefusal> =
    match accumulator.Accepted.TryFind result.Identity.Key with
    | Some hash when hash = result.SubmissionHash -> Ok accumulator
    | Some hash -> Error(DifferentSubmission(result.Identity.Key, hash))
    | None ->
        let evidence =
            accumulator.Definition.Template.Dimensions
            |> List.fold
                (fun (evidence: Map<string, DimensionEvidence>) dimension ->
                    let current = evidence[dimension.Id]

                    let next =
                        match scoreOn dimension result with
                        | Some score -> { current with Scores = insert score current.Scores }
                        | None -> { current with Unscored = current.Unscored + 1 }

                    evidence.Add(dimension.Id, next))
                accumulator.Evidence

        Ok
            { accumulator with
                Accepted = accumulator.Accepted.Add(result.Identity.Key, result.SubmissionHash)
                Evidence = evidence
                Answered = accumulator.Answered + result.AnsweredCount
                NonNumeric = accumulator.NonNumeric + result.NonNumericCount
                Recommendations = counted accumulator.Recommendations result.Recommendations
                Facts = counted accumulator.Facts result.FactsTrue
                Late = accumulator.Late + (if result.LateTimers.IsEmpty then 0 else 1) }

/// The accumulator for a set of accepted results, added one by one.
let ofResults (definition: GroupDefinition) (results: SurveyResult list) =
    results
    |> List.fold (fun state result -> state |> Result.bind (add result)) (Ok(empty definition))

/// The group result the accumulator stands for.
let result (policy: Policy) (accumulator: Accumulator) : SurveyGroupResult =
    assemble
        policy
        accumulator.Definition
        accumulator.Accepted.Count
        (fun dimension ->
            let evidence = accumulator.Evidence[dimension.Id]
            statisticsOf evidence.Scores evidence.Unscored)
        accumulator.Answered
        accumulator.NonNumeric
        (accumulator.Accepted |> Map.toList |> List.map snd)

/// The SubmissionHash accepted for an identity key, for import evaluation.
let acceptedFor (accumulator: Accumulator) (identityKey: string) = accumulator.Accepted.TryFind identityKey
