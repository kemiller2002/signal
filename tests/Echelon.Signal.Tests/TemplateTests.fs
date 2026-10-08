/// The canonical template, its canonical form, pagination, capacity and
/// instance status (WI-0042): CAN-001, CAN-008, URLC-004, VER-002..VER-004,
/// VER-007, ACR-003.
module Echelon.Signal.Tests.TemplateTests

open System
open System.Text
open Xunit
open Microsoft.FSharp.Reflection
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout
open Echelon.Signal.Engine.Instance

let private sdra = Pilot.content

let private ordinal5 id : Question =
    { Id = id
      Prompt = $"Prompt {id}"
      HelpText = None
      Answer = Ordinal 5
      Selector = { Preset = Likert5; Labels = [ "a"; "b"; "c"; "d"; "e" ] }
      SpecialStates = []
      Required = true
      Tags = [] }

let private section id (qs: Question list) : Section =
    { Id = id
      Title = $"Section {id}"
      Description = None
      Required = true
      Questions = qs
      Presentation = defaultSectionPresentation
      Scoring = None }

let private contentOf (sections: Section list) =
    { sdra with Sections = sections; Presentation = defaultPresentation }

[<Fact>]
let ``the SDRA pilot scores identically as a generic template (differential, 3000 seeded samples)`` () =
    let random = Random 20261008
    let domain = Assessment.answerDomain |> List.toArray

    for _ in 1..3000 do
        let answers: Assessment.Answers =
            Pilot.assessment.Items
            |> List.choose (fun item ->
                // About one in ten items is left unanswered.
                if random.Next 10 = 0 then None else Some(item.Id, domain[random.Next domain.Length]))
            |> Map.ofList

        let expected =
            Assessment.score Pilot.assessment answers
            |> List.map (fun (d, r) ->
                d.Id,
                match r with
                | Assessment.Scored(s, _, _) -> Some s
                | Assessment.Unscored _ -> None)

        let actual =
            scoreSections sdra (Pilot.answers answers)
            |> List.map (fun (s, o) ->
                s.Id,
                match o with
                | Scoring.Score(v, _, _) -> Some v
                | Scoring.NotScored _ -> None)

        Assert.Equal<(string * float option) list>(expected, actual)

[<Fact>]
let ``canonical bytes are deterministic and the hash is pinned`` () =
    let a = TemplateCanonical.bytes "SDRA" "1" sdra
    let b = TemplateCanonical.bytes "SDRA" "1" { sdra with Sections = sdra.Sections |> List.map id }
    Assert.Equal<byte[]>(a, b)
    let text = Encoding.UTF8.GetString a
    Assert.StartsWith("{\"form\":\"signal-template/1\",\"surveyId\":\"SDRA\",\"version\":\"1\"", text)
    Assert.DoesNotContain("\n", text)
    // Golden vector: a change here is a change to canonical form 1, which is
    // never edited; a new form is a new version.
    Assert.Equal("sha256:d9649e8a9b3d9616157d4d7a5ef1f6482bd4eedb5742b385243a46ea9aeebccd", TemplateCanonical.templateHash "SDRA" "1" sdra)

[<Fact>]
let ``every interpretive change changes the hash; identity is part of it`` () =
    let h = TemplateCanonical.templateHash "SDRA" "1"
    let baseline = h sdra
    let firstQuestion f = { sdra with Sections = sdra.Sections |> List.mapi (fun i s -> if i = 0 then { s with Questions = s.Questions |> List.mapi (fun j q -> if j = 0 then f q else q) } else s) }
    Assert.NotEqual<string>(baseline, h (firstQuestion (fun q -> { q with Prompt = q.Prompt + "!" })))
    Assert.NotEqual<string>(baseline, h (firstQuestion (fun q -> { q with SpecialStates = [ DontKnow ] })))
    Assert.NotEqual<string>(baseline, h { sdra with Presentation = { sdra.Presentation with ItemsPerPage = Some 5 } })
    Assert.NotEqual<string>(baseline, h { sdra with Sections = List.rev sdra.Sections })
    Assert.NotEqual<string>(baseline, TemplateCanonical.templateHash "SDRA" "2" sdra)
    Assert.NotEqual<string>(baseline, TemplateCanonical.templateHash "SDRB" "1" sdra)

[<Fact>]
let ``absent optional members and empty lists are not written (extension rule)`` () =
    let text = Encoding.UTF8.GetString(TemplateCanonical.bytes "SDRA" "1" sdra)
    Assert.DoesNotContain("\"capabilities\"", text)
    Assert.DoesNotContain("\"help\"", text)
    Assert.DoesNotContain("\"pageBreaksBefore\"", text)
    let withHelp = { sdra with Metadata = { sdra.Metadata with Description = Some "d" } }
    Assert.Contains("\"description\":\"d\"", Encoding.UTF8.GetString(TemplateCanonical.bytes "SDRA" "1" withHelp))

[<Fact>]
let ``the layout fingerprint follows the encoding, not the wording`` () =
    let reworded = { sdra with Metadata = { sdra.Metadata with Title = "Other" } }
    Assert.Equal(TemplateCanonical.layoutFingerprint sdra, TemplateCanonical.layoutFingerprint reworded)
    let fewer = { sdra with Sections = sdra.Sections |> List.take 2 }
    Assert.NotEqual<string>(TemplateCanonical.layoutFingerprint sdra, TemplateCanonical.layoutFingerprint fewer)

[<Fact>]
let ``capacity equals the length of the largest real envelope`` () =
    let size = capacity sdra
    Assert.Equal(15, size.Questions)
    Assert.Equal(60, size.AnswerBits) // 9 states (unanswered, 5 values, 3 special) need 4 bits
    let id = (UrlState.OpaqueId.ofBytes (Array.init 16 byte)).Value
    let group = (UrlState.OpaqueId.ofBytes (Array.init 16 (fun i -> byte (i + 100)))).Value
    let all = Pilot.assessment.Items |> List.map (fun i -> i.Id, Assessment.Rated Assessment.Often) |> Map.ofList
    let envelope: UrlState.Envelope = { Binding = UrlState.Anonymous(id, group); Answers = all }
    Assert.Equal((UrlState.encode Pilot.assessment envelope).Length, size.EncodedCharacters)

[<Fact>]
let ``answers are checked against their question`` () =
    let c = contentOf [ section "s" [ ordinal5 "q1" ] ]
    Assert.Empty(checkAnswers c (Map [ "q1", Value(Point 4) ]))
    Assert.Equal<AnswerProblem list>([ OutOfRange("q1", 5) ], checkAnswers c (Map [ "q1", Value(Point 5) ]))
    Assert.Equal<AnswerProblem list>([ WrongPrimitive "q1" ], checkAnswers c (Map [ "q1", Value(Flag true) ]))
    Assert.Equal<AnswerProblem list>([ SpecialNotOffered("q1", DontKnow) ], checkAnswers c (Map [ "q1", Special DontKnow ]))
    Assert.Equal<AnswerProblem list>([ UnknownQuestion "zz" ], checkAnswers c (Map [ "zz", Value(Point 1) ]))

[<Fact>]
let ``special states never become numbers`` () =
    let q = { ordinal5 "q" with SpecialStates = specialStates }
    Assert.Equal(Scoring.Special Scoring.NotApplicable, observation q (Some(Special NotApplicable)))
    Assert.Equal(Scoring.Special Scoring.Unanswered, observation q None)
    Assert.Equal(Scoring.Numeric 0.0, observation q (Some(Value(Point 0))))

[<Fact>]
let ``no answer primitive can carry free text or personal data`` () =
    let cases = FSharpType.GetUnionCases(typeof<AnswerDefinition>) |> Array.map _.Name

    Assert.Equal<string[]>(
        [| "Boolean"; "Ordinal"; "SingleChoice"; "MultiChoice"; "BoundedNumber"; "BoundedRange"; "Ranking"; "Allocation"; "BestWorst"; "Hierarchical"; "HierarchicalMulti" |],
        cases
    )

    // Every string an answer carries is an option id the template declared:
    // text the respondent typed is refused by every option-based primitive.
    let o id : ChoiceOption = { Id = id; Label = id; Score = None }
    let os = [ o "a"; o "b"; o "c" ]
    let free = "jane.doe@example.com"
    let refuses def value = Assert.True((Primitives.check def value).IsSome, $"%A{def} accepted %A{value}")
    refuses (SingleChoice os) (Choice free)
    refuses (MultiChoice { Options = os; Selection = AnyCount; Exclusive = []; WhenExclusive = RejectCombination }) (Choices(Set [ free ]))
    refuses (Ranking { Options = os; Positions = None }) (Order [ free; "a"; "b" ])
    refuses (Allocation { Options = os; Total = 3; Step = 1.0; ItemMinimum = 0; ItemMaximum = 3 }) (Allocated(Map [ free, 3 ]))
    refuses (BestWorst os) (BestWorstPick(free, "a"))
    refuses (Hierarchical([ { Option = o "a"; Parent = None } ], false)) (Choice free)

[<Fact>]
let ``pagination follows items per page, section overrides, breaks and new-page sections`` () =
    let qs prefix n = [ for i in 1..n -> ordinal5 $"{prefix}{i}" ]
    let pagesOf content = pages content |> List.map _.Questions

    // Survey default 5 over the SDRA's 15 items: three pages.
    let five = { sdra with Presentation = { sdra.Presentation with ItemsPerPage = Some 5 } }
    Assert.Equal(3, (pages five).Length)

    // No limit: everything on one page.
    Assert.Equal(1, (pages sdra).Length)

    let a = section "a" (qs "a" 4)
    let b = { section "b" (qs "b" 3) with Presentation = { defaultSectionPresentation with ItemsPerPage = Some 2 } }
    let c = contentOf [ a; b ]
    let paged = { c with Presentation = { c.Presentation with ItemsPerPage = Some 3 } }
    // a fills pages of 3; b does not start a new page, so b1 fills a's second page; b's own pages hold 2.
    Assert.Equal<string list list>([ [ "a1"; "a2"; "a3" ]; [ "a4"; "b1"; "b2" ]; [ "b3" ] ], pagesOf paged)

    let newPage = { paged with Presentation = { paged.Presentation with SectionStartsOnNewPage = true } }
    Assert.Equal<string list list>([ [ "a1"; "a2"; "a3" ]; [ "a4" ]; [ "b1"; "b2" ]; [ "b3" ] ], pagesOf newPage)

    let broken =
        contentOf [ { a with Presentation = { defaultSectionPresentation with PageBreaksBefore = [ "a3" ] } } ]

    Assert.Equal<string list list>([ [ "a1"; "a2" ]; [ "a3"; "a4" ] ], pagesOf broken)

    // Pagination is presentation: it changes neither the layout nor scores.
    Assert.Equal(TemplateCanonical.layoutFingerprint c, TemplateCanonical.layoutFingerprint paged)

[<Fact>]
let ``instance status is derived and completeness alone never completes`` () =
    let now = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)
    let some = Map [ "q", Value(Point 1) ]
    Assert.Equal(NotStarted, instanceStatus false false None now Map.empty)
    Assert.Equal(InProgress, instanceStatus false false None now some)
    Assert.Equal(Completed, instanceStatus false true None now some)
    Assert.Equal(Expired, instanceStatus false false (Some now) now some)
    Assert.Equal(Completed, instanceStatus false true (Some now) now some)
    Assert.Equal(Cancelled, instanceStatus true true None now some)
    Assert.False(canChangeAnswers defaultRuntime Completed)
    Assert.True(canChangeAnswers { defaultRuntime with AllowChangesAfterCompletion = true } Completed)
    Assert.False(canChangeAnswers defaultRuntime Expired)
    Assert.True(canChangeAnswers defaultRuntime InProgress)
