/// The reports area's work (WI-0075): reading the dataset's formal
/// snapshots, taking one of the report in view, and handing over a
/// snapshot's export files.
///
/// A snapshot is of what the page shows (`Releases.shown`, so the release
/// ledger holds) and of a definition pinned to the group's exact template:
/// each report family has one saved definition per template, saved the first
/// time it is used.
module Echelon.Signal.Application.ReportWork

open System
open System.Globalization
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Admin.ReportLibrary
open Echelon.Signal.Application.AdminWork

let private summaryOf (s: Snapshot) : AdminTypes.SnapshotSummary =
    { SnapshotId = s.SnapshotId
      GroupKey = s.GroupId
      DefinitionId = s.DefinitionId
      DefinitionVersion = s.DefinitionVersion
      Accepted = s.Accepted
      Locale = s.Locale
      TakenAt = s.GeneratedAtEvidence |> Option.map (fun at -> at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) |> Option.defaultValue "not recorded"
      Intact = intact s }

/// Reads the snapshots; the wire keeps them, the page sees summaries.
let load (now: DateTimeOffset) (opened: Store.Opened) =
    async {
        match! ReportStore.load now opened with
        | Ok stored ->
            return
                [ SnapshotsReady stored.Snapshots
                  ToEngine(AdminApp.SnapshotsLoaded(stored.Snapshots |> List.map summaryOf))
                  ToEngine(AdminApp.LibraryLoaded stored.Library) ]
        | Error failure -> return [ ToEngine(groupFailure failure) ]
    }

let private slug (text: string) =
    text.ToLowerInvariant() |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '-')

/// The definition a family uses for one template, pinned to it exactly.
let private definitionFor (family: ReportModel.Definition) (template: TemplateRef) =
    let id = (family.Id + "-" + slug template.SurveyId + "-" + slug template.Version)
    { family with Id = (if id.Length > 64 then id.Substring(0, 64) else id) }, currentPins (ExactTemplate template)

let private failure (f: ReportStore.ReportFailure) =
    match f with
    | ReportStore.NotStored failure -> groupFailure failure
    | ReportStore.SnapshotRefused(WouldRevealDifference(_, difference)) ->
        AdminApp.Failed(notice "SIGNAL.REPORT.WOULD_REVEAL_DIFFERENCE" $"A snapshot now would differ from an earlier one by {difference} response(s), which could single them out. Take it once more responses have arrived." false)
    | ReportStore.SnapshotRefused problem -> AdminApp.Failed(notice "SIGNAL.REPORT.SNAPSHOT_REFUSED" $"The snapshot was refused: %A{problem}" false)
    | ReportStore.LibraryRefused problem -> AdminApp.Failed(notice "SIGNAL.REPORT.DEFINITION_REFUSED" $"The report definition was refused: %A{problem}" false)
    | ReportStore.ExportRefused _ -> AdminApp.Failed(notice "SIGNAL.REPORT.EXPORT_REFUSED" "The snapshot's stored data does not match what it records; it cannot be exported." false)

/// Takes a formal snapshot of the report in view.
let take (catalog: TemplateCatalog.Loaded) (actor: Store.Actor) (model: AdminApp.Model) (group: GroupStore.OpenedGroup) (family: string) (locale: string) (now: DateTimeOffset) =
    let opened = group.Dataset
    let key = GroupRecord.groupKey group.Config.Group

    async {
        match model.Dataset |> Option.bind (fun d -> d.Groups |> List.tryFind (fun g -> g.Key = key)), AdminReportView.definitionOf model family with
        | Some summary, Some chosen ->
            let template = { SurveyId = group.Config.SurveyIdentifier; Version = group.Config.TemplateVersion; Hash = group.Config.TemplateHash }

            match! ReportStore.load now opened with
            | Error f -> return [ ToEngine(groupFailure f) ]
            | Ok reports ->
                // A family gets a definition pinned to this template; a saved definition keeps its own pins.
                let definition, pins =
                    match latest reports.Library family with
                    | Some saved when not (ReportExport.families |> List.exists (fun d -> d.Id = family)) -> saved.Definition, saved.Pins
                    | _ -> definitionFor chosen template

                let same (e: Entry) = { e.Definition with Version = definition.Version } = definition && e.Pins = pins

                let! saved =
                    match latest reports.Library definition.Id with
                    | Some entry when same entry -> async.Return(Ok entry)
                    | _ -> ReportStore.saveDefinition actor now opened definition pins

                match saved with
                | Error f -> return [ ToEngine(failure f) ]
                | Ok entry ->
                    match AdminReportView.build model summary family with
                    | Error reasons -> return [ ToEngine(AdminApp.Failed(notice "SIGNAL.REPORT.WITHHELD" (String.concat " " reasons) false)) ]
                    | Ok report ->
                        let source: Source =
                            { GroupId = key
                              GroupResultHash = summary.Report.Hash
                              ReportStateHash = ReportState.resultId (GroupStore.reportState (Releases.shown group))
                              GroupTemplate = template
                              Report = { report with Definition = (entry.Definition.Id, entry.Definition.Version) }
                              Locale = locale
                              ComparisonReferences = []
                              Mode = group.Config.Mode
                              MinimumReportable = group.Config.MinimumReportableCount
                              Clock = Some now }

                        match! ReportStore.takeSnapshot actor now opened (TemplateCatalog.pinned catalog) source with
                        | Error f -> return [ ToEngine(failure f) ]
                        | Ok snapshot ->
                            let! reloaded = load now opened
                            return reloaded @ [ ToEngine(AdminApp.Noted(notice "SIGNAL.REPORT.SNAPSHOT_TAKEN" $"Snapshot {snapshot.SnapshotId} was taken of {snapshot.Accepted} response(s)." false)) ]
        | _ -> return [ ToEngine(AdminApp.Failed(notice "SIGNAL.REPORT.NO_REPORT" "Open a group's report first." false)) ]
    }

/// Saves a definition from the builder; the next version when the one in use was used.
let saveDefinition (actor: Store.Actor) (opened: Store.Opened) (definition: ReportModel.Definition) (pins: Pins) (now: DateTimeOffset) =
    async {
        match! ReportStore.saveDefinition actor now opened definition pins with
        | Error f -> return [ ToEngine(failure f) ]
        | Ok entry ->
            let! reloaded = load now opened
            return reloaded @ [ ToEngine(AdminApp.DefinitionSaved entry); ToEngine(AdminApp.Noted(notice "SIGNAL.REPORT.DEFINITION_SAVED" $"Report definition {entry.Definition.Id} was saved as version {entry.Definition.Version}." false)) ]
    }

let private mimeOf (file: string) =
    if file.EndsWith ".csv" then "text/csv" else "application/json"

/// One of a snapshot's export files, for the person to save.
let export (actor: Store.Actor) (opened: Store.Opened) (snapshot: Snapshot) (file: string) (now: DateTimeOffset) =
    async {
        match! ReportStore.export actor now opened snapshot with
        | Error f -> return [ ToEngine(failure f) ]
        | Ok files ->
            match files |> List.tryFind (fst >> (=) file) with
            | Some(name, data) -> return [ ToEngine(AdminApp.ExportReady($"{snapshot.SnapshotId}-{name}", mimeOf name, data)) ]
            | None -> return [ ToEngine(AdminApp.Failed(notice "SIGNAL.REPORT.NO_SUCH_EXPORT" "That export is not available." false)) ]
    }
