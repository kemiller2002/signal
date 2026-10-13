/// The respondent link always equals the response (LURL-001), as a
/// property (WI-0081 follow-up to WI-0086, whose defects escaped for lack of
/// this check). For templates that between them hold every answer kind,
/// generated sequences of the messages the survey page can send are applied
/// step by step. The sequences include partial and out-of-order compound
/// input, special states, toggles on and off, out-of-range and malformed
/// text, and clearing. After every step, the link must decode to exactly the
/// answers and binding the session holds. Seeds are fixed, so every run is
/// the same.
module Echelon.Signal.Tests.LinkRoundTripPropertyTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

/// Templates with every answer kind between them: five-point, yes/no, single
/// choice, multi-choice and number (MIXED); ranking, allocation, best-worst,
/// range and option trees, single and multi (COMPOUND); banks (BANKED).
let private templates =
    [ "MIXED", SurveyPageTests.mixedEditor.Draft.Content
      "COMPOUND", SurveyPageTests.compoundEditor.Draft.Content
      "BANKED", SurveyPageTests.banked
      "DEMO", SurveyPageTests.demo ]

/// What a person may type into a number input, sensible or not.
let private texts = [| ""; "0"; "1"; "2"; "3"; "4"; "5"; "6"; "7"; "8"; "9"; "10"; "11"; "-1"; "2.5"; "x"; " 3 " |]

/// One message the survey page can send for a question, drawn at random.
let private messageFor (random: Random) (q: Question) : Msg option =
    let pick (items: 'a list) = if items.IsEmpty then None else Some items[random.Next items.Length]
    let toggles = toggles q |> List.map fst
    let numbers = GenericInputs.numberInputs q |> List.map _.Id
    let pairs = GenericInputs.pairs q |> List.map fst

    let kinds =
        [ if not (choices q).IsEmpty then 0
          if not toggles.IsEmpty then 1
          if not numbers.IsEmpty then 2
          if not pairs.IsEmpty then 3 ]

    match pick kinds with
    | Some 0 -> pick (choices q) |> Option.map (fun (state, _) -> Chose(q.Id, state))
    | Some 1 -> pick toggles |> Option.map (fun id -> Toggled(q.Id, id, random.Next 2 = 0))
    | Some 2 -> pick numbers |> Option.map (fun id -> Numbered(q.Id, id, texts[random.Next texts.Length]))
    | Some 3 -> pick pairs |> Option.map (fun id -> Picked(q.Id, (if random.Next 2 = 0 then GenericInputs.Best else GenericInputs.Worst), id))
    | _ -> None

/// The link decodes to exactly the response's answers and binding.
let private linkEqualsResponse (content: Content) (reference: byte[]) (r: Response) (context: string) =
    let payload = (payloadIn (fragment r) |> Result.toOption).Value

    match GenericEnvelope.decode content reference payload with
    | Ok envelope ->
        Assert.True(envelope.Answers = r.Answers, $"{context}: the link holds %A{envelope.Answers}, the response %A{r.Answers}")
        Assert.True(envelope.Binding = r.Binding, $"{context}: the binding differs")
    | Error error -> failwith $"{context}: the link does not decode: %A{error}"

let private run (surveyId: string) (content: Content) (mode: Import.IdentityMode) (seed: int) (steps: int) =
    let random = Random seed
    let reference = (GenericImport.formOf surveyId "1" content).Reference
    let file = publishedFile surveyId "1" content |> snd
    let link = "#r=" + GenericEnvelope.invitation surveyId "1" content mode (oid (seed % 200)) (oid 210)
    let qs = questions content |> List.map snd |> List.toArray

    let opened =
        match start link |> received (DateOnly(2026, 1, 1)) (Found file) with
        | Responding r -> r
        | other -> failwith $"{surveyId}: %A{other}"

    linkEqualsResponse content reference opened $"{surveyId} seed {seed} on open"

    [ 1..steps ]
    |> List.fold
        (fun (session: Session) step ->
            match messageFor random qs[random.Next qs.Length] with
            | None -> session
            | Some msg ->
                match update msg session with
                | Responding r as next ->
                    linkEqualsResponse content reference r $"{surveyId} seed {seed} step {step} after %A{msg}"
                    next
                | other -> failwith $"{surveyId} seed {seed} step {step}: %A{other}")
        (Responding opened)
    |> ignore

[<Fact>]
let ``after every message the page can send, the link decodes to exactly the response`` () =
    for surveyId, content in templates do
        for seed in 1..60 do
            run surveyId content (if seed % 2 = 0 then Import.IdentifiedGroup else Import.AnonymousGroup) seed 60

[<Fact>]
let ``the generated sequences reach every kind of compound input, held and answered`` () =
    // The property is only as good as what it exercises: these seeds reach a
    // held ranking, a held range and a held best-worst, and their answers.
    let content = SurveyPageTests.compoundEditor.Draft.Content
    let qs = questions content |> List.map snd |> List.toArray
    let file = publishedFile "COMPOUND" "1" content |> snd
    let link = "#r=" + GenericEnvelope.invitation "COMPOUND" "1" content Import.IdentifiedGroup (oid 1) (oid 210)

    let seen =
        [ for seed in 1..60 ->
              let random = Random seed

              [ 1..60 ]
              |> List.scan
                  (fun session _ ->
                      match messageFor random qs[random.Next qs.Length] with
                      | Some msg -> update msg session
                      | None -> session)
                  (start link |> received (DateOnly(2026, 1, 1)) (Found file)) ]
        |> List.concat
        |> List.collect (function
            | Responding r -> [ for KeyValue(id, held) in r.Pending -> $"held {id} {held.GetType().Name}" ] @ [ for KeyValue(id, _) in r.Answers -> $"answered {id}" ]
            | _ -> [])
        |> Set.ofList

    for expected in [ "held Q1"; "held Q2"; "held Q3"; "held R1"; "answered Q1"; "answered Q2"; "answered Q3"; "answered R1"; "answered T1"; "answered T2" ] do
        Assert.True(seen |> Set.exists (fun s -> s.StartsWith expected), $"never reached: {expected}")
