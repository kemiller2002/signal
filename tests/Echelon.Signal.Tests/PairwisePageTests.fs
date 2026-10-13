/// Pairwise comparison on the survey page (SCS-013, WI-0081): a pairwise
/// question is one choice between two options. The page offers them as two
/// keyboard-operable choices, not a drag, declares the pairwise family for
/// the design system, and the choice travels in the link and is read back.
module Echelon.Signal.Tests.PairwisePageTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Admin

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private pairwise: Question =
    { Id = "P1"
      Prompt = "Which matters more to the team this quarter?"
      HelpText = None
      Answer = SingleChoice [ { Id = "speed"; Label = "Shipping sooner"; Score = None }; { Id = "quality"; Label = "Fewer defects"; Score = None } ]
      Selector = { Preset = Selectors.Pairwise; Labels = []; Accessibility = Selectors.noAccessibility }
      SpecialStates = []
      Required = false
      Tags = [] }

let private editor =
    let e = SurveyPageTests.mixedEditor
    { e with Draft = Drafts.addQuestion "S1" pairwise e.Draft |> ok }

let private content = editor.Draft.Content
let private bytes = publishedFile "PAIRS" "1" content |> snd
let private form = GenericImport.formOf "PAIRS" "1" content
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

let private opened () =
    match start ("#r=" + GenericEnvelope.invitation "PAIRS" "1" content Import.IdentifiedGroup (oid 1) (oid 70)) |> received (DateOnly(2026, 1, 1)) (Found bytes) with
    | Responding _ as s -> s
    | other -> failwith $"%A{other}"

let private rows (session: Session) =
    match (GenericSessionView.view session |> Map.ofList)["rows"] with
    | View.Items items -> items |> List.map Map.ofList
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a pairwise question is two keyboard choices on the page, its choice travels in the link, and the draft publishes`` () =
    Assert.True((Authoring.report editor).Passes, $"%A{(Authoring.report editor).Blockers}")

    let session = opened ()
    let all = rows session
    let question = all |> List.find (fun row -> row["prompt"] = View.Text pairwise.Prompt)
    Assert.Equal(View.Text "signal-pairwise", question["component"])
    Assert.Equal(View.Text "two choices", question["variant"])

    // Its two options are native radios in one group (the "option" rows), never a drag.
    let id = match question["id"] with View.Text t -> t | other -> failwith $"%A{other}"
    let options = all |> List.filter (fun row -> row["hideOption"] = View.Flag false && row["group"] = View.Text $"q{id}")
    Assert.Equal<View.Scalar list>([ View.Text "Shipping sooner"; View.Text "Fewer defects" ], options |> List.map (fun row -> row["label"]))

    let chosen = update (Chose("P1", Value(Choice "quality"))) session

    match chosen with
    | Responding r ->
        Assert.Equal(Some(Value(Choice "quality")), r.Answers.TryFind "P1")
        let payload = (payloadIn (fragment r) |> Result.toOption).Value

        match GenericEnvelope.decode content form.Reference payload with
        | Ok envelope -> Assert.Equal<Answers>(r.Answers, envelope.Answers)
        | Error e -> failwith $"%A{e}"
    | other -> failwith $"%A{other}"
