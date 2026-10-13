/// Every fixture answer survives the link encoding before publication
/// (AUT-004 §41, WI-0081): ENCODING-FIXTURE-ROUND-TRIP. The blocker runs
/// at publication only; published templates are never re-validated.
module Echelon.Signal.Tests.FixtureRoundTripTests

open System.IO
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Tests.Support

let private code = "ENCODING-FIXTURE-ROUND-TRIP"

let private q id answer specials : Question =
    { Id = id
      Prompt = $"How about {id}?"
      HelpText = None
      Answer = answer
      Selector = { Preset = Frequency5; Labels = [ "0"; "1"; "2"; "3"; "4" ]; Accessibility = noAccessibility }
      SpecialStates = specials
      Required = true
      Tags = [] }

let private allocation: AllocationSpec =
    { Options = [ for id in [ "a"; "b"; "c" ] -> { Id = id; Label = id; Score = None } ]
      Total = 10
      Step = 1.0
      ItemMinimum = 0
      ItemMaximum = 10 }

let private content =
    { (newDraft "rt" "Round trip").Content with
        Sections =
            [ { Id = "s"
                Title = "S"
                Description = None
                Required = true
                Questions = [ q "o" (Ordinal 5) [ DontKnow ]; q "a" (Allocation allocation) [] ]
                Presentation = defaultSectionPresentation
                Scoring = None } ] }

let private draftWith (answers: (string * AnswerState) list) =
    { (newDraft "rt" "Round trip") with
        Content = content
        Fixtures = [ { Id = "f"; Name = "f"; Answers = Map.ofList answers; Expect = [] } ] }

let private blockers answers =
    (Validation.validate Validation.defaultPolicy (draftWith answers)).Blockers |> List.map _.Code

let private full = Value(Allocated(Map.ofList [ "a", 10; "b", 0; "c", 0 ]))

[<Fact>]
let ``answers the link carries exactly pass`` () =
    Assert.DoesNotContain(code, blockers [ "o", Value(Point 4); "a", full ])
    Assert.DoesNotContain(code, blockers [ "o", Special DontKnow ])
    Assert.DoesNotContain(code, blockers [])

[<Fact>]
let ``an answer the link cannot carry blocks publication`` () =
    // Out of the ordinal's range: no slot state.
    Assert.Contains(code, blockers [ "o", Value(Point 9) ])
    // A special state the question does not offer.
    Assert.Contains(code, blockers [ "o", Special NotApplicable ])
    // A sparse allocation: the link fills the blanks, so it decodes to
    // another value (the WI-0089 defect class).
    Assert.Contains(code, blockers [ "a", Value(Allocated(Map.ofList [ "a", 10 ])) ])
    // The wrong shape for the question.
    Assert.Contains(code, blockers [ "a", Value(Point 1) ])

[<Fact>]
let ``an answer to an unknown question is left to the fixture run`` () =
    let found = blockers [ "nowhere", Value(Point 1) ]
    Assert.DoesNotContain(code, found)
    Assert.Contains("FIXTURES-FAILED", found)

/// Every valid value index (sampled when there are many) and every offered
/// special state of a question.
let private statesOf (question: Question) =
    let n = valueCount question.Answer
    let sample = if n <= 96UL then [ 0UL .. n - 1UL ] else [ 0UL .. 63UL ] @ [ n / 3UL; n / 2UL; n - 2UL; n - 1UL ]

    [ for i in List.distinct sample do
          match ofIndex question.Answer i with
          | Some v when (Primitives.check question.Answer v).IsNone -> yield Value v
          | _ -> ()
      for s in question.SpecialStates do
          yield Special s ]

/// Fixtures that between them give every question every state above.
let private sweep (c: Content) =
    let perQuestion = questions c |> List.map (fun (_, question) -> question.Id, statesOf question)
    let count = perQuestion |> List.map (snd >> List.length) |> List.fold max 0

    [ for i in 0 .. count - 1 ->
          { Id = $"sweep-{i}"
            Name = $"State {i} of every question"
            Answers = perQuestion |> List.choose (fun (id, states) -> List.tryItem i states |> Option.map (fun s -> id, s)) |> Map.ofList
            Expect = [] } ]

let private shipped =
    [ "SDRA", Pilot.content
      for file in Directory.GetFiles(repoFile "published-templates", "*.json") |> Array.sort do
          match TemplateDecode.decode (File.ReadAllBytes file) with
          | Ok decoded -> $"{decoded.SurveyId} {decoded.Version}", decoded.Content
          | Error e -> failwith $"{file}: %A{e}" ]

[<Fact>]
let ``every shipped, sample and demo template carries every answer state through the link`` () =
    // The SDRA pilot and every published file the survey page and the
    // Pages demo serve (published-templates/).
    Assert.True(shipped.Length >= 9, $"%A{shipped |> List.map fst}")

    for name, c in shipped do
        let fixtures = sweep c
        Assert.NotEmpty fixtures
        let draft = { (newDraft "sweep" name) with Content = c; Fixtures = fixtures }
        let failing = (Validation.validate Validation.defaultPolicy draft).Findings |> List.filter (fun f -> f.Code = code)
        Assert.True(failing.IsEmpty, $"{name}: %A{failing}")
