/// The compound answer kinds on the survey page and in authoring (WI-0081,
/// SCS-013): rankings and allocations as numbers per option, a range as its
/// two ends, best-worst on two scales, option trees indented.
module Echelon.Signal.Tests.CompoundKindTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Admin

let private editor = SurveyPageTests.compoundEditor
let private content = editor.Draft.Content
let private bytes = publishedFile "COMPOUND" "1" content |> snd
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private group = oid 70
let private day = DateOnly(2026, 1, 1)

let private opened () =
    match start ("#r=" + GenericEnvelope.invitation "COMPOUND" "1" content Import.IdentifiedGroup (oid 1) group) |> received day (Found bytes) with
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private answer (session: Session) id =
    match session with
    | Responding r -> r.Answers.TryFind id
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the console authors ranking, allocation and best-worst, and the draft publishes`` () =
    let kinds = content.Sections.Head.Questions |> List.map (fun q -> q.Id, q.Answer) |> Map.ofList
    Assert.True(match kinds["Q1"] with Primitives.Ranking r -> r.Options.Length = 3 | _ -> false)
    Assert.True(match kinds["Q2"] with Primitives.Allocation a -> a.Total = 10 && a.Options.Length = 3 | _ -> false)
    Assert.True(match kinds["Q3"] with Primitives.BestWorst os -> os.Length = 3 | _ -> false)
    let r = Authoring.report editor
    Assert.True(r.Passes, $"%A{r.Blockers}")
    Assert.True(Authoring.apply (Authoring.AddQuestion("S1", "Order these", QuestionKinds.Rank, "One")) editor |> Result.isError)
    Assert.True(Authoring.apply (Authoring.AddQuestion("S1", "Pick", QuestionKinds.BestWorstPair, "A, B")) editor |> Result.isError)

[<Fact>]
let ``a ranking is typed position by position, and a position can be moved or cleared`` () =
    let s = Responding(opened ())
    let s = s |> update (Numbered("Q1", "o2", "1")) |> update (Numbered("Q1", "o1", "2"))
    Assert.Equal(Some(Value(Order [ "o2"; "o1" ])), answer s "Q1")
    // Out of range or not a number: nothing changes.
    Assert.Equal(answer s "Q1", answer (update (Numbered("Q1", "o3", "9")) s) "Q1")
    Assert.Equal(answer s "Q1", answer (update (Numbered("Q1", "o3", "x")) s) "Q1")
    let s = update (Numbered("Q1", "o1", "1")) s
    Assert.Equal(Some(Value(Order [ "o1"; "o2" ])), answer s "Q1")
    Assert.Equal(Some(Value(Order [ "o2" ])), answer (update (Numbered("Q1", "o1", "")) s) "Q1")

[<Fact>]
let ``an allocation must share exactly its points before the response submits`` () =
    let r = opened ()
    let s = Responding r |> update (Numbered("Q2", "o1", "4")) |> update (Numbered("Q2", "o2", "3"))
    Assert.Equal(Some(Value(Allocated(Map.ofList [ "o1", 4; "o2", 3 ]))), answer s "Q2")
    Assert.Equal(answer s "Q2", answer (update (Numbered("Q2", "o3", "11")) s) "Q2")

    let rest =
        s
        |> update (Numbered("Q1", "o1", "1"))
        |> update (Numbered("Q1", "o2", "2"))
        |> update (Numbered("Q1", "o3", "3"))
        |> update (Picked("Q3", GenericInputs.Best, "o1"))
        |> update (Picked("Q3", GenericInputs.Worst, "o3"))
        |> update (Numbered("R1", "low", "2"))
        |> update (Numbered("R1", "high", "5"))
        |> update (Chose("T1", Value(Choice "be")))
        |> update (Toggled("T2", "fe", true))

    // 7 of 10 points shared: the rules refuse it, nothing is repaired.
    match update (SubmitRequested(Array.zeroCreate 16)) rest with
    | Responding refused ->
        Assert.Equal(Answering, refused.Phase)
        Assert.True(refused.Refusal.IsSome)
    | other -> failwith $"%A{other}"

    let complete = update (Numbered("Q2", "o3", "3")) rest

    match update (SubmitRequested(Array.zeroCreate 16)) complete with
    | Responding submitted ->
        Assert.Equal(Submitted, submitted.Phase)

        let definition: Import.GroupDefinition =
            { Group = group
              Mode = Import.IdentifiedGroup
              ExpectedCount = 1
              Template = GenericImport.shapeOf "COMPOUND" "1" content
              Generic = Some(GenericImport.formOf "COMPOUND" "1" content) }

        match GenericImport.evaluate definition (fun _ -> None) ("https://signal.example/web/survey/" + fragment submitted) with
        | Import.Accepted accepted -> Assert.Equal(6, accepted.AnsweredCount)
        | other -> failwith $"%A{other}"
    | other -> failwith $"%A{other}"

[<Fact>]
let ``best and least are two picks, and the same option cannot be both`` () =
    let s = Responding(opened ()) |> update (Picked("Q3", GenericInputs.Best, "o1"))
    Assert.Equal(Some(Value(BestWorstPick("o1", ""))), answer s "Q3")
    let s = update (Picked("Q3", GenericInputs.Worst, "o1")) s
    Assert.Equal(Some(Value(BestWorstPick("", "o1"))), answer s "Q3")

[<Fact>]
let ``a range takes its two ends on the scale's ticks`` () =
    let s = Responding(opened ()) |> update (Numbered("R1", "low", "3"))
    Assert.Equal(Some(Value(TickRange(3, -1))), answer s "R1")
    Assert.Equal(answer s "R1", answer (update (Numbered("R1", "high", "2.5")) s) "R1")
    Assert.Equal(Some(Value(TickRange(3, 7))), answer (update (Numbered("R1", "high", "7")) s) "R1")

[<Fact>]
let ``option trees are indented, and only leaves are offered when the tree says so`` () =
    let tree = (Template.tryQuestion content "T1").Value
    Assert.Equal<string list>([ "— Backend"; "— Frontend"; "Design" ], choices tree |> List.map snd)
    let multi = (Template.tryQuestion content "T2").Value
    Assert.Equal<string list>([ "Engineering"; "— Backend"; "— Frontend"; "Design" ], toggles multi |> List.map snd)
    let s = Responding(opened ()) |> update (Toggled("T2", "eng", true)) |> update (Toggled("T2", "be", true)) |> update (Toggled("T2", "eng", false))
    Assert.Equal(Some(Value(Choices(set [ "be" ]))), answer s "T2")

[<Fact>]
let ``the row keys name each compound input`` () =
    let r = opened ()
    Assert.Equal(Some("Q1", "o3"), numberFor r "0-n2")
    Assert.Equal(Some("R1", "high"), numberFor r "3-n1")
    Assert.Equal(Some("Q3", "o2"), pairFor r "2-p1")
    Assert.Equal(None, pairFor r "0-p0")
    Assert.Equal(None, numberFor r "2-n0")
