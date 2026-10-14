/// What the scoring area of the draft editor shows (ALG-004; WI-0081):
/// each section's scoring in words, the models and overall methods to pick
/// from, the interpretations and the overall score.
///
/// Pure.
module Echelon.Signal.Admin.ScoringAuthoringView

open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Template

let private text (value: string) = Value(Text value)

let private choices (options: (string * string) list) (selected: string) =
    Items(options |> List.map (fun (value, label) -> [ "value", Text value; "label", Text label; "selected", Flag(value = selected) ]))

let project (content: Content option) (form: ScoringAuthoring.Form) : (string * ViewValue) list =
    let sections = content |> Option.map _.Sections |> Option.defaultValue []
    let selectedSection = if form.Section = "" then sections |> List.tryHead |> Option.map _.Id |> Option.defaultValue "" else form.Section

    [ "draftSectionScoring", Items(sections |> List.map (fun s -> [ "id", Text s.Id; "label", Text(ScoringAuthoring.describeSection s) ]))
      "scoringSectionChoices", choices (sections |> List.map (fun s -> s.Id, $"{s.Title} ({s.Id})")) selectedSection
      "scoringModels", choices ScoringAuthoring.models form.Model
      "scoringWeights", text form.Weights
      "scoringValues", text form.Values
      "scoringReverse", Value(Flag form.Reverse)
      "scoringSubstitute", text form.Substitute
      "scoringMinimum", text form.Minimum
      "draftInterpretations",
      Items(
          content
          |> Option.map (fun c -> c.Results.Interpretations |> List.map (fun i -> [ "id", Text i.Id; "label", Text(ScoringAuthoring.describeInterpretation i) ]))
          |> Option.defaultValue []
      )
      "bandTarget", text form.BandTarget
      "bandText", text form.Bands
      "passTarget", text form.PassTarget
      "passThreshold", text form.PassThreshold
      "passDirections", choices [ "higher", "Higher is better"; "lower", "Lower is better" ] form.PassDirection
      "overallNow", text (content |> Option.map ScoringAuthoring.describeOverall |> Option.defaultValue "")
      "overallMethods", choices ScoringAuthoring.overallMethods form.OverallMethod
      "overallWeights", text form.OverallWeights
      "overallThreshold", text form.OverallThreshold
      "overallCap", text form.OverallCap
      "overallDirections", choices [ "higher", "Higher is better"; "lower", "Lower is better" ] form.OverallDirection ]
