/// The state chips' tones (SIG-VIS-PENDING-001): every group phase and every
/// template lifecycle maps to the tone the Echelon theme 1.1.0 contract
/// gives it, exhaustively over the unions, so a new case cannot drift past
/// this test; and the lifecycle comes from the stored review, not the view.
module Echelon.Signal.Tests.StateToneTests

open Microsoft.FSharp.Reflection
open Xunit
open Echelon.Signal.Admin

/// Every case of a union without fields.
let private everyCase<'T> () : 'T list =
    [ for case in FSharpType.GetUnionCases typeof<'T> -> FSharpValue.MakeUnion(case, [||]) :?> 'T ]

/// The contract's tones (`.echelon-chip[data-echelon-tone=…]`).
let private contractTones = set [ "draft"; "review"; "published"; "ready"; "progress"; "complete"; "sealed"; "closed" ]

[<Fact>]
let ``every group phase has the contract's tone`` () =
    // The contract: ready (hollow); progress = Importing/Partial (half);
    // review = Reconciling (diamond); complete (filled); sealed =
    // Finalized/FinalizedIncomplete/Sealed (square); closed =
    // ClosedIncomplete/Superseded (muted).
    let expected =
        Map.ofList
            [ "GroupReady", "ready"
              "Importing", "progress"
              "GroupPartial", "progress"
              "Reconciling", "review"
              "GroupComplete", "complete"
              "ClosedIncomplete", "closed"
              "Finalized", "sealed"
              "FinalizedIncomplete", "sealed"
              "Sealed", "sealed"
              "Superseded", "closed" ]

    let phases = everyCase<AdminState.GroupPhase> ()
    Assert.Equal(expected.Count, phases.Length)

    for phase in phases do
        let name = AdminState.phaseName phase
        let tone = StateTones.toneId (StateTones.ofPhase phase)
        Assert.True(expected.ContainsKey name, $"{name} has no expected tone")
        Assert.Equal(expected[name], tone)

[<Fact>]
let ``every template lifecycle has the contract's tone and a visible label`` () =
    let expected =
        [ TemplateListing.Lifecycle.Draft, ("draft", "Draft")
          TemplateListing.Lifecycle.InReview, ("review", "In review")
          TemplateListing.Lifecycle.ReviewedDraft, ("review", "Reviewed")
          TemplateListing.Lifecycle.Published, ("published", "Published")
          // The contract names no tone for a hidden version: the muted one.
          TemplateListing.Lifecycle.Hidden, ("closed", "Hidden") ]
        |> Map.ofList

    let lifecycles = everyCase<TemplateListing.Lifecycle> ()
    Assert.Equal(expected.Count, lifecycles.Length)

    for lifecycle in lifecycles do
        Assert.Equal(expected[lifecycle], (StateTones.toneId (StateTones.ofLifecycle lifecycle), StateTones.lifecycleLabel lifecycle))

[<Fact>]
let ``every tone is one the contract defines, and each is distinct`` () =
    let tones = everyCase<StateTones.Tone> () |> List.map StateTones.toneId
    Assert.Equal<Set<string>>(contractTones, set tones)
    Assert.Equal(tones.Length, (List.distinct tones).Length)

[<Fact>]
let ``a draft's lifecycle comes from its stored review, and a version's from its visibility`` () =
    let expected =
        [ TemplateListing.NotSubmitted, TemplateListing.Lifecycle.Draft
          TemplateListing.ChangedSinceSubmitted, TemplateListing.Lifecycle.Draft
          TemplateListing.ReadyForReview, TemplateListing.Lifecycle.InReview
          TemplateListing.Reviewed, TemplateListing.Lifecycle.ReviewedDraft ]
        |> Map.ofList

    let states = everyCase<TemplateListing.ReviewState> ()
    Assert.Equal(expected.Count, states.Length)

    for state in states do
        Assert.Equal(expected[state], TemplateListing.draftLifecycle state)
        // The words the console already showed are unchanged.
        Assert.False(System.String.IsNullOrEmpty(TemplateListing.reviewText state))

    let row hidden : TemplateListing.Row =
        { SurveyId = "PULSE"; Version = "1"; Hash = "sha256:00"; Title = "Team pulse"; Hidden = hidden; Superseded = false; NotForGroups = None }

    Assert.Equal(TemplateListing.Lifecycle.Published, TemplateListing.versionLifecycle (row false))
    Assert.Equal(TemplateListing.Lifecycle.Hidden, TemplateListing.versionLifecycle (row true))
    // A survey with no stored draft (one being started) is a draft.
    Assert.Equal(TemplateListing.Lifecycle.Draft, TemplateListing.lifecycleOfDraft TemplateListing.empty "PULSE")
