/// Facts and recommendations through the import pipeline into group results
/// (WI-0081, ARP-001 §§14-16, §27; DF-SIGNAL-2026-0012): each accepted
/// result keeps the ids it triggered, its stored record carries them, and
/// the group counts them, withheld below an anonymous group's minimum.
module Echelon.Signal.Tests.GroupSignalsTests

open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

/// MIXED with F1 (Q2 is Yes), R1 (important, when F1) and R2 (critical, when Q1 is "Never or almost never").
let private content =
    let rule change = Result.bind (Authoring.apply (Authoring.Rule change))

    (Ok SurveyPageTests.mixedEditor
     |> rule (RuleAuthoring.AddFact("Q2", "Yes"))
     |> rule (RuleAuthoring.AddRecommendation("F1", "", "important", "Keep shipping", "Write it down."))
     |> rule (RuleAuthoring.AddRecommendation("Q1", "Never or almost never", "critical", "Finish work", "Limit work in progress."))
     |> ok)
        .Draft.Content

let private form = GenericImport.formOf "MIXED" "1" content
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private group = oid 200

let private definition mode : Import.GroupDefinition =
    { Group = group
      Mode = mode
      ExpectedCount = 3
      Template = GenericImport.shapeOf "MIXED" "1" content
      Generic = Some form }

/// A complete submission: Q1 at the given label, Q2 at the given label, everything else at its first answer.
let private submission (mode: Import.IdentityMode) (instance: int) (q1: string) (q2: string) =
    let answerOf (q: Question) (label: string option) =
        let choices = GenericSession.choices q

        match label with
        | Some l -> choices |> List.find (fun (_, x) -> x = l) |> fst
        | None ->
            match GenericSession.toggles q with
            | (first, _) :: _ -> Responses.Value(Responses.Choices(Set.singleton first))
            | [] -> fst choices.Head

    let answers =
        questions content
        |> List.map snd
        |> List.filter (fun q -> q.Id <> "Q3" || q2 = "Yes")
        |> List.map (fun q -> q.Id, answerOf q (match q.Id with "Q1" -> Some q1 | "Q2" -> Some q2 | _ -> None))
        |> Map.ofList

    let binding =
        match mode with
        | Import.IdentifiedGroup -> Identified(oid instance, group)
        | Import.AnonymousGroup -> Anonymous(oid instance, group)

    "https://signal.example/web/survey/" + GenericImport.link form "" { Binding = binding; Answers = answers }

let private accepted mode instance q1 q2 =
    match GenericImport.evaluate (definition mode) (fun _ -> None) (submission mode instance q1 q2) with
    | Import.Accepted result -> result
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an accepted result keeps the recommendations it triggered and the facts that held`` () =
    let both = accepted Import.IdentifiedGroup 1 "Never or almost never" "Yes"
    Assert.Equal<string list>([ "R2"; "R1" ], both.Recommendations)
    Assert.Equal<string list>([ "F1" ], both.FactsTrue)
    let neither = accepted Import.IdentifiedGroup 2 "Often" "No"
    Assert.Empty(neither.Recommendations)
    Assert.Empty(neither.FactsTrue)

[<Fact>]
let ``the stored record carries them, older records read as none, and a damaged list is refused`` () =
    let result = accepted Import.IdentifiedGroup 1 "Never or almost never" "Yes"
    let provenance: ResultRecord.Provenance = { Origin = ResultRecord.PastedUrl; BatchId = "b1"; ArtifactHash = "sha256:00"; EncodingVersion = 1 }
    let contribution: ResultRecord.Contribution = { Group = group; Result = result; Provenance = provenance; Submission = None }
    let body = ResultRecord.body "ds" contribution
    let template = (definition Import.IdentifiedGroup).Template
    let back = ResultRecord.ofBody template body |> ok
    Assert.Equal<string list>(result.Recommendations, back.Contribution.Result.Recommendations)
    Assert.Equal<string list>(result.FactsTrue, back.Contribution.Result.FactsTrue)

    // A result without them is stored exactly as before: no new fields.
    let plain = ResultRecord.body "ds" { contribution with Result = { result with Recommendations = []; FactsTrue = [] } }
    let text = Json.canonicalText plain
    Assert.DoesNotContain("recommendations", text)
    Assert.DoesNotContain("factsTrue", text)
    Assert.Empty((ResultRecord.ofBody template plain |> ok).Contribution.Result.Recommendations)

    let damaged = Json.canonicalText body |> fun t -> t.Replace("\"factsTrue\":[\"F1\"]", "\"factsTrue\":[1]")
    Assert.NotEqual<string>(Json.canonicalText body, damaged)
    match Json.parse damaged with
    | Ok value -> Assert.True(ResultRecord.ofBody template value |> Result.isError)
    | Error error -> failwith $"%A{error}"

[<Fact>]
let ``the group counts each recommendation and fact once per accepted result, however it is aggregated`` () =
    let results =
        [ accepted Import.IdentifiedGroup 1 "Never or almost never" "Yes"
          accepted Import.IdentifiedGroup 2 "Never or almost never" "No"
          accepted Import.IdentifiedGroup 3 "Often" "Yes" ]

    let accumulator = Incremental.ofResults (definition Import.IdentifiedGroup) results |> ok
    let fromAccumulator = GroupResult.ofAccumulator form.Hash 5 accumulator
    Assert.Equal<(string * int) list>([ "R1", 2; "R2", 2 ], fromAccumulator.Recommendations)
    Assert.Equal<(string * int) list>([ "F1", 2 ], fromAccumulator.Facts)

    let items = questions content |> List.length
    let fromScores = GroupResult.ofScores form.Hash Import.IdentifiedGroup 3 5 (results |> List.map (GroupResult.ofSubmission items))
    Assert.Equal<(string * int) list>(fromAccumulator.Recommendations, fromScores.Recommendations)
    Assert.Equal<(string * int) list>(fromAccumulator.Facts, fromScores.Facts)

[<Fact>]
let ``below an anonymous group's minimum, the counts are withheld with the scores`` () =
    let results = [ accepted Import.AnonymousGroup 1 "Never or almost never" "Yes"; accepted Import.AnonymousGroup 2 "Often" "Yes" ]
    let accumulator = Incremental.ofResults (definition Import.AnonymousGroup) results |> ok
    let withheld = GroupResult.ofAccumulator form.Hash 5 accumulator
    Assert.Empty(withheld.Recommendations)
    Assert.Empty(withheld.Facts)
    Assert.Equal<(string * int) list>([ "F1", 2 ], (GroupResult.ofAccumulator form.Hash 2 accumulator).Facts)

[<Fact>]
let ``a report on the group lists its recommendations by how often they fired, with the template's titles`` () =
    let results = [ for i in 1..3 -> accepted Import.IdentifiedGroup i "Never or almost never" (if i = 1 then "Yes" else "No") ]
    let group = GroupResult.ofAccumulator form.Hash 5 (Incremental.ofResults (definition Import.IdentifiedGroup) results |> ok)
    let family = ReportExport.families |> List.find (fun d -> d.Blocks |> List.contains ReportModel.Recommendations)
    let subject: Report.Subject = { SurveyId = "MIXED"; TemplateVersion = "1"; Content = content }

    match Report.build family subject 5 group [] System.DateTimeOffset.UnixEpoch with
    | Ok report ->
        Assert.Equal<(string * int) list>([ "Finish work", 3; "Keep shipping", 1 ], report.Recommendations |> List.map (fun r -> r.Title, r.Frequency))
    | Error problems -> failwith $"%A{problems}"
