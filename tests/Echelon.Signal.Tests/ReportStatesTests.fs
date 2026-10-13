/// ARX-002 slice 3 (WI-0092): report-state persistence and report snapshot
/// generation held to the shared state-system properties, with their
/// unknown and searched-not-found knowledge.
module Echelon.Signal.Tests.ReportStatesTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Engine.StateModel
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin
open Echelon.Signal.Admin.ReportStates
open Echelon.Signal.Tests.StateModelTests

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

// ---- Persistence --------------------------------------------------------------------------------

let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value

let private reportState =
    let definition: GroupDefinition = { Group = opaque 200; Mode = IdentifiedGroup; ExpectedCount = 12; Template = Pilot.assessment; Generic = None }
    ReportState.project { MinimumReportableCount = 3 } (Incremental.empty definition)

let private roomy = ReportState.defaultBudget
let private tight = { ReportState.defaultBudget with MaximumUrlLength = 600 }

let private persistenceStates =
    [ Placing reportState
      Embedded "a=x"
      CheckingStore("rs-1", "s=rs-1")
      ReadyToWrite("rs-1", "s=rs-1")
      Writing("rs-1", "s=rs-1")
      WriteOutcomeUnknown("rs-1", "s=rs-1")
      WriteFailed("rs-1", "s=rs-1", "SIGNAL.STORAGE.RATE_LIMITED")
      Stored("rs-1", "s=rs-1")
      ErasedInStore "rs-1" ]

let private persistenceCommands =
    [ Place roomy; Place tight; ObserveRead FoundInStore; ObserveRead AbsentFromStore; ObserveRead ErasedFromStore; Commit
      ObserveCommit Landed; ObserveCommit(Failed "x"); ObserveCommit OutcomeUnknown; Reconcile ]

[<Fact>]
let ``report-state persistence is a state system with explicit results`` () =
    holds persistenceSystem persistenceStates persistenceCommands

let private run commands start =
    commands |> List.fold (fun state command -> state |> Result.bind (persistenceAttempt command)) (Ok start)

[<Fact>]
let ``a state that fits travels in the URL; one that does not is read, written once, and an unknown write is reconciled`` () =
    Assert.True(match run [ Place roomy ] (Placing reportState) with Ok(Embedded _) -> true | _ -> false)

    // Too large for a tight URL: read first; absent, so write.
    let unsure = run [ Place tight; ObserveRead AbsentFromStore; Commit; ObserveCommit OutcomeUnknown ] (Placing reportState) |> ok
    Assert.Equal("WriteOutcomeUnknown", persistenceStateName unsure)
    Assert.Equal<string list>([ "ReconcileUnknownPersistenceOutcome" ], persistenceObligations unsure |> List.map _.Code)
    Assert.True(match stored unsure with Unknown reason -> reason.Contains "not failed" | _ -> false)

    // Sending again before reconciling is refused, with the reason.
    match persistenceAttempt Commit unsure with
    | Error illegal -> Assert.Contains("reconcile before sending again", illegal.Reason)
    | Ok _ -> failwith "an unknown write was sent again"

    // Reconciling reads again: found means it landed.
    let landed = run [ Reconcile; ObserveRead FoundInStore ] unsure |> ok
    Assert.True(match stored landed with Known id -> id.StartsWith "rs-" | _ -> false)
    Assert.Contains(Capability.CanRenderFinalReport, persistenceCapabilities landed)

    // An erased record is not written again.
    let erased = run [ Place tight; ObserveRead ErasedFromStore ] (Placing reportState) |> ok
    Assert.True(match stored erased with SearchedNotFound(_, scope) -> scope.Contains "erased" | _ -> false)
    Assert.True(persistenceAttempt Commit erased |> Result.isError)

// ---- Snapshots ----------------------------------------------------------------------------------

let private content = Pilot.content
let private hash = TemplateCanonical.templateHash "SDRA" "1" content
let private sdra: ReportLibrary.TemplateRef = { SurveyId = "SDRA"; Version = "1"; Hash = hash }

let private published: Publication.Published =
    { SurveyId = "SDRA"; Version = "1"; Hash = hash; Content = content; Parent = None; PublishedAt = DateTimeOffset.UnixEpoch; PublishedBy = "author"; Manifest = []; Fixtures = [] }

let private catalog = { Publication.emptyCatalog with Templates = [ published ] }

let private respondent (a: int) : GroupResult.Contribution =
    let answers = Pilot.assessment.Items |> List.map (fun i -> i.Id, Value(Point a)) |> Map.ofList
    { Role = None; Result = SurveyResult.compute hash content answers true }

let private group = GroupResult.aggregate hash IdentifiedGroup 6 5 [ for a in [ 4; 3; 4; 2; 4 ] -> respondent a ]
let private subject: Report.Subject = { SurveyId = "SDRA"; TemplateVersion = "1"; Content = content }
let private definition = { ReportExport.administratorGroup with Id = "quarterly" }
let private library, entry = ReportLibrary.save ReportLibrary.empty definition (ReportLibrary.currentPins (ReportLibrary.ExactTemplate sdra)) |> ok
let private report = Report.build entry.Definition subject 5 group [] DateTimeOffset.UnixEpoch |> ok

let private source: ReportLibrary.Source =
    { GroupId = "g-1"
      GroupResultHash = group.Hash
      ReportStateHash = "sha256:" + String('a', 64)
      GroupTemplate = sdra
      Report = report
      Locale = "en-US"
      ComparisonReferences = []
      Mode = IdentifiedGroup
      MinimumReportable = 5
      Clock = None }

let private start: Snapshots = { Library = library; Catalog = catalog; Taken = [] }
let private taken = snapshotAttempt (Take source) start |> ok
let private snapshotId = taken.Taken.Head.SnapshotId

/// The same snapshot when its template has left the catalog.
let private orphaned = { taken with Catalog = Publication.emptyCatalog }

[<Fact>]
let ``snapshot generation is a state system with explicit results`` () =
    holds snapshotSystem [ start; taken; orphaned ] [ Take source; Take { source with GroupTemplate = { sdra with Version = "2" } }; Open snapshotId; Open "snap-none" ]

[<Fact>]
let ``a snapshot whose template is gone is unresolved, owes its dependency, and its template is searched-not-found`` () =
    Assert.Contains(Capability.CanRenderFinalReport, snapshotCapabilities taken)
    Assert.True(snapshotAttempt (Open snapshotId) taken |> Result.isOk)

    Assert.DoesNotContain(Capability.CanRenderFinalReport, snapshotCapabilities orphaned)
    Assert.Equal<string list>([ "ResolveSnapshotDependency" ], snapshotObligations orphaned |> List.map _.Code)

    match snapshotAttempt (Open snapshotId) orphaned with
    | Error illegal -> Assert.Contains("template SDRA 1", illegal.Reason)
    | Ok _ -> failwith "an unresolved snapshot opened"

    match snapshotTemplate orphaned orphaned.Taken.Head with
    | SearchedNotFound(what, scope) ->
        Assert.StartsWith("SDRA 1", what)
        Assert.Contains("0 published version", scope)
    | other -> failwith $"%A{other}"

    // A pin the group was not scored with is refused, explicitly.
    match snapshotAttempt (Take { source with GroupTemplate = { sdra with Version = "2"; Hash = "sha256:" + String('b', 64) } }) start with
    | Error illegal -> Assert.Contains("scored with SDRA 2", illegal.Reason)
    | Ok _ -> failwith "a mismatched pin was taken"
