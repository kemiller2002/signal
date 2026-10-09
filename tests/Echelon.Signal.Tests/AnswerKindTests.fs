/// The other answer kinds and conditional questions (WI-0078, SCS-009..011,
/// ACR-001 flow): authored in the console, published as publication judges
/// them, answered on the survey page, and imported.
module Echelon.Signal.Tests.AnswerKindTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Authoring

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private authored = SurveyPageTests.mixedEditor

let private content = authored.Draft.Content

[<Fact>]
let ``each kind stores what it should, and the section scores only the five-point questions`` () =
    let answers = content.Sections.Head.Questions |> List.map (fun q -> q.Id, q.Answer) |> Map.ofList
    Assert.Equal(Ordinal 5, answers["Q1"])
    Assert.Equal(Boolean, answers["Q2"])
    Assert.True(match answers["Q3"] with SingleChoice os -> os |> List.map _.Label = [ "Continuously"; "Weekly"; "At the end" ] | _ -> false)
    Assert.True(match answers["Q4"] with MultiChoice m -> m.Options.Length = 3 && m.Selection = AnyCount | _ -> false)
    Assert.Equal(BoundedNumber { Minimum = 1.0; Maximum = 5.0; Step = 1.0; Decimals = 0 }, answers["Q5"])
    Assert.Equal(Ordinal 5, answers["Q6"])
    Assert.Equal<string list>([ "Q1"; "Q6" ], content.Sections.Head.Scoring.Value.Questions)

[<Fact>]
let ``a mixed draft validates and publishes as publication judges it`` () =
    let r = report authored
    Assert.True(r.Passes, $"%A{r.Blockers}")
    Assert.True(Authoring.preview Publication.emptyCatalog authored |> Result.isOk)

[<Fact>]
let ``unusable details and conditions are refused, and the draft is unchanged`` () =
    let refused edit = apply edit authored |> Result.isError
    Assert.True(refused (AddQuestion("S1", "Pick one", QuestionKinds.Choice, "Only")))
    Assert.True(refused (AddQuestion("S1", "Pick one", QuestionKinds.Choice, "Same, same")))
    Assert.True(refused (AddQuestion("S1", "How many?", QuestionKinds.Number, "1-100")))
    Assert.True(refused (AddQuestion("S1", "How many?", QuestionKinds.Number, "5-1")))
    // A condition on a later question, an unknown answer, or a multi-choice.
    Assert.True(refused (AddShowRule("Q2", "Q3", "Weekly")))
    Assert.True(refused (AddShowRule("Q5", "Q2", "Perhaps")))
    Assert.True(refused (AddShowRule("Q5", "Q4", "Pairing")))
    Assert.Equal("Show Q3 only when Q2 is 'Yes'", QuestionKinds.describeRule content content.Rules.Flow.Head)

[<Fact>]
let ``removing a question takes it out of the score and the conditions that name it`` () =
    let removed = apply (RemoveQuestion "Q2") authored |> ok
    Assert.Empty removed.Draft.Content.Rules.Flow
    let unscored = apply (RemoveQuestion "Q1") authored |> ok |> apply (RemoveQuestion "Q6") |> ok
    Assert.Equal(None, unscored.Draft.Content.Sections.Head.Scoring)
    Assert.True(apply (RemoveRule "show-1") authored |> ok |> fun e -> e.Draft.Content.Rules.Flow.IsEmpty)

// ---- On the survey page -----------------------------------------------------------------------

let private file, bytes = publishedFile "MIXED" "1" content
let private form = GenericImport.formOf "MIXED" "1" content
let private group = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (60 + i)))).Value
let private instance = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (90 + i)))).Value

let private step msg session = GenericSession.update msg session

let private opened () =
    match GenericSession.start ("#r=" + GenericEnvelope.invitation "MIXED" "1" content Import.IdentifiedGroup instance group) |> received (Found bytes) with
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private rowIds session =
    match GenericSessionView.view session |> List.find (fst >> (=) "rows") |> snd with
    | View.Items rows -> rows |> List.map (fun row -> row |> List.pick (fun (k, v) -> if k = "id" then (match v with View.Text t -> Some t | _ -> None) else None))
    | _ -> []

[<Fact>]
let ``the survey page shows a conditional question only once its condition holds`` () =
    let r = opened ()
    Assert.DoesNotContain("2", rowIds (Responding r))
    let yes = step (Chose("Q2", Value(Flag true))) (Responding r)
    Assert.Contains("2", rowIds yes)
    Assert.Contains("2-1", rowIds yes)

let private tickLabels (_: Response) =
    GenericSession.choices (Template.tryQuestion content "Q5").Value
    |> List.choose (fun (state, label) -> match state with Value _ -> Some label | _ -> None)

[<Fact>]
let ``multi-choice options toggle, numbers are their ticks, and the response imports`` () =
    let r = opened ()
    Assert.Equal(Some("Q4", "o2"), toggleFor r "3-t1")
    Assert.Equal(None, toggleFor r "3-t9")
    Assert.Equal<string list>([ "1"; "2"; "3"; "4"; "5" ], tickLabels r)

    let session =
        Responding r
        |> step (Chose("Q1", Value(Point 3)))
        |> step (Chose("Q2", Value(Flag true)))
        |> step (Chose("Q3", Value(Choice "o2")))
        |> step (Toggled("Q4", "o1", true))
        |> step (Toggled("Q4", "o3", true))
        |> step (Toggled("Q4", "o3", true)) // already selected: nothing changes
        |> step (Chose("Q5", Value(Tick 4)))
        |> step (Chose("Q6", Value(Point 4)))

    Assert.Equal(Some(Value(Choices(set [ "o1"; "o3" ]))), (responding session).Answers.TryFind "Q4")
    Assert.Equal(None, (responding (step (Toggled("Q4", "o1", false)) session |> step (Toggled("Q4", "o3", false)))).Answers.TryFind "Q4")

    let submitted = step (SubmitRequested(Array.zeroCreate 16)) session |> responding
    Assert.Equal(Submitted, submitted.Phase)

    let definition: Import.GroupDefinition =
        { Group = group
          Mode = Import.IdentifiedGroup
          ExpectedCount = 1
          Template = GenericImport.shapeOf "MIXED" "1" content
          Generic = Some form }

    match GenericImport.evaluate definition (fun _ -> None) ("https://signal.example/web/survey/" + fragment submitted) with
    | Import.Accepted accepted -> Assert.Equal(6, accepted.AnsweredCount)
    | other -> failwith $"%A{other}"

