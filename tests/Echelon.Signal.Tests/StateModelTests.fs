/// ARX-002 (WI-0092): every state system Signal exposes is held to the same
/// rules. Every command has an explicit result; a command whose capability
/// the state does not grant is refused; capabilities come from the shared
/// vocabulary; a stable (terminal) state accepts no command; every
/// obligation names the evidence that discharges it.
module Echelon.Signal.Tests.StateModelTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.StateModel
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession
open Echelon.Signal.Admin

/// The shared properties, over sample states and commands.
let holds (system: System<'s, 'c>) (states: 's list) (commands: 'c list) =
    Assert.NotEmpty states
    Assert.NotEmpty commands

    for s in states do
        let caps = system.Capabilities s
        Assert.True(Set.isSubset caps Capability.all, $"{system.Name} {system.StateName s}: %A{Set.difference caps Capability.all}")

        for o in system.Obligations s do
            Assert.False(String.IsNullOrWhiteSpace o.Code || String.IsNullOrWhiteSpace o.DischargedBy, $"{system.Name}: %A{o}")

        for c in commands do
            let where = $"{system.Name} {system.StateName s} {system.CommandName c}"

            match system.Attempt c s with
            | Error illegal ->
                Assert.Equal(system.Name, illegal.System)
                Assert.Equal(system.StateName s, illegal.From)
                Assert.Equal(system.CommandName c, illegal.Command)
                Assert.False(String.IsNullOrWhiteSpace illegal.Reason, where)
                Assert.StartsWith("SIGNAL.", illegal.Code)
            | Ok _ ->
                Assert.False(system.Terminal s, $"{where}: a stable state moved")

                match system.Requires c with
                | Some capability -> Assert.True(caps.Contains capability, $"{where}: legal without {capability}")
                | None -> ()

// ---- The respondent session ---------------------------------------------------------------------

let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private day = DateOnly(2026, 1, 1)
let private link surveyId (content: Content) = "#r=" + GenericEnvelope.invitation surveyId "1" content Import.IdentifiedGroup (opaque 1) (opaque 40)
let private opened surveyId content = start (link surveyId content) |> received day (Found(snd (publishedFile surveyId "1" content)))
let private first (q: Question) = choices q |> List.head |> fst

let private answeredAll (content: Content) (session: Session) =
    questions content |> List.fold (fun s (_, q) -> update (Chose(q.Id, first q)) s) session

let private demo = SurveyPageTests.demo
let private paged = SurveyPageTests.paged

let private sessions =
    let answered = opened "signal-demo" demo |> answeredAll demo

    [ start (link "signal-demo" demo)
      start (link "signal-demo" demo) |> received day NotFound
      start (link "signal-demo" demo) |> received day (Unreachable "offline")
      start ""
      opened "signal-demo" demo
      answered
      update (SubmitRequested(Array.create 16 7uy)) answered
      // Paged: the first section is locked once left (LockPreviousSectionsAfterExit).
      opened "PAGED" paged |> answeredAll paged |> update (Turned GenericPaging.Next) |> update (Turned GenericPaging.Next) ]

let private messages =
    let qs = questions demo |> List.map snd

    [ for q in qs do
          Chose(q.Id, first q)
      Chose(qs.Head.Id, Value(Point 99))
      Chose("NOPE", Value(Point 1))
      Toggled(qs.Head.Id, "x", true)
      Numbered(qs.Head.Id, "n", "1")
      Picked(qs.Head.Id, GenericInputs.Best, "x")
      Ticked(DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
      Turned GenericPaging.Back
      Turned GenericPaging.Next
      Reached 1
      SubmitRequested(Array.create 16 7uy) ]

[<Fact>]
let ``the respondent session is a state system with explicit results`` () =
    holds SessionStates.system sessions messages

[<Fact>]
let ``an illegal message is exactly what the page's reducer ignores, and a legal one is what it does`` () =
    for s in sessions do
        for m in messages do
            let after = update m s

            match SessionStates.attempt m s, s, after with
            | Ok next, _, _ -> Assert.True((next = after), $"{SessionStates.stateName s} {SessionStates.commandName m}")
            | Error _, Responding r, Responding r2 ->
                Assert.Equal(r.Phase, r2.Phase)
                Assert.Equal<Answers>(r.Answers, r2.Answers)
                Assert.Equal(r.Position, r2.Position)
            | Error _, before, after -> Assert.Equal(SessionStates.stateName before, SessionStates.stateName after)

[<Fact>]
let ``the session's capabilities, obligations and template knowledge follow its state`` () =
    let fresh = opened "signal-demo" demo
    let answered = fresh |> answeredAll demo
    let submitted = update (SubmitRequested(Array.create 16 7uy)) answered

    Assert.Contains(Capability.CanAnswer, SessionStates.capabilities fresh)
    Assert.DoesNotContain(Capability.CanFinalize, SessionStates.capabilities fresh)
    Assert.Contains("MissingRequiredAnswer", SessionStates.obligations fresh |> List.map _.Code)
    Assert.Contains(Capability.CanFinalize, SessionStates.capabilities answered)
    Assert.Empty(SessionStates.obligations answered)
    Assert.Equal<Set<string>>(set [ Capability.CanCopySubmission ], SessionStates.capabilities submitted)
    Assert.True(SessionStates.terminal submitted)

    let waiting = start (link "signal-demo" demo)
    Assert.Equal<string list>([ "ResolveTemplate" ], SessionStates.obligations waiting |> List.map _.Code)
    Assert.Equal(Known("signal-demo", "1"), SessionStates.template fresh)

    // Searched, not found, names the file and where it was looked for; an
    // unreachable catalog is unknown, not absent.
    match SessionStates.template (waiting |> received day NotFound) with
    | SearchedNotFound(file, scope) ->
        Assert.StartsWith("published-templates/", file)
        Assert.Contains("published catalog", scope)
    | other -> failwith $"%A{other}"

    match SessionStates.template (waiting |> received day (Unreachable "offline")) with
    | Unknown reason -> Assert.Contains("offline", reason)
    | other -> failwith $"%A{other}"

    // Refusals name why.
    match SessionStates.attempt (SubmitRequested [||]) fresh with
    | Error illegal ->
        Assert.Contains("CanFinalize", illegal.Reason)
        Assert.Equal("SIGNAL.SESSION.ILLEGAL_TRANSITION", illegal.Code)
    | Ok _ -> failwith "an unfinished response was finalized"

    match SessionStates.attempt (Chose("Q1", Value(Point 1))) submitted with
    | Error illegal -> Assert.Contains("sealed", illegal.Reason)
    | Ok _ -> failwith "a sealed response changed"

// ---- Group aggregation --------------------------------------------------------------------------

let private clean: GroupLifecycle.Evidence =
    { Complete = true; UnreconciledBatches = 0; RecordProblems = 0; IndexCurrent = true; MinimumReportableCount = 5 }

let private blocked = { clean with UnreconciledBatches = 2; IndexCurrent = false }

let private groupStates =
    [ for status in [ GroupLifecycle.Collecting; GroupLifecycle.ClosedIncomplete; GroupLifecycle.Finalized; GroupLifecycle.FinalizedIncomplete; GroupLifecycle.Sealed; GroupLifecycle.Superseded(opaque 9) ] do
          for evidence in [ clean; blocked ] -> ({ Status = status; Evidence = evidence }: GroupLifecycle.GroupState) ]

let private transitions =
    [ GroupLifecycle.Close; GroupLifecycle.Reopen; GroupLifecycle.Finalize clean; GroupLifecycle.Seal; GroupLifecycle.Supersede(opaque 10) ]

[<Fact>]
let ``group aggregation is a state system with explicit results`` () =
    holds GroupLifecycle.system groupStates transitions

[<Fact>]
let ``a group's open obligations block finalization and name what discharges them`` () =
    let state: GroupLifecycle.GroupState = { Status = GroupLifecycle.Collecting; Evidence = blocked }
    let open' = GroupLifecycle.system.Obligations state
    Assert.Equal<string list>([ "ReconcileUnknownWrite"; "RebuildDerivedProjection" ], open' |> List.map _.Code)
    Assert.Contains("reconciling", open'.Head.DischargedBy)
    Assert.DoesNotContain(Capability.CanFinalize, GroupLifecycle.system.Capabilities state)

    match GroupLifecycle.system.Attempt (GroupLifecycle.Finalize clean) state with
    | Error illegal -> Assert.Contains("ReconcileUnknownWrite", illegal.Reason)
    | Ok _ -> failwith "finalized with open obligations"

    // The evidence discharges them: finalization is then legal.
    let discharged = { state with Evidence = clean }
    Assert.Empty(GroupLifecycle.system.Obligations discharged)
    Assert.True(GroupLifecycle.system.Attempt (GroupLifecycle.Finalize blocked) discharged |> Result.isOk)
