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

let private held (session: Session) id =
    match session with
    | Responding r -> r.Pending.TryFind id
    | other -> failwith $"%A{other}"

/// The link always equals the response (LURL-001): what it decodes to is
/// exactly the answers the session holds, nothing pending among them.
let private linkEqualsResponse (session: Session) =
    match session with
    | Responding r ->
        let form = GenericImport.formOf "COMPOUND" "1" content
        let payload = (payloadIn (fragment r) |> Result.toOption).Value

        match GenericEnvelope.decode content form.Reference payload with
        | Ok envelope -> Assert.Equal<Answers>(r.Answers, envelope.Answers)
        | Error e -> failwith $"%A{e}"
    | other -> failwith $"%A{other}"

// WI-0086 corrected the four tests below. Before it, they asserted the
// defect from #73: a partial ranking, range or best-worst counted as an
// answer that the link could not carry, and an allocation held without its
// blanks. Each now asserts the corrected behaviour and that the link equals
// the response at every step.

[<Fact>]
let ``a ranking is typed position by position, in any order, and becomes an answer only when complete`` () =
    let s = Responding(opened ()) |> update (Numbered("Q1", "o2", "1")) |> update (Numbered("Q1", "o1", "2"))
    // Two of three positions: held, not answered, not in the link.
    Assert.Equal(None, answer s "Q1")
    Assert.Equal(Some(GenericInputs.RankPositions(Map.ofList [ "o2", 1; "o1", 2 ])), held s "Q1")
    linkEqualsResponse s
    // Out of range or not a number: nothing changes.
    Assert.Equal(held s "Q1", held (update (Numbered("Q1", "o3", "9")) s) "Q1")
    Assert.Equal(held s "Q1", held (update (Numbered("Q1", "o3", "x")) s) "Q1")
    // The last position completes it: an answer, in the link.
    let s = update (Numbered("Q1", "o3", "3")) s
    Assert.Equal(Some(Value(Order [ "o2"; "o1"; "o3" ])), answer s "Q1")
    Assert.Equal(None, held s "Q1")
    linkEqualsResponse s
    // Typed from the last position down, it lands where typed.
    let down = Responding(opened ()) |> update (Numbered("Q1", "o1", "3")) |> update (Numbered("Q1", "o2", "2")) |> update (Numbered("Q1", "o3", "1"))
    Assert.Equal(Some(Value(Order [ "o3"; "o2"; "o1" ])), answer down "Q1")
    // A position another option holds moves; clearing one makes it partial again.
    let moved = update (Numbered("Q1", "o1", "1")) s
    Assert.Equal(Some(GenericInputs.RankPositions(Map.ofList [ "o1", 1; "o3", 3 ])), held moved "Q1")
    Assert.Equal(None, answer moved "Q1")
    let cleared = update (Numbered("Q1", "o1", "")) s
    Assert.Equal(None, answer cleared "Q1")
    Assert.Equal(Some(GenericInputs.RankPositions(Map.ofList [ "o2", 1; "o3", 3 ])), held cleared "Q1")
    linkEqualsResponse cleared

[<Fact>]
let ``an allocation must share exactly its points before the response submits`` () =
    let r = opened ()
    let s = Responding r |> update (Numbered("Q2", "o1", "4")) |> update (Numbered("Q2", "o2", "3"))
    // 7 of 10 points: not an answer the link can carry. Held, every option
    // with a blank at the minimum, and the link agrees (it has no Q2).
    Assert.Equal(None, answer s "Q2")
    Assert.Equal(Some(GenericInputs.Partial(Value(Allocated(Map.ofList [ "o1", 4; "o2", 3; "o3", 0 ])))), held s "Q2")
    linkEqualsResponse s
    Assert.Equal(held s "Q2", held (update (Numbered("Q2", "o3", "11")) s) "Q2")

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

    linkEqualsResponse rest

    // 7 of 10 points shared: unfinished, so submitting is refused; nothing is repaired.
    match update (SubmitRequested(Array.zeroCreate 16)) rest with
    | Responding refused ->
        Assert.Equal(Answering, refused.Phase)
        Assert.Contains("not finished", refused.Refusal.Value)
    | other -> failwith $"%A{other}"

    let complete = update (Numbered("Q2", "o3", "3")) rest
    Assert.Equal(Some(Value(Allocated(Map.ofList [ "o1", 4; "o2", 3; "o3", 3 ]))), answer complete "Q2")
    linkEqualsResponse complete

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
let ``best and least are two picks, the same option cannot be both, and one pick alone is held`` () =
    let s = Responding(opened ()) |> update (Picked("Q3", GenericInputs.Best, "o1"))
    Assert.Equal(None, answer s "Q3")
    Assert.Equal(Some(GenericInputs.Partial(Value(BestWorstPick("o1", "")))), held s "Q3")
    let s = update (Picked("Q3", GenericInputs.Worst, "o1")) s
    Assert.Equal(Some(GenericInputs.Partial(Value(BestWorstPick("", "o1")))), held s "Q3")
    linkEqualsResponse s
    let s = update (Picked("Q3", GenericInputs.Best, "o2")) s
    Assert.Equal(Some(Value(BestWorstPick("o2", "o1"))), answer s "Q3")
    Assert.Equal(None, held s "Q3")
    linkEqualsResponse s

[<Fact>]
let ``a range takes its two ends on the scale's ticks, and one end alone is held`` () =
    let s = Responding(opened ()) |> update (Numbered("R1", "low", "3"))
    Assert.Equal(None, answer s "R1")
    Assert.Equal(Some(GenericInputs.Partial(Value(TickRange(3, -1)))), held s "R1")
    Assert.Equal(held s "R1", held (update (Numbered("R1", "high", "2.5")) s) "R1")
    let s = update (Numbered("R1", "high", "7")) s
    Assert.Equal(Some(Value(TickRange(3, 7))), answer s "R1")
    linkEqualsResponse s

[<Fact>]
let ``submitting with an unfinished answer is refused, and choosing "prefer not to answer" replaces it`` () =
    let r = opened ()
    let s = Responding r |> update (Numbered("Q1", "o1", "1"))

    match update (SubmitRequested(Array.zeroCreate 16)) s with
    | Responding refused ->
        Assert.Equal(Answering, refused.Phase)
        Assert.Contains("not finished", refused.Refusal.Value)
    | other -> failwith $"%A{other}"

    let declined = update (Chose("Q1", Special Declined)) s
    Assert.Equal(None, held declined "Q1")
    Assert.Equal(Some(Special Declined), answer declined "Q1")
    linkEqualsResponse declined

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

// WI-0089: escaped defect from #73. The console's midpoint fixture gave an
// allocation only its first option, a sparse answer the link cannot carry:
// the link fills the other options with their minimum, so the fixture was
// not the answer a respondent's link would hold.
[<Fact>]
let ``the midpoint fixture's answers are complete and travel in the link unchanged`` () =
    let fixture = editor.Draft.Fixtures |> List.find (fun f -> f.Id.StartsWith "midpoint-")

    match fixture.Answers["Q2"] with
    | Value(Allocated steps) ->
        Assert.Equal(3, steps.Count)
        Assert.Equal(10, steps |> Map.toSeq |> Seq.sumBy snd)
    | other -> failwith $"%A{other}"

    let form = GenericImport.formOf "COMPOUND" "1" content
    let envelope: GenericEnvelope.Envelope = { Binding = Unbound; Answers = fixture.Answers }

    match GenericEnvelope.decode content form.Reference (GenericEnvelope.encode content form.Reference envelope) with
    | Ok decoded -> Assert.Equal<Answers>(fixture.Answers, decoded.Answers)
    | Error e -> failwith $"%A{e}"
