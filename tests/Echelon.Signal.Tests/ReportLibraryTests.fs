/// Saved report definitions, formal snapshots and exports (WI-0050):
/// ADM-021 versioning, ADM-022 immutable snapshots that never read current
/// state, ADM-023 exports with lineage, ADM-025 no locators or credentials in
/// exports, ADM-063 exact dependency pins; and their storage through Arca.
module Echelon.Signal.Tests.ReportLibraryTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.ReportModel
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.ReportLibrary
open Echelon.Signal.Application
open Echelon.Signal.Tests.StoreFixture

let private content = Pilot.content
let private hash = TemplateCanonical.templateHash "SDRA" "1" content
let private sdra = { SurveyId = "SDRA"; Version = "1"; Hash = hash }

let private published: Publication.Published =
    { SurveyId = "SDRA"; Version = "1"; Hash = hash; Content = content; Parent = None; PublishedAt = at; PublishedBy = "github:583231"; Manifest = []; Fixtures = [] }

let private catalog = { Publication.emptyCatalog with Templates = [ published ] }

let private respondent (a: int) : GroupResult.Contribution =
    let answers = Pilot.assessment.Items |> List.map (fun i -> i.Id, Value(Point a)) |> Map.ofList
    { Role = None; Result = SurveyResult.compute hash content answers true }

let private group = GroupResult.aggregate hash AnonymousGroup 6 5 [ for a in [ 4; 3; 4; 2; 4 ] -> respondent a ]
let private subject: Report.Subject = { SurveyId = "SDRA"; TemplateVersion = "1"; Content = content }
let private definition = { ReportExport.anonymousAggregate with Id = "quarterly" }

let private reportWith (d: Definition) generatedAt =
    Report.build d subject 5 group [] generatedAt |> Result.toOption |> Option.get

let private source (report: ReportData) clock : Source =
    { GroupId = "g-7f3a"
      GroupResultHash = group.Hash
      ReportStateHash = "sha256:" + String('a', 64)
      GroupTemplate = sdra
      Report = report
      Locale = "en-US"
      ComparisonReferences = []
      Clock = clock }

let private saved pins =
    ReportLibrary.save ReportLibrary.empty definition pins |> ok

[<Fact>]
let ``an unused version is replaced, a used version never changes and editing it creates the next`` () =
    let library, first = saved (currentPins (ExactTemplate sdra))
    Assert.Equal(1, first.Definition.Version)

    let library, again = ReportLibrary.save library { definition with Decimals = 2 } first.Pins |> ok
    Assert.Equal(1, again.Definition.Version)
    Assert.Equal(1, (versions library "quarterly").Length)

    let _, used = ReportLibrary.take library catalog (source (reportWith again.Definition at) None) |> ok
    let library, next = ReportLibrary.save used { definition with Decimals = 0 } first.Pins |> ok
    Assert.Equal(2, next.Definition.Version)
    Assert.Equal(2, (resolve library "quarterly" 1 |> Option.get).Definition.Decimals)
    Assert.True((resolve library "quarterly" 1 |> Option.get).Used)

    match ReportLibrary.save library { definition with Blocks = [] } first.Pins with
    | Error(InvalidDefinition [ Report.NoBlocks ]) -> ()
    | other -> failwith $"%A{other}"

    match ReportLibrary.save library { definition with Id = "Quarterly Report" } first.Pins with
    | Error(InvalidId _) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a snapshot resolves latest to the exact template, and an unresolvable or different pin is refused`` () =
    let library, entry = saved (currentPins (LatestTemplate "SDRA"))
    let report = reportWith entry.Definition at
    let snapshot, _ = ReportLibrary.take library catalog (source report None) |> ok
    Assert.Equal(sdra, snapshot.Template)

    match ReportLibrary.take library Publication.emptyCatalog (source report None) with
    | Error(TemplateUnresolved "SDRA") -> ()
    | other -> failwith $"%A{other}"

    let other = { sdra with Version = "2"; Hash = "sha256:" + String('b', 64) }

    match ReportLibrary.take library catalog { source report None with GroupTemplate = other } with
    | Error(TemplateMismatch(pinned, group)) -> Assert.Equal((sdra, other), (pinned, group))
    | failed -> failwith $"%A{failed}"

    let stale, _ = saved { currentPins (ExactTemplate sdra) with AggregateSchema = 1 }

    match ReportLibrary.take stale catalog (source report None) with
    | Error(SchemaUnsupported("aggregate", 1, _)) -> ()
    | failed -> failwith $"%A{failed}"

[<Fact>]
let ``the clock never changes a snapshot's identity, and opening one never reads current state`` () =
    let library, entry = saved (currentPins (ExactTemplate sdra))
    let first, used = ReportLibrary.take library catalog (source (reportWith entry.Definition at) (Some at)) |> ok
    let later, _ = ReportLibrary.take library catalog (source (reportWith entry.Definition (at.AddDays 3.0)) (Some(at.AddDays 3.0))) |> ok
    Assert.Equal(first.SnapshotId, later.SnapshotId)
    Assert.Equal(first.CanonicalReportDataHash, later.CanonicalReportDataHash)
    Assert.True(intact first)

    Assert.Equal(Resolved first.ReportData, openSnapshot used catalog first)

    match openSnapshot ReportLibrary.empty Publication.emptyCatalog first with
    | Unresolved missing -> Assert.Equal<Missing list>([ MissingDefinition("quarterly", 1); MissingTemplate sdra ], missing)
    | failed -> failwith $"%A{failed}"

    match openSnapshot used catalog { first with ReportData = first.ReportData.Replace("\"accepted\":5", "\"accepted\":6") } with
    | Unresolved [ DataAltered ] -> ()
    | failed -> failwith $"%A{failed}"

[<Fact>]
let ``exports come from the snapshot's report data, carry lineage, and never a locator or credential`` () =
    let library, entry = saved (currentPins (ExactTemplate sdra))
    let report = reportWith entry.Definition at
    let snapshot, _ = ReportLibrary.take library catalog { source report None with ComparisonReferences = [ "baseline-2026q2" ] } |> ok
    let files = ReportLibrary.export snapshot report |> ok |> Map.ofList

    Assert.Equal<string list>([ "lineage.json"; "report.json"; "sections.csv" ], files |> Map.keys |> List.ofSeq)
    Assert.Equal(ReportExport.sectionsCsv report, files["sections.csv"])
    Assert.Contains(ReportExport.json report, files["report.json"])

    for expected in [ "\"surveyId\":\"SDRA\""; "\"groupId\":\"g-7f3a\""; "\"definitionVersion\":1"; "\"snapshotId\":\"" + snapshot.SnapshotId; "\"comparisons\":[\"baseline-2026q2\"]"; "\"resultSchema\":1" ] do
        Assert.Contains(expected, files["lineage.json"])

    // ADM-025: the dataset's storage location and the sign-in never reach an export.
    for file in files.Values do
        for secret in [ "acme"; "signal-data"; "583231"; "octocat"; "token"; "Iv23li" ] do
            Assert.DoesNotContain(secret, file)

    match ReportLibrary.export snapshot { report with Counts = { report.Counts with Accepted = 9 } } with
    | Error ReportIsNotTheSnapshot -> ()
    | failed -> failwith $"%A{failed}"

// ---- Through Arca ------------------------------------------------------------------------------

let private publishSdra (opened: Store.Opened) =
    let fixture: Drafts.Fixture =
        { Id = "all-fours"
          Name = "Every answer is 4"
          Answers = Pilot.assessment.Items |> List.map (fun i -> i.Id, Value(Point 4)) |> Map.ofList
          Expect = [ Drafts.ExpectComplete true ] }

    let draft: Drafts.Draft = { SurveyId = "SDRA"; Parent = None; Content = content; Fixtures = [ fixture ] }
    let warnings = (Validation.validate Validation.defaultPolicy draft).Warnings |> List.map _.Code |> Set.ofList
    TemplateStore.publish (actor octocat) Validation.defaultPolicy warnings at opened draft |> run |> ok

[<Fact>]
let ``a snapshot and its used definition are stored in one commit and read back intact`` () =
    let github, admin, _ = dataset Grants.analyst
    let template = publishSdra admin
    let pinned = { SurveyId = template.SurveyId; Version = template.Version; Hash = template.Hash }
    let pinnedGroup = GroupResult.aggregate template.Hash AnonymousGroup 6 5 [ for a in [ 4; 3; 4; 2; 4 ] -> { Role = None; Result = SurveyResult.compute template.Hash content (Pilot.assessment.Items |> List.map (fun i -> i.Id, Value(Point a)) |> Map.ofList) true } ]

    let entry = ReportStore.saveDefinition (actor octocat) at admin definition (currentPins (LatestTemplate "SDRA")) |> run |> ok
    let report = Report.build entry.Definition subject 5 pinnedGroup [] at |> Result.toOption |> Option.get
    let before = github.State.History.Length

    let snapshot =
        ReportStore.takeSnapshot (actor octocat) at admin { source report (Some at) with GroupTemplate = pinned; GroupResultHash = pinnedGroup.Hash } |> run |> ok

    Assert.Equal(before + 1, github.State.History.Length)

    let stored = ReportStore.load at admin |> run |> ok
    Assert.Empty(stored.Problems)
    Assert.Equal<Snapshot list>([ snapshot ], stored.Snapshots)
    Assert.True((resolve stored.Library "quarterly" 1 |> Option.get).Used)
    Assert.Equal(Resolved snapshot.ReportData, openSnapshot stored.Library stored.Catalog snapshot)

    // Editing the used definition stores version 2 beside it.
    let next = ReportStore.saveDefinition (actor octocat) at admin { definition with Decimals = 0 } entry.Pins |> run |> ok
    Assert.Equal(2, next.Definition.Version)
    Assert.Equal(2, (ReportStore.load at admin |> run |> ok).Library["quarterly"].Length)

[<Fact>]
let ``building reports and exporting need their own capabilities`` () =
    let _, admin, editor = dataset Grants.draftEditor

    match ReportStore.saveDefinition (actor hubot) at editor definition (currentPins (LatestTemplate "SDRA")) |> run with
    | Error(ReportStore.NotStored(GroupStore.NotPermitted _)) -> ()
    | failed -> failwith $"%A{failed}"

    let library, entry = saved (currentPins (ExactTemplate sdra))
    let report = reportWith entry.Definition at
    let snapshot, _ = ReportLibrary.take library catalog (source report None) |> ok

    match ReportStore.export (actor hubot) editor snapshot report with
    | Error(ReportStore.NotStored(GroupStore.NotPermitted _)) -> ()
    | failed -> failwith $"%A{failed}"

    Assert.True(ReportStore.export (actor octocat) admin snapshot report |> Result.isOk)
