/// The survey page follows the template's presentation (VER-004, WI-0081):
/// question numbers when it asks for them, the progress mode it names, and a
/// review of every answer before submitting when it asks for one.
module Echelon.Signal.Tests.PagePresentationTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private mixed = SurveyPageTests.mixedEditor.Draft.Content

let private withPresentation (change: Presentation -> Presentation) =
    { mixed with Presentation = change mixed.Presentation }

let private opened (content: Content) =
    let bytes = publishedFile "SHOWN" "1" content |> snd

    match start ("#r=" + GenericEnvelope.invitation "SHOWN" "1" content Import.IdentifiedGroup (oid 1) (oid 70)) |> received (DateOnly(2026, 1, 1)) (Found bytes) with
    | Responding _ as s -> s
    | other -> failwith $"%A{other}"

let private view (session: Session) = GenericSessionView.view session |> Map.ofList

let private scalar (session: Session) name =
    match (view session)[name] with
    | View.Value v -> v
    | other -> failwith $"%A{other}"

let private rows (session: Session) =
    match (view session)["rows"] with
    | View.Items items -> items |> List.map Map.ofList
    | other -> failwith $"%A{other}"

let private shown (session: Session) (kind: string) =
    let hide = "hide" + string (Char.ToUpperInvariant kind[0]) + kind.Substring 1
    rows session |> List.filter (fun row -> row[hide] = View.Flag false)

let private numbered (session: Session) =
    shown session "question"
    |> List.map (fun row -> row["questionNumber"], row["prompt"])
    |> List.filter (fun (n, _) -> n <> View.Text "")
    |> List.map (fun (n, p) -> (match n with View.Text t -> t | _ -> ""), (match p with View.Text t -> t | _ -> ""))

/// Every answer the mixed survey needs to be submitted.
let private answerAll session =
    session
    |> update (Chose("Q1", Value(Point 3)))
    |> update (Chose("Q2", Value(Flag true)))
    |> update (Chose("Q3", Value(Choice "o2")))
    |> update (Toggled("Q4", "o1", true))
    |> update (Chose("Q5", Value(Tick 4)))
    |> update (Chose("Q6", Value(Point 4)))

let private phase =
    function
    | Responding r -> r.Phase
    | other -> failwith $"%A{other}"

[<Fact>]
let ``questions are numbered in order among those shown, and not at all when the template says not to`` () =
    let session = opened mixed
    // Q3 is shown only once Q2 is "Yes": it takes no number until then.
    Assert.Equal<string list>([ "1."; "2."; "3."; "4."; "5." ], numbered session |> List.map fst)
    let withQ3 = session |> update (Chose("Q2", Value(Flag true)))
    Assert.Equal<string list>([ "1."; "2."; "3."; "4."; "5."; "6." ], numbered withQ3 |> List.map fst)
    Assert.Equal("How did it ship?", numbered withQ3 |> List.item 2 |> snd)

    let unnumbered = opened (withPresentation (fun p -> { p with ShowQuestionNumbers = false }))
    Assert.Empty(numbered unnumbered)

[<Fact>]
let ``progress is shown in the mode the template names, or not at all`` () =
    let progress mode = opened (withPresentation (fun p -> { p with Progress = mode }))
    Assert.Equal(View.Text "0 of 5 answered", scalar (progress QuestionProgress) "progress")
    Assert.Equal(View.Text "Page 1 of 1", scalar (progress PageProgress) "progress")
    Assert.Equal(View.Text "Section 1 of 1", scalar (progress SectionProgress) "progress")
    Assert.Equal(View.Flag false, scalar (progress NoProgress) "hasProgress")
    Assert.Equal(View.Flag true, scalar (progress QuestionProgress) "hasProgress")

[<Fact>]
let ``a template that asks for a review shows every answer before it is submitted, and only then submits`` () =
    let reviewed = withPresentation (fun p -> { p with ReviewBeforeSubmit = true })

    // Not ready: refused as before, no review.
    let early = opened reviewed |> update (SubmitRequested(Array.zeroCreate 16))
    Assert.Equal(View.Flag false, scalar early "reviewing")
    Assert.Equal(View.Flag true, scalar early "hasRefusal")

    let ready = opened reviewed |> answerAll
    Assert.Equal(View.Text "Review answers", scalar ready "submitLabel")

    // The first request shows the review: every applicable question with its answer in words.
    let review = ready |> update (SubmitRequested(Array.zeroCreate 16))
    Assert.Equal(Answering, phase review)
    Assert.Equal(View.Flag true, scalar review "reviewing")
    Assert.Equal(View.Text "Submit answers", scalar review "submitLabel")
    Assert.Equal(View.Flag false, scalar review "canBack")
    let answers = shown review "review" |> List.map (fun row -> row["answerText"])
    Assert.Equal(6, answers.Length)
    Assert.Contains(View.Text "Yes", answers)
    Assert.Contains(View.Text "Pairing", answers)
    Assert.Empty(shown review "option")

    // Answers cannot change while reviewing; closing the review returns to them.
    let unchanged = review |> update (Chose("Q1", Value(Point 0)))
    Assert.Equal(review, unchanged)
    let back = review |> update ReviewClosed
    Assert.Equal(View.Flag false, scalar back "reviewing")
    Assert.NotEmpty(shown back "option")

    // Submitting from the review submits.
    let submitted = back |> update (SubmitRequested(Array.zeroCreate 16)) |> update (SubmitRequested(Array.zeroCreate 16))
    Assert.Equal(Submitted, phase submitted)

[<Fact>]
let ``a template without a review submits on the first request, as before`` () =
    Assert.Equal(Submitted, opened mixed |> answerAll |> update (SubmitRequested(Array.zeroCreate 16)) |> phase)
