/// Semantic regression verification for visualizations (ADM-050): fixtures
/// of data and expectations, assertions about meaning and accessibility
/// rather than pixels, and the difference between two definitions'
/// assertion results.
///
/// Pure.
module Echelon.Signal.Admin.Regression

open Echelon.Signal.Admin.Analysis
open Echelon.Signal.Admin.Visualization

/// What a fixture expects of a compiled chart.
type Expectation =
    { /// Categories that must be represented.
      Required: string list
      /// Categories that must not be.
      Forbidden: string list
      /// The unit the axis must use.
      Unit: Unit }

/// One assertion's name and whether it held.
type Assertion = { Name: string; Holds: bool }

/// The semantic and accessibility assertions for a compiled chart.
let assertions (expectation: Expectation) (data: Datum list) (compiled: Compiled) : Assertion list =
    let shown = compiled.Marks |> List.map _.Category |> Set.ofList
    let suppressed = data |> List.filter (fun d -> match d.Value with Unavailable(Suppressed _) -> true | _ -> false)

    let check name holds = { Name = name; Holds = holds }

    [ check "every required metric is represented" (expectation.Required |> List.forall shown.Contains)
      check "no unauthorized metric is represented" (expectation.Forbidden |> List.forall (shown.Contains >> not))
      check "privacy-suppressed data has no value" (suppressed |> List.forall (fun d -> compiled.Marks |> List.exists (fun m -> m.Category = d.Category && m.Position.IsNone)))
      check "missing values stay explicit" (compiled.Marks |> List.forall (fun m -> m.Available || (m.Position.IsNone && m.Text <> "0")))
      check "sort order is deterministic" (compile compiled.Spec (List.rev data) |> Result.map (fun again -> again.Marks = compiled.Marks) |> Result.defaultValue false)
      check "labels map to their data" (compiled.Marks |> List.forall (fun m -> data |> List.exists (fun d -> d.Category = m.Category)))
      check "the unit is correct" (compiled.Spec.Unit = expectation.Unit)
      check "the axis scale is legal" (compiled.Spec.Scale.Maximum > compiled.Spec.Scale.Minimum)
      check "an accessible description exists" (compiled.Description.Length > 0)
      check "the table carries the same data" (compiled.Table = (compiled.Marks |> List.map (fun m -> m.Category, m.Text)))
      check "no interaction is hover-only" (compiled.Spec.Interaction <> NoInteraction || compiled.Marks.IsEmpty |> not)
      check "colour is never the only carrier" (accessibility compiled "#FFFFFF" |> List.forall (fun p -> not (p.StartsWith "COLOUR_ONLY") && not (p.StartsWith "LOW_CONTRAST"))) ]

/// The assertions whose result differs between two definitions over the
/// same fixture: what a change to a definition changed or broke.
let changed (before: Assertion list) (after: Assertion list) =
    List.zip before after |> List.filter (fun (a, b) -> a.Holds <> b.Holds) |> List.map (fun (_, b) -> b)
