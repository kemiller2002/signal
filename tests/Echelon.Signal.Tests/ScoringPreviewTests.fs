/// A fixture's scoring explained to its author (WI-0081; AUT-006 §63,
/// SCS-017, AST-005): raw answers, contributions, weights, reversal,
/// exclusions and substitutes, numerator over denominator, transforms and
/// rounding, the section and overall scores, bands, facts, recommendations,
/// coverage and the rule trace, all from the one evaluation that scores.
module Echelon.Signal.Tests.ScoringPreviewTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Admin
open Echelon.Signal.Admin.ScoringPreview

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private mixed = SurveyPageTests.mixedEditor.Draft.Content
let private midpoint = SurveyPageTests.mixedEditor.Draft.Fixtures.Head.Answers
/// How many questions S1 scores: weights must name each of them.
let private s1Count = scoredQuestions (mixed.Sections |> List.find (fun s -> s.Id = "S1")) |> List.length
let private weights (first: float) = first :: List.replicate (s1Count - 1) 1.0

let private texts kind (lines: Line list) = lines |> List.filter (fun l -> l.Kind = kind) |> List.map _.Text

let private withS1Scorer (change: Scoring.Scorer -> Scoring.Scorer) (content: Content) =
    { content with
        Sections =
            content.Sections
            |> List.map (fun s ->
                match s.Scoring with
                | Some sc when s.Id = "S1" -> { s with Scoring = Some { sc with Scorer = change sc.Scorer } }
                | _ -> s) }

[<Fact>]
let ``a fixture's explanation shows each question's contribution and the section score the result computes`` () =
    let lines = explain mixed midpoint
    let result = SurveyResult.compute "preview" mixed midpoint false
    let scored = scoredQuestions (mixed.Sections |> List.find (fun s -> s.Id = "S1")) |> List.filter (fun q -> result.Evaluation.Applicability.Questions.Contains q.Id)

    Assert.Contains(lines, fun l -> l.Kind = SectionHeading && l.Text.StartsWith "Everything (S1)")
    Assert.Equal(scored.Length, (texts Contribution lines).Length)

    for q in scored do
        Assert.Contains(texts Contribution lines, fun t -> t.StartsWith $"{q.Id}: '{GenericReview.answerText q (midpoint.TryFind q.Id)}'")

    match result.Evaluation.Sections |> List.find (fst >> (=) "S1") with
    | _, Rules.SectionScored outcome -> Assert.Contains($"section score: {outcomeText outcome}", texts Calculation lines)
    | other -> failwith $"%A{other}"

    Assert.Contains(texts Coverage lines, fun t -> t.Contains "applicable")

[<Fact>]
let ``the written-out arithmetic is the trace's own aggregate, for every aggregate that has one`` () =
    let random = Random 5
    let qs = questions mixed |> List.map snd

    let mutable checkedCount = 0

    for aggregate in [ Scoring.Mean; Scoring.RawSum; Scoring.WeightedMean(weights 3.0); Scoring.WeightedSum(weights 2.0); Scoring.PercentageOfMaximum 4.0 ] do
        let content = mixed |> withS1Scorer (fun s -> { s with Aggregate = aggregate })

        for _ in 1..40 do
            let answers =
                qs
                |> List.choose (fun q ->
                    match GenericSession.choices q with
                    | [] -> None
                    | cs when random.Next 4 > 0 -> Some(q.Id, fst cs[random.Next cs.Length])
                    | _ -> None)
                |> Map.ofList

            let result = SurveyResult.compute "preview" content answers false
            let section = content.Sections |> List.find (fun s -> s.Id = "S1")

            match result.SectionTraces |> List.tryFind (fst >> (=) "S1"), section.Scoring with
            | Some(_, trace), Some sc ->
                match arithmetic sc.Scorer trace, trace.Aggregated with
                | Some(num, den), Some aggregated ->
                    Assert.Equal(aggregated, num / den, 9)
                    checkedCount <- checkedCount + 1
                | _ -> ()
            | _ -> ()

    // Every aggregate was actually checked, many times over.
    Assert.True(checkedCount > 150, $"only {checkedCount} cases had arithmetic")

[<Fact>]
let ``reversal, weights, a declared substitute, transforms and rounding are each shown`` () =
    let content =
        mixed
        |> withS1Scorer (fun s ->
            { s with
                Scale = Scoring.Reverse(0.0, 4.0)
                Aggregate = Scoring.WeightedMean(weights 2.0)
                Transforms = [ Scoring.LinearTransform(25.0, 0.0); Scoring.Clamp(0.0, 90.0) ]
                Missing = { MinimumObservations = 1; Special = Scoring.Substitute 2.0 }
                Decimals = 1 })

    let firstScored = scoredQuestions (content.Sections |> List.find (fun s -> s.Id = "S1")) |> List.head
    let answers = midpoint |> Map.add firstScored.Id (Special DontKnow)
    let lines = explain content answers
    let contributions = texts Contribution lines
    let calculations = texts Calculation lines

    Assert.Contains(contributions, fun t -> t.StartsWith $"{firstScored.Id}: 'Don't know', Don't know → counted as 2, the declared substitute for Don't know")
    Assert.Contains(contributions, fun t -> t.Contains "reversed" && t.Contains "weight 1")
    Assert.Contains(texts SectionHeading lines, fun t -> t.Contains "each answer is reversed within 0 to 4" && t.Contains "special answers count as 2")
    Assert.Contains(calculations, fun t -> t.StartsWith "weighted mean: " && t.Contains " / ")
    Assert.Contains(calculations, fun t -> t.StartsWith "× 25 + 0: ")
    Assert.Contains(calculations, fun t -> t.StartsWith "bounded to 0–90: ")
    Assert.Contains(calculations, fun t -> t.StartsWith "rounded to 1 decimal place(s): ")

[<Fact>]
let ``an excluded special and too few answers are explained, never as zero`` () =
    let content = mixed |> withS1Scorer (fun s -> { s with Missing = { MinimumObservations = 9; Special = Scoring.Exclude } })
    let firstScored = scoredQuestions (content.Sections |> List.find (fun s -> s.Id = "S1")) |> List.head
    let lines = explain content (midpoint |> Map.add firstScored.Id (Special DontKnow))
    Assert.Contains(texts Contribution lines, fun t -> t.StartsWith $"{firstScored.Id}: 'Don't know', Don't know → excluded")
    Assert.Contains(texts Calculation lines, fun t -> t.StartsWith "section score: not scored:" && t.Contains "never counted as zero")

[<Fact>]
let ``the rule trace says which question the answers hide and why`` () =
    let hidden = explain mixed (midpoint |> Map.add "Q2" (Value(Flag false)))
    Assert.Contains("Q3 is not shown: Show Q3 only when Q2 is 'Yes'", texts RuleTrace hidden)
    let shown = explain mixed (midpoint |> Map.add "Q2" (Value(Flag true)))
    Assert.Empty(texts RuleTrace shown)

[<Fact>]
let ``bands, facts and triggered recommendations are shown with what they come from`` () =
    let content =
        mixed
        |> RuleAuthoring.apply (RuleAuthoring.AddFact("Q2", "Yes"))
        |> Result.bind (RuleAuthoring.apply (RuleAuthoring.AddRecommendation("Q2", "Yes", "important", "Ship smaller", "Release in smaller steps.")))
        |> ok

    let bands: Scoring.Band list = [ { Label = "Low"; From = 0.0; To = 2.0 }; { Label = "High"; From = 2.0; To = 4.0 } ]
    let content = { content with Results = { content.Results with Interpretations = [ { Id = "s1-level"; Target = SectionResultOf "S1"; Kind = Bands bands } ] } }
    let lines = explain content (midpoint |> Map.add "Q2" (Value(Flag true)))

    Assert.Contains(texts Band lines, fun t -> t.StartsWith "s1-level on section S1: " && t.Contains "Low 0–2, High 2–4")
    Assert.Contains(texts Fact lines, fun t -> t.EndsWith "= true")
    Assert.Contains(texts Recommendation lines, fun t -> t.StartsWith "triggered: " && t.Contains "Ship smaller")

[<Fact>]
let ``an answer key's credit and penalty are explained from the evaluation that scores`` () =
    // Q3 ("How did it ship?") keyed: "Continuously" correct (2 points), a wrong answer costs 1.
    let q3 = (tryQuestion mixed "Q3").Value
    let options = Primitives.options q3.Answer |> List.map _.Id
    let key: Keyed.SingleKey = { Correct = set [ options[0] ]; PointsCorrect = 2.0; PointsIncorrect = -1.0; PointsBlank = 0.0 }

    let content =
        { mixed with
            Results = { mixed.Results with ItemKeys = [ { Question = "Q3"; Key = SingleKeyed key } ] }
            Sections =
                mixed.Sections
                |> List.map (fun s ->
                    match s.Scoring with
                    | Some sc when s.Id = "S1" -> { s with Scoring = Some { sc with Questions = [ "Q3" ]; Scorer = { sc.Scorer with Aggregate = Scoring.RawSum; Missing = { sc.Scorer.Missing with MinimumObservations = 1 } } } }
                    | _ -> s) }

    let answered choice = midpoint |> Map.add "Q2" (Value(Flag true)) |> Map.add "Q3" (Value(Choice choice))
    let right = explain content (answered options[0])
    let wrong = explain content (answered options[1])

    Assert.Contains($"Q3 answer key: credited {options[0]}, penalized none; 2 − 0 = 2", texts Contribution right)
    Assert.Contains($"Q3 answer key: credited none, penalized {options[1]}; 0 − 1 = -1", texts Contribution wrong)
    // The key's final is the contribution the section aggregates (before
    // the authored scorer's rescaling).
    Assert.Contains("sum: -1", texts Calculation wrong)
