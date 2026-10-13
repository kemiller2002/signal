/// Multi-choice counts on the survey page (SCS-011, WI-0081): the page says
/// how many to choose before anything fails, says where a selection stands
/// as it changes, and a selection the count rule refuses is held on the page,
/// never written into a link that could not be read back (LURL-001).
module Echelon.Signal.Tests.SelectionCountTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private day = DateOnly(2026, 1, 1)

/// The mixed survey with Q4 (four practices plus "None of these") asking
/// for two or three, "None of these" only alone.
let private content =
    let base' = SurveyPageTests.mixedEditor.Draft.Content

    let q4 (q: Question) =
        match q.Answer with
        | Primitives.MultiChoice m when q.Id = "Q4" ->
            { q with
                Answer =
                    Primitives.MultiChoice
                        { m with
                            Options = m.Options @ [ { Id = "pr"; Label = "Pair rotation"; Score = None }; { Id = "none"; Label = "None of these"; Score = None } ]
                            Selection = Primitives.Between(2, 3)
                            Exclusive = [ "none" ]
                            WhenExclusive = Primitives.RejectCombination } }
        | _ -> q

    { base' with Sections = base'.Sections |> List.map (fun s -> { s with Questions = s.Questions |> List.map q4 }) }

let private bytes = publishedFile "COUNTS" "1" content |> snd
let private form = GenericImport.formOf "COUNTS" "1" content

let private opened () =
    match start ("#r=" + GenericEnvelope.invitation "COUNTS" "1" content Import.IdentifiedGroup (oid 1) (oid 70)) |> received day (Found bytes) with
    | Responding _ as s -> s
    | other -> failwith $"%A{other}"

let private response =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private optionIds = Primitives.options ((Template.tryQuestion content "Q4").Value.Answer) |> List.map _.Id

let private toggle (index: int) (selected: bool) session = update (Toggled("Q4", optionIds[index], selected)) session

/// What the link decodes to is exactly the answers the session holds.
let private linkEqualsResponse (session: Session) =
    let r = response session
    let payload = (payloadIn (fragment r) |> Result.toOption).Value

    match GenericEnvelope.decode content form.Reference payload with
    | Ok envelope -> Assert.Equal<Answers>(r.Answers, envelope.Answers)
    | Error e -> failwith $"the page wrote a link it cannot read back: %A{e}"

let private q4Row (session: Session) =
    let rows =
        match (GenericSessionView.view session |> Map.ofList)["rows"] with
        | View.Items items -> items
        | other -> failwith $"%A{other}"

    rows |> List.find (fun row -> row |> List.exists (fun (k, v) -> k = "prompt" && v = View.Text "Which practices does the team use?")) |> Map.ofList

/// Whether the page shows Q4's option `index` as chosen.
let private toggleChecked (session: Session) (index: int) =
    let rows =
        match (GenericSessionView.view session |> Map.ofList)["rows"] with
        | View.Items items -> items |> List.map Map.ofList
        | other -> failwith $"%A{other}"

    let id = match (q4Row session)["id"] with View.Text t -> t | other -> failwith $"%A{other}"
    rows |> List.exists (fun row -> row["id"] = View.Text $"{id}-t{index}" && row["checked"] = View.Flag true)

let private text (row: Map<string, View.Scalar>) name =
    match row[name] with
    | View.Text t -> t
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the page says how many to choose before anything fails`` () =
    let row = q4Row (opened ())
    Assert.Contains("Choose 2 to 3.", text row "help")
    Assert.Contains("\"None of these\" can only be chosen on its own.", text row "help")
    Assert.Equal("", text row "selectionStatus")

[<Fact>]
let ``a selection the count refuses is held on the page with where it stands, and the link never carries it`` () =
    // One of two to three: held, not answered, and not in the link.
    let one = opened () |> toggle 0 true
    Assert.Equal(None, (response one).Answers.TryFind "Q4")
    linkEqualsResponse one
    Assert.Equal("1 chosen: choose at least 1 more.", text (q4Row one) "selectionStatus")
    // The page still shows the option as chosen.
    Assert.True(toggleChecked one 0)

    // Two: an answer the link carries.
    let two = one |> toggle 1 true
    Assert.Equal(Some(Value(Choices(set [ optionIds[0]; optionIds[1] ]))), (response two).Answers.TryFind "Q4")
    linkEqualsResponse two
    Assert.Equal("2 chosen.", text (q4Row two) "selectionStatus")

    // Four: one too many, held until one is cleared; the last answer is not kept as if current.
    let four = two |> toggle 2 true |> toggle 3 true
    Assert.Equal(None, (response four).Answers.TryFind "Q4")
    linkEqualsResponse four
    Assert.Equal("4 chosen: choose at most 3.", text (q4Row four) "selectionStatus")
    let three = four |> toggle 3 false
    Assert.Equal(Some(Value(Choices(set [ optionIds[0]; optionIds[1]; optionIds[2] ]))), (response three).Answers.TryFind "Q4")

    // The exclusive option with others: held, and the page says why.
    let mixed = opened () |> toggle 0 true |> toggle 1 true |> toggle 4 true
    Assert.Equal(None, (response mixed).Answers.TryFind "Q4")
    linkEqualsResponse mixed
    Assert.Equal("\"None of these\" can only be chosen on its own.", text (q4Row mixed) "selectionStatus")

    // Submitting with a held selection is refused, and says so.
    let refused = update (SubmitRequested(Array.zeroCreate 16)) one |> response
    Assert.Equal(Answering, refused.Phase)
