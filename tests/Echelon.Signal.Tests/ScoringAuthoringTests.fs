/// Scoring authoring in the console (WI-0081; ALG-004, AUT-002): the
/// source's primary choices offered by name and compiled to the engine's
/// ordinary scorer algebra, bands and pass/fail, and the overall score;
/// each accepted draft still passes publication, and the scoring preview
/// explains what was chosen.
module Echelon.Signal.Tests.ScoringAuthoringTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Admin
open Echelon.Signal.Admin.ScoringAuthoring

let private editor = SurveyPageTests.mixedEditor
let private s1Count = scoredQuestions (editor.Draft.Content.Sections |> List.find (fun s -> s.Id = "S1")) |> List.length

let private applied (change: Edit) =
    Authoring.apply (Authoring.Scoring change) editor

let private scorerOf (e: Authoring.Editor) =
    (e.Draft.Content.Sections |> List.find (fun s -> s.Id = "S1")).Scoring.Value.Scorer

let private passes (e: Authoring.Editor) = Assert.True((Authoring.report e).Passes, $"%A{(Authoring.report e).Blockers}")

let private ok =
    function
    | Ok value -> value
    | Error (error: string) -> failwith error

[<Fact>]
let ``each named model compiles to the engine's scorer, and the draft still publishes`` () =
    let weights = String.Join(", ", List.replicate s1Count "1" |> List.mapi (fun i w -> if i = 0 then "2" else w))
    let values = "0, 1, 2, 3, 5"

    let expected =
        [ "sum", (fun (s: Scoring.Scorer) -> s.Aggregate = Scoring.RawSum && s.Transforms.IsEmpty)
          "mean", (fun s -> s.Aggregate = Scoring.Mean && s.Transforms.IsEmpty)
          "weighted-sum", (fun s -> s.Aggregate = Scoring.WeightedSum(2.0 :: List.replicate (s1Count - 1) 1.0))
          "weighted-mean", (fun s -> s.Aggregate = Scoring.WeightedMean(2.0 :: List.replicate (s1Count - 1) 1.0))
          "percent-of-maximum", (fun s -> s.Aggregate = Scoring.PercentageOfMaximum 4.0)
          "normalized", (fun s -> s.Aggregate = Scoring.Mean && s.Transforms = [ Scoring.LinearNormalize(0.0, 4.0, 0.0, 100.0) ])
          "mapped", (fun s -> s.Scale = Scoring.Mapped(Map [ 0.0, 0.0; 1.0, 1.0; 2.0, 2.0; 3.0, 3.0; 4.0, 5.0 ]))
          "median", (fun s -> s.Aggregate = Scoring.Median)
          "minimum", (fun s -> s.Aggregate = Scoring.Minimum)
          "maximum", (fun s -> s.Aggregate = Scoring.Maximum) ]

    Assert.Equal<string list>(models |> List.map fst, expected |> List.map fst)

    for model, holds in expected do
        let e = applied (SetSectionModel("S1", model, weights, values)) |> ok
        Assert.True(holds (scorerOf e), $"{model}: %A{scorerOf e}")
        passes e

[<Fact>]
let ``a model with the wrong parameters, or none the console offers, is refused with the reason`` () =
    Assert.Contains($"one weight for each of the section's {s1Count}", (applied (SetSectionModel("S1", "weighted-mean", "1", "")) |> function Error e -> e | Ok _ -> ""))
    Assert.Contains("cannot be negative", (applied (SetSectionModel("S1", "weighted-sum", String.Join(",", List.replicate s1Count "-1"), "")) |> function Error e -> e | Ok _ -> ""))
    Assert.Contains("one value for each answer position", (applied (SetSectionModel("S1", "mapped", "", "0, 1")) |> function Error e -> e | Ok _ -> ""))
    Assert.Contains("not a scoring model", (applied (SetSectionModel("S1", "geometric", "", "")) |> function Error e -> e | Ok _ -> ""))
    Assert.Contains("no section 'S9'", (applied (SetSectionModel("S9", "mean", "", "")) |> function Error e -> e | Ok _ -> ""))

[<Fact>]
let ``reverse scoring and the special-answer policy are set per section`` () =
    let reversed = applied (SetSectionReverse("S1", true)) |> ok
    Assert.Equal(Scoring.Reverse(0.0, 4.0), (scorerOf reversed).Scale)
    passes reversed
    Assert.Equal(Scoring.Direct, (scorerOf (Authoring.apply (Authoring.Scoring(SetSectionReverse("S1", false))) reversed |> ok)).Scale)

    let substituted = applied (SetSectionSpecials("S1", "2", "1")) |> ok
    let expected: Scoring.MissingPolicy = { MinimumObservations = 1; Special = Scoring.Substitute 2.0 }
    Assert.Equal(expected, (scorerOf substituted).Missing)
    Assert.Equal(Scoring.Exclude, (scorerOf (applied (SetSectionSpecials("S1", "", "")) |> ok)).Missing.Special)
    Assert.True(applied (SetSectionSpecials("S1", "", "0")) |> Result.isError)

[<Fact>]
let ``bands and pass or fail are added on a section or the overall score, and malformed ones are refused`` () =
    let banded = applied (AddBands("S1", "Low 0-2; High 2-4")) |> ok
    let interpretation = banded.Draft.Content.Results.Interpretations |> List.exactlyOne
    Assert.Equal(SectionResultOf "S1", interpretation.Target)
    Assert.Equal("bands-1: section S1 in bands Low 0–2, High 2–4", describeInterpretation interpretation)
    passes banded

    let passFail = Authoring.apply (Authoring.Scoring(AddPassFail("S1", "3", "higher"))) banded |> ok
    Assert.Equal(PassFail(3.0, HigherIsBetter), (passFail.Draft.Content.Results.Interpretations |> List.last).Kind)

    let removed = Authoring.apply (Authoring.Scoring(RemoveInterpretation "bands-1")) passFail |> ok
    Assert.Equal<string list>([ "pass-1" ], removed.Draft.Content.Results.Interpretations |> List.map _.Id)

    Assert.True(applied (AddBands("S1", "High seventy")) |> Result.isError)
    Assert.True(applied (AddBands("S9", "Low 0-1")) |> Result.isError)
    // Overlapping bands are accepted as shape and blocked by publication, as every draft problem is.
    let overlapping = applied (AddBands("S1", "Low 0-3; High 2-4")) |> ok
    Assert.False((Authoring.report overlapping).Passes)

[<Fact>]
let ``the overall score is a balanced, weighted, weakest-link or capped composite, or none`` () =
    let overall method weights threshold cap =
        (applied (SetOverall(method, weights, threshold, cap, "higher")) |> ok).Draft.Content.Results.Overall

    let methodOf =
        function
        | Some(Composite c) -> Some c.Method
        | _ -> None

    Assert.Equal(Some BalancedMean, methodOf (overall "balanced" "" "" ""))
    Assert.Equal(Some(WeightedMean [ "S1", 2.0 ]), methodOf (overall "weighted" "S1=2" "" ""))
    Assert.Equal(Some WeakestLink, methodOf (overall "weakest-link" "" "" ""))
    Assert.Equal(Some(MinimumDomain(1.0, 2.0)), methodOf (overall "minimum-domain" "" "1" "2"))
    Assert.Equal(None, overall "none" "" "" "")
    Assert.True(applied (SetOverall("weighted", "S9=1", "", "", "higher")) |> Result.isError)
    passes (applied (SetOverall("weighted", "S1=2", "", "", "higher")) |> ok)

[<Fact>]
let ``what is chosen is what the scoring preview explains`` () =
    let e =
        applied (SetSectionModel("S1", "normalized", "", ""))
        |> Result.bind (Authoring.apply (Authoring.Scoring(SetOverall("balanced", "", "", "", "higher"))))
        |> Result.bind (Authoring.apply (Authoring.Scoring(AddBands("overall", "Low 0-50; High 50-100"))))
        |> ok

    let lines = ScoringPreview.explain e.Draft.Content e.Draft.Fixtures.Head.Answers |> List.map _.Text
    Assert.Contains(lines, fun t -> t.StartsWith "rescaled from 0–4 to 0–100: ")
    Assert.Contains(lines, fun t -> t.StartsWith "overall score: ")
    Assert.Contains(lines, fun t -> t.StartsWith "bands-1 on the overall score: ")
