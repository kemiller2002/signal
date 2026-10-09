/// Test and preview artifacts (AUT-006 §§21, 60-61): a test link is marked
/// in its envelope, the survey page says so, what it submits stays marked,
/// and production import refuses it. Only an import explicitly opened as a
/// test environment reads it.
module Echelon.Signal.Tests.TestModeTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Tests.SurveyPageTests

let private demo = SurveyPageTests.demo
let private form = GenericImport.formOf DemoSurvey DemoVersion demo
let private bytes = publishedFile DemoSurvey DemoVersion demo |> snd
let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private instance = opaque 1
let private group = opaque 40

let private testLink mode answers =
    "#r=" + GenericEnvelope.testLink DemoSurvey DemoVersion demo mode instance group answers

let private opened link =
    match start link |> received (Found bytes) with
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private answered (r: Response) =
    Template.questions demo
    |> List.map snd
    |> List.fold (fun session q -> update (Chose(q.Id, fst (choices q).Head)) session) (Responding r)

let private submitted mode =
    match opened (testLink mode Map.empty) |> answered |> update (SubmitRequested(Array.init 16 (fun i -> byte (100 + i)))) with
    | Responding r when r.Phase = Submitted -> r
    | other -> failwith $"%A{other}"

let private definition mode : Import.GroupDefinition =
    { Group = group
      Mode = mode
      ExpectedCount = 1
      Template = GenericImport.shapeOf DemoSurvey DemoVersion demo
      Generic = Some form }

[<Fact>]
let ``a test link is marked in the envelope and only there`` () =
    let marked = (GenericEnvelope.referenceIn (testLink Import.IdentifiedGroup Map.empty |> fun l -> l.Substring 3))
    Assert.Equal<byte[]>(form.Reference, Result.defaultValue [||] marked)
    let r = opened (testLink Import.IdentifiedGroup Map.empty)
    Assert.Equal(Test(IdentifiedInvitation(instance, group)), r.Binding)
    // The production invitation for the same ids is a different link.
    Assert.NotEqual<string>(GenericEnvelope.invitation DemoSurvey DemoVersion demo Import.IdentifiedGroup instance group, (testLink Import.IdentifiedGroup Map.empty).Substring 3)
    Assert.Equal(Test(Unbound), asTest (asTest Unbound))
    Assert.Equal(Identified(instance, group), production (Test(Identified(instance, group))))

[<Fact>]
let ``the survey page says a test link is a test`` () =
    let isTestOf session =
        View.Value(View.Flag true) = (GenericSessionView.view session |> List.find (fst >> (=) "isTest") |> snd)

    Assert.True(isTestOf (Responding(opened (testLink Import.AnonymousGroup Map.empty))))
    Assert.False(isTestOf (start "#r=x"))

[<Fact>]
let ``what a test link submits stays a test artifact`` () =
    Assert.Equal(Test(Identified(instance, group)), (submitted Import.IdentifiedGroup).Binding)

    match (submitted Import.AnonymousGroup).Binding with
    | Test(Anonymous(fresh, g)) ->
        Assert.Equal(group, g)
        Assert.NotEqual(instance, fresh)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``production import refuses a test submission, and a test environment reads it`` () =
    for mode in [ Import.IdentifiedGroup; Import.AnonymousGroup ] do
        let link = "https://signal.example/web/survey/" + fragment (submitted mode)
        Assert.Equal(Import.Rejected Import.TestSubmission, GenericImport.evaluate (definition mode) (fun _ -> None) link)
        Assert.Equal("rejected:test-submission", Import.outcomeCode (GenericImport.evaluate (definition mode) (fun _ -> None) link))

        match GenericImport.evaluateIn GenericImport.TestEnvironment (definition mode) form (fun _ -> None) link with
        | Import.Accepted accepted -> Assert.Equal(5, accepted.AnsweredCount)
        | other -> failwith $"%A{other}"

[<Fact>]
let ``a live test link is not a submission in either environment`` () =
    let link = "https://signal.example/web/survey/" + testLink Import.IdentifiedGroup Map.empty
    Assert.Equal(Import.Rejected Import.TestSubmission, GenericImport.evaluate (definition Import.IdentifiedGroup) (fun _ -> None) link)
    Assert.Equal(Import.Rejected Import.NotFinalized, GenericImport.evaluateIn GenericImport.TestEnvironment (definition Import.IdentifiedGroup) form (fun _ -> None) link)

let private published fixtures : Publication.Published =
    { SurveyId = DemoSurvey
      Version = DemoVersion
      Hash = form.Hash
      Content = demo
      Parent = None
      PublishedAt = DateTimeOffset.UnixEpoch
      PublishedBy = "author"
      Manifest = []
      Fixtures = fixtures }

[<Fact>]
let ``the console's test links open the survey page as tests, one per fixture`` () =
    let fixture: Drafts.Fixture =
        { Id = "all-yes"
          Name = "Everything yes"
          Answers = Map.ofList [ "CTX-001", Value(Flag true) ]
          Expect = [] }

    let shown = Echelon.Signal.Admin.TestLinks.forVersion (published [ fixture ])
    Assert.Equal<string list>([ "Blank, identified"; "Blank, anonymous"; "Fixture: Everything yes" ], shown.Links |> List.map fst)

    for _, href in shown.Links do
        Assert.StartsWith("../survey/#r=", href)
        Assert.True(isTest (opened (href.Substring "../survey/".Length)).Binding)

    let fixtureLink = shown.Links |> List.last |> snd
    Assert.Equal(Some(Value(Flag true)), (opened (fixtureLink.Substring "../survey/".Length)).Answers.TryFind "CTX-001")
    // Deterministic: the same version gives the same links.
    Assert.Equal<(string * string) list>(shown.Links, (Echelon.Signal.Admin.TestLinks.forVersion (published [ fixture ])).Links)
