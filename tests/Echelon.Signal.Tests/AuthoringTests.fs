/// Authoring, validation, fixtures, diff, lifecycle and immutable
/// publication (WI-0042): AUT-001..AUT-007, CAN-005, ACR-003, ACR-008, ARX-002.
module Echelon.Signal.Tests.AuthoringTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Authoring

let private at = DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.Zero)

let private q id : Question =
    { Id = id
      Prompt = $"How often does {id} happen?"
      HelpText = None
      Answer = Ordinal 5
      Selector = { Preset = Frequency5; Labels = [ "0"; "1"; "2"; "3"; "4" ] }
      SpecialStates = [ DontKnow ]
      Required = true
      Tags = [] }

let private meanScorer: Scoring.Scorer =
    { Scale = Scoring.Direct
      Aggregate = Scoring.Mean
      Transforms = []
      Missing = { MinimumObservations = 1; Special = Scoring.Exclude }
      Decimals = 2 }

let private sec id (qs: Question list) : Section =
    { Id = id
      Title = $"Section {id}"
      Description = None
      Required = true
      Questions = qs
      Presentation = defaultSectionPresentation
      Scoring = Some { Scorer = meanScorer; Questions = [] } }

let private fixture =
    { Id = "all-fours"
      Name = "Every answer is 4"
      Answers = Map [ for i in 1..3 -> $"q{i}", Value(Point 4) ]
      Expect = [ SectionScore("s", Some 4.0); Completion true ] }

let private ok (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok v -> v
    | Error e -> failwith $"%A{e}"

/// A small valid draft: one scored section of three questions and a fixture.
let private draft =
    newDraft "team-health" "Team health"
    |> addSection (sec "s" [ q "q1"; q "q2"; q "q3" ])
    |> Result.bind (addFixture fixture)
    |> ok

let private codes (report: ValidationReport) = report.Findings |> List.map _.Code |> Set.ofList

let private blockersOf d = (validate defaultPolicy d).Blockers |> List.map _.Code |> Set.ofList

[<Fact>]
let ``a well-formed draft validates with every category passing`` () =
    let report = validate defaultPolicy draft
    Assert.True(report.Passes, $"%A{report.Findings}")
    Assert.True(summary report |> List.forall snd)
    Assert.Equal<string list>([], report.Fixtures |> List.collect _.Failures)

[<Fact>]
let ``the SDRA pilot validates and publishes as version 1`` () =
    let pilot =
        { SurveyId = "SDRA"
          Parent = None
          Content = Pilot.content
          Fixtures =
            [ { Id = "all-often"
                Name = "Every answer Often"
                Answers = Pilot.assessment.Items |> List.map (fun i -> i.Id, Value(Point 3)) |> Map.ofList
                Expect = [ SectionScore("D01", Some 75.0); SectionScore("D02", Some 75.0); SectionScore("D03", Some 75.0); Completion true ] } ] }

    let published, _ = publish defaultPolicy emptyCatalog Set.empty at "author" pilot |> ok
    Assert.Equal("1", published.Version)
    Assert.True(verify published)

[<Fact>]
let ``edits are pure, keep ids unique and report unknown targets`` () =
    let d2 = draft |> addQuestion "s" (q "q4") |> ok
    Assert.Equal(4, (questions d2.Content).Length)
    Assert.Equal(3, (questions draft.Content).Length) // the original is unchanged
    Assert.Equal(Error(DuplicateId "q1"), draft |> addQuestion "s" (q "q1") |> Result.map ignore)
    Assert.Equal(Error(UnknownSection "nope"), draft |> addQuestion "nope" (q "q9") |> Result.map ignore)
    Assert.Equal(Error(UnknownQuestionId "zz"), draft |> removeQuestion "zz" |> Result.map ignore)
    Assert.Equal(Error(DuplicateId "s"), draft |> addSection (sec "s" []) |> Result.map ignore)
    Assert.Equal(Error(DuplicateId "q2"), draft |> editQuestion "q1" (fun x -> { x with Id = "q2" }) |> Result.map ignore)

    let moved = draft |> addSection (sec "t" [ q "t1" ]) |> Result.bind (moveQuestion "q2" "t" 0) |> ok
    Assert.Equal<string list>([ "q1"; "q3"; "q2"; "t1" ], questions moved.Content |> List.map (fun (_, x) -> x.Id))
    let within = draft |> moveQuestion "q1" "s" 2 |> ok
    Assert.Equal<string list>([ "q2"; "q3"; "q1" ], questions within.Content |> List.map (fun (_, x) -> x.Id))
    Assert.Equal(Error(PositionOutOfRange 9), draft |> moveQuestion "q1" "s" 9 |> Result.map ignore)

    let two = draft |> addSection (sec "t" [ q "t1" ]) |> Result.bind (moveSection "t" 0) |> ok
    Assert.Equal<string list>([ "t"; "s" ], two.Content.Sections |> List.map _.Id)
    Assert.Equal<string list>([ "s" ], (two |> removeSection "t" |> ok).Content.Sections |> List.map _.Id)

[<Fact>]
let ``structural and answer blockers have stable codes`` () =
    let noTitle = draft |> editContent (fun c -> { c with Metadata = { c.Metadata with Title = " " } })
    Assert.Contains("STRUCT-TITLE", blockersOf noTitle)
    Assert.Contains("STRUCT-SURVEY-ID", blockersOf { draft with SurveyId = "bad id!" })
    Assert.Contains("STRUCT-NO-SECTIONS", blockersOf (newDraft "x" "x"))

    let dupe =
        draft |> editContent (fun c -> { c with Sections = c.Sections @ [ sec "t" [ q "q1" ] ] })

    Assert.Contains("STRUCT-DUPLICATE-QUESTION", blockersOf dupe)

    let selector = draft |> editQuestion "q1" (fun x -> { x with Selector = { x.Selector with Preset = YesNo } }) |> ok
    Assert.Contains("ANSWER-SELECTOR-INCOMPATIBLE", blockersOf selector)
    let labels = draft |> editQuestion "q1" (fun x -> { x with Selector = { x.Selector with Labels = [ "a" ] } }) |> ok
    Assert.Contains("ANSWER-LABELS", blockersOf labels)
    let order = draft |> editQuestion "q1" (fun x -> { x with SpecialStates = [ NotApplicable; DontKnow ] }) |> ok
    Assert.Contains("ANSWER-SPECIAL-STATES", blockersOf order)

    let choice =
        draft
        |> editQuestion "q1" (fun x ->
            { x with
                Answer = SingleChoice [ { Id = "a"; Label = "A"; Score = Some 1.0 }; { Id = "a"; Label = "B"; Score = None } ]
                Selector = { Preset = SingleSelect; Labels = [] } })
        |> ok

    let b = blockersOf choice
    Assert.Contains("ANSWER-DUPLICATE-OPTION", b)
    Assert.Contains("SCORING-MAPPING-INCOMPLETE", b) // a scored option without a score

[<Fact>]
let ``scoring, compatibility and encoding blockers`` () =
    let withScorer scorer = draft |> editSection "s" (fun s -> { s with Scoring = Some { Scorer = scorer; Questions = [] } }) |> ok
    Assert.Contains("SCORING-WEIGHTS", blockersOf (withScorer { meanScorer with Aggregate = Scoring.WeightedMean [ 1.0; 2.0 ] }))
    Assert.Contains("SCORING-INVALID-SCORER", blockersOf (withScorer { meanScorer with Decimals = 99 }))

    let mapped = withScorer { meanScorer with Scale = Scoring.Mapped(Map [ 0.0, 0.0; 1.0, 1.0 ]) }
    Assert.Contains("SCORING-MAPPING-INCOMPLETE", blockersOf mapped)

    let reference = draft |> editSection "s" (fun s -> { s with Scoring = Some { Scorer = meanScorer; Questions = [ "nope" ] } }) |> ok
    Assert.Contains("SCORING-REFERENCE", blockersOf reference)

    let compat c = draft |> editContent (fun x -> { x with Compatibility = c })
    Assert.Contains("COMPAT-CAPABILITY", blockersOf (compat { defaultCompatibility with Capabilities = [ UsesBranching ] }))
    Assert.Contains("COMPAT-SCHEMA", blockersOf (compat { defaultCompatibility with SchemaVersion = 2 }))
    Assert.Contains("COMPAT-ENGINE", blockersOf (compat { defaultCompatibility with MinimumEngineVersion = 99 }))
    Assert.Contains("COMPAT-ENCODING", blockersOf (compat { defaultCompatibility with ResponseEncodingVersion = 2 }))

    let tight = { defaultPolicy with MaximumUrlCharacters = 250 }
    Assert.Contains("ENCODING-URL-BUDGET", (validate tight draft).Blockers |> List.map _.Code)

    let unrequired = draft |> editContent (fun c -> { c with Sections = c.Sections |> List.map (fun s -> { s with Required = false }) })
    Assert.Contains("COMPLETION-NOTHING-REQUIRED", blockersOf unrequired)

[<Fact>]
let ``fixtures run, failures block, and a missing fixture blocks by policy`` () =
    let wrong = { fixture with Id = "wrong"; Expect = [ SectionScore("s", Some 1.0) ] }
    let failing = draft |> addFixture wrong |> ok
    let report = validate defaultPolicy failing
    Assert.Contains("FIXTURES-FAILED", report.Blockers |> List.map _.Code)
    Assert.Equal<string list>([ "wrong" ], report.Blockers |> List.filter (fun f -> f.Code = "FIXTURES-FAILED") |> List.map _.Subject)

    let invalid = { fixture with Id = "invalid"; Answers = Map [ "q1", Value(Point 9) ]; Expect = [] }
    Assert.Contains("FIXTURES-FAILED", blockersOf (draft |> addFixture invalid |> ok))

    let none = { draft with Fixtures = [] }
    Assert.Contains("FIXTURES-NONE", blockersOf none)
    Assert.True((validate { defaultPolicy with RequireFixtures = false } none).Passes)

    // The result hash is a function of what the fixture produced.
    let r1 = runFixture draft.Content fixture
    let r2 = runFixture draft.Content { fixture with Id = "copy" }
    Assert.Equal(r1.ResultHash, r2.ResultHash)
    Assert.NotEqual<string>(r1.ResultHash, (runFixture draft.Content { fixture with Answers = Map [ "q1", Value(Point 0) ] }).ResultHash)

[<Fact>]
let ``PII-like prompts warn, or block when policy says so`` () =
    let pii = draft |> editQuestion "q1" (fun x -> { x with Prompt = "What is your email address?" }) |> ok
    let report = validate defaultPolicy pii
    Assert.True(report.Passes)
    Assert.Contains("PRIVACY-PII-LIKE-PROMPT", report.Warnings |> List.map _.Code)
    Assert.Contains("PRIVACY-PII-LIKE-PROMPT", (validate { defaultPolicy with PiiPromptsBlock = true } pii).Blockers |> List.map _.Code)
    Assert.False(isPiiLike "How often does the team review work?")
    Assert.True(isPiiLike "Employee ID")
    Assert.True(isPiiLike "What is your name?")

[<Fact>]
let ``publication assigns versions, supersedes without removing, and locks the hash`` () =
    let v1, catalog = publish defaultPolicy emptyCatalog Set.empty at "author" draft |> ok
    Assert.Equal("1", v1.Version)
    Assert.Equal(PublishedState, publishedState catalog v1)
    Assert.True(verify v1)
    Assert.True(v1.Manifest |> List.forall _.PassedAtPublication)
    Assert.Equal<string list>([ "all-fours" ], v1.Manifest |> List.map _.FixtureId)

    // Same content again is refused, not re-versioned.
    Assert.Equal(Error(Unchanged "1"), publish defaultPolicy catalog Set.empty at "author" draft |> Result.map ignore)

    let next = deriveDraft v1 |> editQuestion "q1" (fun x -> { x with Prompt = "Reworded?" }) |> ok
    Assert.Equal(Some { Version = "1"; Hash = v1.Hash }, next.Parent)
    let v2, catalog2 = publish defaultPolicy catalog Set.empty at "author" next |> ok
    Assert.Equal("2", v2.Version)
    Assert.Equal(SupersededState, publishedState catalog2 v1) // still resolvable
    Assert.Equal(Some v1, resolve catalog2 "team-health" "1")
    Assert.Equal(Some v2, resolveHash catalog2 v2.Hash)
    Assert.Equal(Some v1, resolveReference catalog2 (TemplateCanonical.reference v1.SurveyId v1.Version v1.Content))

    // Rollback: derive from v1 and publish the old design as v3.
    let rollback = deriveDraft v1 |> editContent (fun c -> { c with Metadata = { c.Metadata with Tags = [ "rollback" ] } })
    let v3, catalog3 = publish defaultPolicy catalog2 Set.empty at "author" rollback |> ok
    Assert.Equal("3", v3.Version)
    Assert.Equal(Some "1", v3.Parent |> Option.map _.Version)
    Assert.Equal(3, (versionsOf catalog3 "team-health").Length)

    // Hiding keeps a version resolvable.
    let hidden = hide v1 catalog3
    Assert.Equal(HiddenFromDistribution, visibility hidden v1)
    Assert.Equal(Some v1, resolve hidden "team-health" "1")

    // Provenance is outside the hash: another publisher and time, same hash.
    let other, _ = publish defaultPolicy emptyCatalog Set.empty (at.AddDays 1.0) "someone-else" draft |> ok
    Assert.Equal(v1.Hash, other.Hash)

    // A tampered artifact no longer verifies.
    Assert.False(verify { v1 with Content = next.Content })

[<Fact>]
let ``publication refuses blockers, unacknowledged warnings and unknown parents`` () =
    match publish defaultPolicy emptyCatalog Set.empty at "a" { draft with Fixtures = [] } with
    | Error(Blocked report) -> Assert.Contains("FIXTURES-NONE", report.Blockers |> List.map _.Code)
    | other -> failwith $"expected Blocked, got %A{other}"

    let pii = draft |> editQuestion "q1" (fun x -> { x with Prompt = "Enter your email" }) |> ok

    match publish defaultPolicy emptyCatalog Set.empty at "a" pii with
    | Error(UnacknowledgedWarnings ws) -> Assert.Equal<string list>([ "PRIVACY-PII-LIKE-PROMPT" ], ws |> List.map _.Code)
    | other -> failwith $"expected UnacknowledgedWarnings, got %A{other}"

    Assert.True(publish defaultPolicy emptyCatalog (Set [ "PRIVACY-PII-LIKE-PROMPT" ]) at "a" pii |> Result.isOk)

    let orphan = { draft with Parent = Some { Version = "7"; Hash = "sha256:00" } }
    Assert.Equal(Error(UnknownParent { Version = "7"; Hash = "sha256:00" }), publish defaultPolicy emptyCatalog Set.empty at "a" orphan |> Result.map ignore)

[<Fact>]
let ``lifecycle is derived and capabilities follow from it`` () =
    Assert.Equal(ValidatedState, draftState defaultPolicy draft)
    let broken = draft |> editQuestion "q1" (fun x -> { x with Prompt = "" }) |> ok
    Assert.Equal(DraftState, draftState defaultPolicy broken) // editing a validated draft can return it to Draft
    Assert.DoesNotContain(Publish, allowedActions DraftState)
    Assert.Contains(Publish, allowedActions ValidatedState)
    Assert.DoesNotContain(Edit, allowedActions PublishedState)
    Assert.DoesNotContain(Edit, allowedActions SupersededState)
    Assert.Contains(DeriveDraft, allowedActions SupersededState)

[<Fact>]
let ``diff classifies scoring, encoding and presentation impact and comparability`` () =
    let c = draft.Content
    let edit (f: Draft -> Result<Draft, EditError>) = (draft |> f |> ok).Content

    Assert.Empty(diff c c)
    Assert.Equal(Comparable, comparability (diff c c))

    let presentation = { c with Presentation = { c.Presentation with ItemsPerPage = Some 2 } }
    Assert.Equal<Change list>([ PresentationChanged ], diff c presentation)
    Assert.Equal(Comparable, comparability (diff c presentation))

    let reworded = edit (editQuestion "q1" (fun x -> { x with Prompt = "New wording" }))
    Assert.Equal<Change list>([ QuestionWordingChanged "q1" ], diff c reworded)

    match comparability (diff c reworded) with
    | ComparableWithCaution _ -> ()
    | other -> failwith $"%A{other}"

    let rescored = edit (editSection "s" (fun s -> { s with Scoring = Some { Scorer = { meanScorer with Aggregate = Scoring.Median }; Questions = [] } }))
    Assert.Equal<Change list>([ SectionScoringChanged "s" ], diff c rescored)
    Assert.True((impact (SectionScoringChanged "s")).Scoring)

    match comparability (diff c rescored) with
    | NotComparable _ -> ()
    | other -> failwith $"%A{other}"

    let reordered = edit (moveQuestion "q3" "s" 0)
    Assert.Equal<Change list>([ QuestionsReordered ], diff c reordered)
    Assert.True((impact QuestionsReordered).Encoding)
    Assert.False((impact QuestionsReordered).Scoring)

    let added = edit (addQuestion "s" (q "q4"))
    Assert.Contains(QuestionAdded "q4", diff c added)

[<Fact>]
let ``the publication preview reports what publishing would produce`` () =
    let v1, catalog = publish defaultPolicy emptyCatalog Set.empty at "a" draft |> ok
    let next = deriveDraft v1 |> editSection "s" (fun s -> { s with Scoring = Some { Scorer = { meanScorer with Aggregate = Scoring.Median }; Questions = [] } }) |> ok
    let p = preview defaultPolicy catalog next |> ok
    Assert.Equal("2", p.NewVersion)
    Assert.Equal(Some "1", p.ParentVersion)
    Assert.True(p.BreakingScoringChanges)
    Assert.False(p.EncodingChanges)
    Assert.Equal(3, p.QuestionCount)
    Assert.Equal(TemplateCanonical.templateHash "team-health" "2" next.Content, p.TemplateHash)

    match p.Comparability with
    | Some(NotComparable _) -> ()
    | other -> failwith $"%A{other}"
