/// Interpretation of results, kept separate from scoring (ETA §16, SCS-002
/// threshold/banding and pass/fail, ALG-002 maturity stages): changing a
/// band or a stage never changes the score it reads.
module Echelon.Signal.Engine.Interpretation

open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

type Outcome =
    { Id: string
      /// The label, or None when the target is not scored or a stage
      /// condition is still unknown.
      Label: string option }

let private valueOf (overall: Scoring.Outcome option) (sections: (string * Rules.SectionResult) list) (target: ResultTarget) =
    match target with
    | OverallResult ->
        match overall with
        | Some(Scoring.Score(v, _, _)) -> Some v
        | _ -> None
    | SectionResultOf id ->
        match sections |> List.tryFind (fun (s, _) -> s = id) with
        | Some(_, Rules.SectionScored(Scoring.Score(v, _, _))) -> Some v
        | _ -> None

/// Gated stages: each is reached only when its condition holds and every
/// earlier one was reached. An unknown condition leaves the stage unknown.
let private stage (env: Rules.Env) (baseline: string) (stages: Stage list) =
    let rec climb (reached: string) =
        function
        | [] -> Some reached
        | s :: rest ->
            match Rules.truth env s.Requires with
            | Some true -> climb s.Label rest
            | Some false -> Some reached
            | None -> None

    climb baseline stages

let interpret
    (env: Rules.Env)
    (overall: Scoring.Outcome option)
    (sections: (string * Rules.SectionResult) list)
    (interpretation: Interpretation)
    : Outcome =
    let value = valueOf overall sections interpretation.Target

    let label =
        match interpretation.Kind with
        | Bands bands -> value |> Option.bind (fun v -> Scoring.band bands v |> Result.toOption)
        | PassFail(threshold, HigherIsBetter) -> value |> Option.map (fun v -> if v >= threshold then "pass" else "fail")
        | PassFail(threshold, HigherIsWorse) -> value |> Option.map (fun v -> if v <= threshold then "pass" else "fail")
        | PassFail(_, Neutral) -> None
        | Stages(baseline, stages) -> stage env baseline stages

    { Id = interpretation.Id; Label = label }

let check (content: Content) (interpretations: Interpretation list) : Finding list =
    let block = finding ScoringCategory Blocker

    [ for id in duplicates (interpretations |> List.map _.Id) do
          block "INTERPRETATION-DUPLICATE" id $"Interpretation id '{id}' is used more than once."
      for i in interpretations do
          if not (isIdentifier i.Id) then
              block "INTERPRETATION-ID" i.Id $"Interpretation id '{i.Id}' is not a stable identifier."

          match i.Target with
          | OverallResult when content.Results.Overall.IsNone ->
              block "INTERPRETATION-TARGET" i.Id $"'{i.Id}' interprets the overall score, which the template does not define."
          | SectionResultOf s when not (content.Sections |> List.exists (fun x -> x.Id = s && x.Scoring.IsSome)) ->
              block "INTERPRETATION-TARGET" i.Id $"'{i.Id}' interprets '{s}', which is not a scored section."
          | _ -> ()

          match i.Kind with
          | Bands bands ->
              for problem in Scoring.validateBands bands do
                  block "INTERPRETATION-BANDS" i.Id $"'{i.Id}': {problem}."
          | PassFail(_, Neutral) -> block "INTERPRETATION-DIRECTION" i.Id $"Pass/fail '{i.Id}' needs a direction."
          | PassFail _ -> ()
          | Stages(_, stages) ->
              if stages.IsEmpty then
                  block "INTERPRETATION-STAGES" i.Id $"'{i.Id}' has no stages."
              for s in stages do
                  yield! RuleChecks.checkCondition content i.Id s.Requires ]
