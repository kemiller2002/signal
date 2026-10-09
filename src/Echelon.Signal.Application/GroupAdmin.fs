/// Group administration over the store (ADM-007, ADM-065, ADM-066) and the
/// summaries the administrator page shows.
///
/// - Listing a dataset's groups reads their configuration records.
/// - A lifecycle transition is one commit of the lifecycle record,
///   conditioned on the revision read. Finalization first gathers its
///   evidence from the store: completion, unfinished batches, unusable
///   records and whether the contribution index is current; open
///   obligations block it with their names (ADM-066). Sealing needs a
///   finalized group, and a sealed group refuses imports and configuration
///   changes (ADM-065).
module Echelon.Signal.Application.GroupAdmin

open System
open Arca
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Application.Flow
open Echelon.Signal.Application.GroupStore

let private failed now (failure: StorageFailure) =
    match failure with
    | StorageFailure.ProviderFailed(_, true, _) -> Offline(ProviderContract.meaning now failure)
    | _ -> Storage(ProviderContract.meaning now failure)

let private call now work = work |> mapError (failed now)

/// The groups a dataset holds, by id, read as untrusted records.
let listGroups (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<OpaqueId list, GroupFailure> =
    asyncResult {
        let! listing = call now (opened.Provider.List opened.Namespace GroupRecord.folder)
        let! objects = Loading.recordFiles listing |> List.map (fun path -> call now (opened.Provider.Read opened.Namespace path)) |> sequence
        let found = objects |> List.choose (function ReadOutcome.Found stored -> Some stored | ReadOutcome.Absent -> None)
        let loaded = Loading.load opened.Verified GroupRecord.reader GroupRecord.folder listing found
        return loaded.Records |> Map.toList |> List.map (fun (_, stored) -> stored.Value.Config.Group)
    }

/// The batches of a group that did not finish: interrupted, blocked or
/// awaiting reconciliation. They must be resumed before finalization.
let unfinishedBatches (now: DateTimeOffset) (group: OpenedGroup) : AsyncResult<Intake.Batch list, GroupFailure> =
    let opened = group.Dataset

    asyncResult {
        let! folder = RelativePath.parse ("records/" + RecordType.value Intake.recordType) |> Result.mapError (fun e -> Unusable [ InvalidDataLocation(LocationError.describe e) ]) |> lift
        let! listing = call now (opened.Provider.List opened.Namespace folder)
        let! objects = Loading.recordFiles listing |> List.map (fun path -> call now (opened.Provider.Read opened.Namespace path)) |> sequence

        let batches =
            objects
            |> List.choose (function
                | ReadOutcome.Found stored ->
                    Record.decode Record.DefaultMaxBytes stored.Content
                    |> Result.toOption
                    |> Option.bind (fun record -> Intake.ofBody record.Body |> Result.toOption)
                | ReadOutcome.Absent -> None)
            |> List.map _.Batch
            |> List.filter (fun batch -> batch.Group = group.Config.Group)

        return
            batches
            |> List.filter (fun batch ->
                match (Intake.summarize batch).Status with
                | Intake.Complete
                | Intake.CompleteWithRejections
                | Intake.CancelledBeforeProcessingRemainder -> false
                | _ -> true)
    }

/// Applies a lifecycle transition (`close`, `reopen`, `finalize`, `seal`):
/// one commit of the lifecycle record. Returns the group as it stands after.
let transition (actor: Store.Actor) (name: string) (now: DateTimeOffset) (group: OpenedGroup) : AsyncResult<OpenedGroup, GroupFailure> =
    let opened = group.Dataset

    let capability =
        match name with
        | "finalize"
        | "seal" -> Access.SealAndFinalize
        | _ -> Access.ManageGroups

    asyncResult {
        let! _ =
            match Access.authorize opened.Roster.Roster opened.DatasetId actor.Principal.PrincipalId capability, opened.Grant with
            | Error refusal, _ -> Error(NotPermitted refusal)
            | Ok(), None -> Error(ReadOnly opened.ReadOnlyReasons)
            | Ok(), Some grant -> Ok grant
            |> lift

        let! requested =
            match name with
            | "close" -> async.Return(Ok GroupLifecycle.Close)
            | "reopen" -> async.Return(Ok GroupLifecycle.Reopen)
            | "seal" -> async.Return(Ok GroupLifecycle.Seal)
            | "finalize" ->
                asyncResult {
                    let! unfinished = unfinishedBatches now group
                    let! index = validateIndex now opened

                    return
                        GroupLifecycle.Finalize
                            { Complete = group.Accumulator.Accepted.Count >= group.Config.ExpectedCount
                              UnreconciledBatches = unfinished.Length
                              RecordProblems = group.Problems.Length
                              IndexCurrent = (index = IndexStatus.Current)
                              MinimumReportableCount = group.Config.MinimumReportableCount }
                }
            | other -> async.Return(Error(LifecycleRefused(GroupLifecycle.Illegal(GroupLifecycle.statusName group.Lifecycle.Status, other))))

        let! status = GroupLifecycle.apply requested group.Lifecycle.Status |> Result.mapError LifecycleRefused |> lift

        let next =
            { group.Lifecycle with
                Status = status
                Revision = group.Lifecycle.Revision + (if group.LifecycleRevision.IsSome then 1 else 0) }

        let! operation =
            match GroupLifecycle.path group.Config.Group, GroupLifecycle.encode opened.DatasetId next with
            | Ok path, Ok content ->
                let change =
                    match group.LifecycleRevision with
                    | Some revision -> Change.Update(path, content, revision)
                    | None -> Change.Create(path, content)

                Storage.operation opened.Namespace (actor.NewContext()) $"{name} group {GroupRecord.groupKey group.Config.Group}" [ change ]
                |> Result.bind (
                    GovernanceRecord.auditedWith
                        opened.DatasetId
                        [ GovernanceRecord.record
                              Audit.GroupConfigurationVersioned
                              [ "group", string group.Config.Group ]
                              []
                              [ GovernanceRecord.codeOf ("lifecycle-" + name) ]
                              (Some("state-" + GovernanceRecord.codeOf(GroupLifecycle.statusName group.Lifecycle.Status).ToLowerInvariant()))
                              (Some("state-" + GovernanceRecord.codeOf(GroupLifecycle.statusName status).ToLowerInvariant()))
                              (Some now) ]
                )
            | Error problem, _
            | _, Error problem -> Error [ problem ]
            |> Result.mapError Unusable
            |> lift

        let! receipt = call now (opened.Provider.Commit operation)

        let revision =
            GroupLifecycle.path group.Config.Group
            |> Result.toOption
            |> Option.bind (fun path -> receipt.Revisions.TryFind(RelativePath.render path))
            |> Option.flatten

        return
            { group with
                Lifecycle = next
                LifecycleRevision = revision |> Option.orElse group.LifecycleRevision }
    }

// ---- Summaries for the page ------------------------------------------------------------------

/// The measures the page shows for each section, with the reason when one
/// has no value (ADM-012, ADM-013).
let private analysisRows (group: OpenedGroup) =
    let source = Analysis.source group.Config group.Accumulator

    let show =
        function
        | Analysis.Value v -> v.ToString("0.0#", Globalization.CultureInfo.InvariantCulture)
        | Analysis.Unavailable(Analysis.Suppressed(minimum, _)) -> $"suppressed below {minimum}"
        | Analysis.Unavailable(Analysis.InsufficientSample(required, found)) -> $"needs {required} scores, has {found}"
        | Analysis.Unavailable reason -> $"unavailable: %A{reason}"

    [ for section in source.Scores |> Map.keys do
          for m in [ Analysis.Mean; Analysis.Median; Analysis.StandardDeviation; Analysis.InterquartileRange; Analysis.ConfidenceLower95; Analysis.ConfidenceUpper95 ] ->
              section, Analysis.measureName m, show (Analysis.measure source (Some section) m) ]

/// What the page shows about a group.
/// A section's distribution, released only when the disclosure policy allows
/// it (ADM-024): enough values, enough distinct ones, and no occupied bin
/// small enough to single out its respondents.
let private distribution (group: OpenedGroup) (source: Analysis.Source) (section: string) =
    let policy = Disclosure.forGroup group.Config.MinimumReportableCount
    let scores = source.Scores.TryFind section |> Option.defaultValue []

    match Disclosure.distribution policy group.Config.Mode scores, Analysis.distribution source section 5 with
    | Disclosure.Withhold _, Ok _ -> Error(Analysis.Suppressed(policy.MinimumDistribution, scores.Length))
    | Disclosure.Release _, Ok bins when
        group.Config.Mode = Echelon.Signal.Engine.Import.AnonymousGroup
        && bins |> List.exists (fun (_, _, n) -> n > 0 && n < policy.MinimumCell)
        ->
        Error(Analysis.Suppressed(policy.MinimumCell, scores.Length))
    | _, found -> found

/// What the page shows about a group. Everything derived from responses
/// comes from what the group has released (`Releases.shown`); the counts are live.
let summary (unreconciled: int) (imported: Imported option) (live: OpenedGroup) : AdminApp.GroupSummary =
    let group = Releases.shown live
    let state = reportState group

    let lastBatch, items =
        match imported with
        | Some found ->
            Some found.Summary,
            found.Batch.Items |> List.map (fun (hash, outcome) -> hash, outcome |> Option.map Intake.itemCode |> Option.defaultValue "pending")
        | None -> None, []

    // Imported surveys: every stored contribution (accepted), with this
    // session's last batch adding its other outcomes, so a link to the view
    // shows the same list after a reload (SIG-LINK-001).
    let stored =
        live.Contributions
        |> Map.toList
        |> List.map (fun (_, c) -> c.Value.Contribution.Provenance.ArtifactHash, "accepted")
        |> List.filter (fun (hash, _) -> not (items |> List.exists (fun (h, _) -> h = hash)))
        |> List.sort

    let items = items @ stored

    { Key = GroupRecord.groupKey group.Config.Group
      SurveyIdentifier = group.Config.SurveyIdentifier
      TemplateVersion = group.Config.TemplateVersion
      Mode = group.Config.Mode
      Expected = group.Config.ExpectedCount
      Accepted = live.Accumulator.Accepted.Count
      Withheld = Releases.withheld live
      MinimumReportable = group.Config.MinimumReportableCount
      Status = group.Lifecycle.Status
      Phase =
        AdminState.phase
            { Status = group.Lifecycle.Status
              Accepted = live.Accumulator.Accepted.Count
              Expected = group.Config.ExpectedCount
              BatchRunning = lastBatch |> Option.exists (fun b -> b.Status = Intake.Running)
              UnreconciledBatches = unreconciled }
      Sections = state.Sections |> List.map (fun s -> s.SectionId, s.AggregateScore)
      ReportFragment =
        match ReportState.persistence ReportState.defaultBudget state with
        | ReportState.EmbeddedInUrl fragment
        | ReportState.ExternalStore(_, fragment) -> fragment
      LastBatch = lastBatch
      Items = items
      UnreconciledBatches = unreconciled
      Analysis = analysisRows group
      Distributions =
        let source = Analysis.source group.Config group.Accumulator
        source.Scores |> Map.map (fun section _ -> distribution group source section)
      Report = Echelon.Signal.Engine.GroupResult.ofAccumulator group.Config.TemplateHash group.Config.MinimumReportableCount group.Accumulator
      Template = group.Definition.Template
      Content = group.Definition.Generic |> Option.map _.Content |> Option.defaultValue (Echelon.Signal.Engine.Pilot.contentOf group.Definition.Template)
      Lineage =
        let result = result group
        $"{result.Lineage.DerivationHash} from {result.Lineage.SubmissionHashes.Length} accepted contribution(s), template {result.Lineage.TemplateHash}"
      Problems = live.Problems |> List.map Problems.code }

/// What the page shows about a dataset.
let datasetSummary (config: Deployment.DeploymentConfig) (opened: Store.Opened) (groups: AdminApp.GroupSummary list) : AdminApp.DatasetSummary =
    { DatasetId = opened.DatasetId
      Label = Deployment.dataset config opened.DatasetId |> Option.map _.Label |> Option.defaultValue opened.DatasetId
      Roster = opened.Roster.Roster
      Credential = opened.Credential
      ReadOnlyReasons = opened.ReadOnlyReasons |> List.map Problems.describe
      Verification =
        match opened.Verification with
        | ProviderContract.Verified(repository, mode, missing) ->
            $"Repository {repository}: {ProviderContract.writeModeCode mode}" + (if missing.IsEmpty then "" else $"; {missing.Length} capability(ies) missing")
        | ProviderContract.NotVerified -> "Not verified yet"
        | ProviderContract.VerificationFailed reason -> reason
      Groups = groups }
