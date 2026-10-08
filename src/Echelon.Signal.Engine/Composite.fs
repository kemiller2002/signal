/// The overall score as an explicit composite of section results (VER-005,
/// ALG-002, ALG-003; DF-SIGNAL-2026-0002 Q5 and Q6).
///
/// Sections are scored independently first. A section that is not scored or
/// not applicable is excluded from the composite with its reason, never
/// counted as zero, and declared weights are renormalized over the sections
/// that were scored. Weakest-link and minimum-domain rules exist so an
/// average cannot hide a critical deficiency; they need a declared
/// direction.
module Echelon.Signal.Engine.Composite

open System
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

type Trace =
    { /// Section, value and the normalized weight it carried.
      Included: (string * float * float) list
      /// Section and why it was left out.
      Excluded: (string * string) list
      /// The composite before any cap and before rounding.
      Raw: float option
      /// True when a minimum-domain rule capped the result.
      Capped: bool }

let private finite v = not (Double.IsNaN v || Double.IsInfinity v)

let compose (spec: CompositeSpec) (sections: (string * Rules.SectionResult) list) : Scoring.Outcome * Trace =
    let chosen =
        if spec.Sections.IsEmpty then
            sections
        else
            spec.Sections
            |> List.map (fun id ->
                match sections |> List.tryFind (fun (s, _) -> s = id) with
                | Some found -> found
                | None -> id, Rules.SectionNotApplicable)

    let included, excluded =
        chosen
        |> List.fold
            (fun (inc, exc) (id, result) ->
                match result with
                | Rules.SectionScored(Scoring.Score(v, _, _)) -> inc @ [ id, v ], exc
                | Rules.SectionScored(Scoring.NotScored reason) -> inc, exc @ [ id, $"not scored: %A{reason}" ]
                | Rules.SectionNotApplicable -> inc, exc @ [ id, "not applicable" ])
            ([], [])

    let weightOf id =
        match spec.Method with
        | WeightedMean ws -> ws |> List.tryFind (fun (s, _) -> s = id) |> Option.map snd |> Option.defaultValue 0.0
        | _ -> 1.0

    let total = included |> List.sumBy (fst >> weightOf)

    let normalized =
        included |> List.map (fun (id, v) -> id, v, (if total > 0.0 then weightOf id / total else 0.0))

    let trace =
        { Included = normalized
          Excluded = excluded
          Raw = None
          Capped = false }

    let values = included |> List.map snd
    let balanced () = List.average values

    let raw: Result<float * bool, string> =
        if included.Length < max 1 spec.MinimumScoredSections then
            Error "insufficient"
        else
            match spec.Method, spec.Direction with
            | BalancedMean, _ -> Ok(balanced (), false)
            | WeightedMean _, _ when total <= 0.0 -> Error "the weights of the scored sections sum to zero"
            | WeightedMean _, _ -> Ok(normalized |> List.sumBy (fun (_, v, w) -> v * w), false)
            | WeakestLink, HigherIsBetter -> Ok(List.min values, false)
            | WeakestLink, HigherIsWorse -> Ok(List.max values, false)
            | WeakestLink, Neutral -> Error "a weakest link needs a direction"
            | (GeometricMean | HarmonicMean), _ when values |> List.exists (fun v -> v <= 0.0) ->
                Error "geometric and harmonic means are defined only for positive values"
            | GeometricMean, _ -> Ok(exp (values |> List.averageBy log), false)
            | HarmonicMean, _ -> Ok(float values.Length / (values |> List.sumBy (fun v -> 1.0 / v)), false)
            | MinimumDomain(threshold, cap), HigherIsBetter ->
                let mean = balanced ()
                if values |> List.exists (fun v -> v < threshold) && mean > cap then Ok(cap, true) else Ok(mean, false)
            | MinimumDomain(threshold, floor), HigherIsWorse ->
                let mean = balanced ()
                if values |> List.exists (fun v -> v > threshold) && mean < floor then Ok(floor, true) else Ok(mean, false)
            | MinimumDomain _, Neutral -> Error "a minimum-domain rule needs a direction"

    match raw with
    | Error "insufficient" ->
        Scoring.NotScored(Scoring.InsufficientObservations(included.Length, max 1 spec.MinimumScoredSections)), trace
    | Error reason -> Scoring.NotScored(Scoring.Undefined reason), trace
    | Ok(value, capped) when finite value ->
        Scoring.Score(Math.Round(value, spec.Decimals, MidpointRounding.AwayFromZero), included.Length, excluded.Length),
        { trace with Raw = Some value; Capped = capped }
    | Ok _ -> Scoring.NotScored(Scoring.Undefined "the result is not a finite number"), trace

/// Publication checks for a composite.
let check (content: Content) (spec: CompositeSpec) : Finding list =
    let block = finding ScoringCategory Blocker
    let scored = content.Sections |> List.filter (fun s -> s.Scoring.IsSome) |> List.map _.Id |> Set.ofList

    let named =
        spec.Sections
        @ (match spec.Method with
           | WeightedMean ws -> ws |> List.map fst
           | _ -> [])

    [ for id in List.distinct named do
          if not (scored.Contains id) then
              block "COMPOSITE-REFERENCE" id $"The overall composite names '{id}', which is not a scored section."
      if scored.IsEmpty then
          block "COMPOSITE-NO-SECTIONS" "" "The overall composite has no scored sections to compose."
      if spec.MinimumScoredSections < 1 then
          block "COMPOSITE-MINIMUM" "" "The minimum number of scored sections must be at least 1."
      if spec.Decimals < 0 || spec.Decimals > 10 then
          block "COMPOSITE-DECIMALS" "" "Decimals must be between 0 and 10."
      match spec.Method with
      | WeightedMean ws ->
          if ws |> List.exists (fun (_, w) -> w < 0.0 || not (finite w)) then
              block "COMPOSITE-WEIGHTS" "" "Section weights must be finite and not negative."
          for id, n in ws |> List.countBy fst do
              if n > 1 then block "COMPOSITE-WEIGHTS" id $"Section '{id}' is weighted more than once."
          let composed = if spec.Sections.IsEmpty then Set.toList scored else spec.Sections
          for id in composed do
              if not (ws |> List.exists (fun (s, _) -> s = id)) then
                  block "COMPOSITE-WEIGHTS" id $"Composed section '{id}' has no weight."
      | WeakestLink
      | MinimumDomain _ when spec.Direction = Neutral ->
          block "COMPOSITE-DIRECTION" "" "Weakest-link and minimum-domain composites need a direction."
      | _ -> () ]
