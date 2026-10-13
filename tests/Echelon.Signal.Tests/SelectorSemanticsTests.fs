/// Selector accessibility semantics and the component handoff on the survey
/// page (WI-0081, SCS-014, SCS-018, SCS-010; DF-SIGNAL-2026-0013): every
/// input names its question, says whether it is required and invalid, and
/// each question declares its component family; nothing starts answered.
module Echelon.Signal.Tests.SelectorSemanticsTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private demo = SurveyPageTests.demo
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

let private opened (content: Content) =
    let file = publishedFile "DEMO" "1" content |> snd
    start ("#r=" + GenericEnvelope.invitation "DEMO" "1" content Import.IdentifiedGroup (oid 1) (oid 40)) |> received (DateOnly(2026, 1, 1)) (Found file)

let private rows (session: Session) =
    match GenericSessionView.view session |> List.find (fst >> (=) "rows") |> snd with
    | View.Items items -> items |> List.map Map.ofList
    | View.Value _ -> failwith "rows is not a list"

let private textOf (row: Map<string, View.Scalar>) name =
    match row[name] with
    | View.Text t -> t
    | other -> failwith $"%A{other}"

let private flagOf (row: Map<string, View.Scalar>) name =
    match row[name] with
    | View.Flag f -> f
    | other -> failwith $"%A{other}"

let private byId session id = rows session |> List.find (fun r -> textOf r "id" = id)

[<Fact>]
let ``every input names its question and says it is required; each question names its component`` () =
    let s = opened demo
    let question = byId s "0"
    Assert.Equal("q0-prompt", textOf question "promptId")
    let family, variant = Selectors.componentOf demo.Sections.Head.Questions.Head.Selector.Preset
    Assert.Equal(family, textOf question "component")
    Assert.Equal(variant, textOf question "variant")
    Assert.Equal("", textOf question "optionalMark")

    for option in rows s |> List.filter (fun r -> (textOf r "id").StartsWith "0-") do
        Assert.Equal("q0-prompt", textOf option "describedBy")
        Assert.Equal("true", textOf option "required")
        Assert.Equal("false", textOf option "invalid")
        // SCS-010: nothing is answered until the person answers it.
        Assert.False(flagOf option "checked")

[<Fact>]
let ``after a refused submit, each unanswered required question is invalid and says why`` () =
    let refused = update (SubmitRequested(Array.zeroCreate 16)) (opened demo)
    let question = byId refused "0"
    Assert.False(flagOf question "hideError")
    Assert.Equal("This question needs an answer.", textOf question "error")
    Assert.Equal("q0-error", textOf question "errorId")
    let option = byId refused "0-0"
    Assert.Equal("q0-prompt q0-error", textOf option "describedBy")
    Assert.Equal("true", textOf option "invalid")

    // Answering it clears its error; the others keep theirs.
    let answered = update (Chose(demo.Sections.Head.Questions.Head.Id, choices demo.Sections.Head.Questions.Head |> List.head |> fst)) refused
    Assert.True(flagOf (byId answered "0") "hideError")
    Assert.False(flagOf (byId answered "1") "hideError")

[<Fact>]
let ``an optional question says so in words, not only by look`` () =
    let section = demo.Sections[1]
    let optional = { demo with Sections = [ demo.Sections[0]; { section with Questions = section.Questions |> List.map (fun q -> { q with Required = false }) } ] }
    let s = opened optional
    Assert.Equal("(optional)", textOf (byId s "3") "optionalMark")
    Assert.Equal("false", textOf (byId s "3-0") "required")
