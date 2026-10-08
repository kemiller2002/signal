/// The administrator page's view: what `web/admin/index.html` binds, as
/// Limen's flat named values. Every flag that enables an action comes from
/// `AdminApp.capabilities`; the page decides nothing (ADM-035). No personal
/// data is shown: administrators appear by account number, groups and
/// contributions by opaque ids and hashes (ADM-001).
///
/// Pure.
module Echelon.Signal.Admin.AdminView

open System
open System.Globalization
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.AdminApp
open Echelon.Signal.Admin.Routes

let private text (value: string) = Value(Text value)
let private flag (value: bool) = Value(Flag value)
let private number (value: int) = Value(Number(float value))

let private screen (model: Model) =
    match model.ConfigurationProblem, model.Deployment, model.Principal, model.Dataset with
    | _ when AdminRouteView.isPublic model -> "public"
    | Some _, _, _, _ -> "misconfigured"
    | None, None, _, _ -> "loading"
    | None, Some config, _, _ when config.Profiles.IsEmpty -> "unconfigured"
    | None, Some _, None, _ -> "signIn"
    | None, Some _, Some _, None -> "datasets"
    | None, Some _, Some _, Some _ -> "dataset"

let private credentialText (state: Credential.CredentialState) =
    match state with
    | Credential.CredentialValidReadWrite -> "Signed in; changes can be saved."
    | Credential.CredentialValidReadOnly reason -> $"Signed in, read-only: {reason}."
    | Credential.CredentialUnverifiable reason -> $"Your access cannot be confirmed now: {reason}."
    | Credential.CredentialMissing -> "Not signed in."
    | Credential.CredentialExpired -> "Your sign-in expired. Sign in again."
    | Credential.CredentialRejected -> "GitHub refused your sign-in. Sign in again."
    | Credential.PermissionInsufficient reason -> $"Your account cannot use this data: {reason}."
    | Credential.ProviderIdentityMismatch _ -> "The configured repository is now a different repository; an administrator must confirm it."

let private score (value: float option) =
    value |> Option.map (fun v -> v.ToString("0.0", CultureInfo.InvariantCulture)) |> Option.defaultValue "suppressed"

let private groupItem (model: Model) (group: GroupSummary) =
    [ "key", Text group.Key
      "survey", Text $"{group.SurveyIdentifier} {group.TemplateVersion}"
      "mode", Text(if group.Mode = IdentifiedGroup then "Identified" else "Anonymous")
      "progress", Text $"{group.Accepted} of {group.Expected}"
      "phase", Text(AdminState.phaseName group.Phase)
      "status", Text(GroupLifecycle.statusName group.Status)
      "href", Text(href (Group group.Key))
      "resultsHref", Text(href (Results(group.Key, defaultResults)))
      "selected", Flag(groupKey model = Some group.Key) ]

/// A status as the filter writes it: "ClosedIncomplete" is "closed-incomplete".
let statusValue (status: GroupLifecycle.Status) =
    GroupLifecycle.statusName status
    |> Seq.mapi (fun i c -> if i > 0 && Char.IsUpper c then $"-{Char.ToLowerInvariant c}" else string (Char.ToLowerInvariant c))
    |> String.concat ""

/// Whether a group passes the list's typed filters (SIG-LINK-008).
let private matches (filter: GroupFilter) (group: GroupSummary) =
    (filter.Status.IsEmpty || List.contains (statusValue group.Status) filter.Status)
    && (filter.Mode.IsEmpty || List.contains (if group.Mode = IdentifiedGroup then "identified" else "anonymous") filter.Mode)
    && (filter.Survey |> Option.forall ((=) group.SurveyIdentifier))

let private obligations (dataset: DatasetSummary) =
    dataset.Groups
    |> List.collect (fun g ->
        [ if g.UnreconciledBatches > 0 then AdminState.ReconcileUnknownWrite g.UnreconciledBatches
          if not g.Problems.IsEmpty then AdminState.RepairCorruptRecords g.Problems.Length ])
    |> List.distinct

let private glyph =
    function
    | "circle" -> "●"
    | "square" -> "■"
    | "triangle" -> "▲"
    | "diamond" -> "◆"
    | "star" -> "★"
    | "plus" -> "✚"
    | _ -> "○"

let private marks (compiled: Visualization.Compiled) =
    compiled.Marks
    |> List.map (fun m ->
        [ "key", Text m.Category
          "category", Text m.Category
          "value", Text m.Text
          "position", Number(defaultArg m.Position 0.0)
          "unavailable", Flag(not m.Available)
          "pattern", Text m.Pattern
          "symbol", Text(glyph m.Symbol)
          "colour", Text m.Colour ])

let private warningText =
    function
    | Visualization.TruncatedAxis -> "The axis does not start at zero."
    | Visualization.TooManyCategories n -> $"{n} categories: a table may read better."
    | Visualization.MissingValues n -> $"{n} value(s) cannot be shown and are marked, not drawn as zero."
    | Visualization.TotalWithheldForSuppression -> "The total is withheld so suppressed values cannot be inferred."

/// The open group's charts (ADM-015..017, ADM-019): section means, and the
/// distribution of a section the person drilled into.
let private charts (model: Model) (group: GroupSummary option) =
    let e =
        match model.Place.View with
        | Ok(Results(_, view)) -> view
        | _ -> defaultResults

    let sectionChart =
        group
        |> Option.map (fun g ->
            let spec =
                { Visualization.defaultSpec Visualization.Bar "Section means" with
                    Sort = if (e.Sort = ByValue) then Visualization.ByValueDescending else Visualization.ByOrder }

            let data =
                g.Sections
                |> List.mapi (fun i (id, score) ->
                    ({ Category = id
                       Value =
                         match score with
                         | Some v -> Analysis.Value v
                         | None -> Analysis.Unavailable(Analysis.Suppressed(g.MinimumReportable, g.Accepted))
                       Order = i }: Visualization.Datum))

            Visualization.compile spec data)

    let distribution =
        match group, e.Section with
        | Some g, Some section ->
            match g.Distributions.TryFind section with
            | Some(Ok bins) ->
                let total = bins |> List.sumBy (fun (_, _, n) -> n)

                let spec =
                    { Visualization.defaultSpec Visualization.Histogram $"Distribution of {section}" with
                        Dimension = Visualization.Binned
                        Sort = Visualization.ByOrder
                        Unit = if (e.Display = Percentages) then Visualization.Percent0To1 else Visualization.Count
                        Scale = { Minimum = 0.0; Maximum = (if (e.Display = Percentages) then 1.0 else float (max 1 total)) } }

                let data =
                    bins
                    |> List.mapi (fun i (lower, upper, n) ->
                        ({ Category = $"{lower:F0}-{upper:F0}"
                           Value = Analysis.Value(if (e.Display = Percentages) then (if total = 0 then 0.0 else float n / float total) else float n)
                           Order = i }: Visualization.Datum))

                Some(Visualization.compile spec data)
            | Some(Error reason) -> Some(Error [ Visualization.IllegalShape("Histogram", $"%A{reason}") ])
            | None -> None
        | _ -> None

    let items (compiled: Result<Visualization.Compiled, Visualization.Refusal list> option) =
        match compiled with
        | Some(Ok c) -> Items(marks c), Value(Text c.Description), Value(Text(c.Warnings |> List.map warningText |> String.concat " "))
        | Some(Error refusals) -> Items [], Value(Text $"This chart cannot be drawn: %A{refusals.Head}"), Value(Text "")
        | None -> Items [], Value(Text ""), Value(Text "")

    let sectionItems, sectionDescription, sectionWarnings = items sectionChart
    let distributionItems, distributionDescription, _ = items distribution

    [ "chart", sectionItems
      "chartDescription", sectionDescription
      "chartWarnings", sectionWarnings
      "hasDistribution", Value(Flag distribution.IsSome)
      "distribution", distributionItems
      "distributionDescription", distributionDescription
      "exploreByValue", Value(Flag (e.Sort = ByValue))
      "explorePercent", Value(Flag (e.Display = Percentages))
      "exploreSection", Value(Text(e.Section |> Option.defaultValue "")) ]

/// The last batch's items by the view ADM-008 names.
let private importViews: (string * (string -> bool)) list =
    [ "importPending", (fun code -> code = "pending")
      "importAccepted", (fun code -> code = "accepted")
      "importDuplicate", (fun code -> code = "duplicate" || code = "rejected:duplicate-instance")
      "importRejected", (fun code -> code.StartsWith "rejected:" && code <> "rejected:duplicate-instance")
      "importReconciliation", (fun code -> code = "reconciliation-required")
      "importBlockedTemplate", (fun code -> code = "blocked:missing-template")
      "importBlockedEncoding", (fun code -> code.StartsWith "blocked:unsupported-encoding") ]

/// The page's view.
let project (model: Model) : View =
    let can = capabilities model
    let enabled capability = flag (can.Contains capability)
    let dataset = model.Dataset

    let selected =
        match groupKey model, dataset with
        | Some key, Some d -> d.Groups |> List.tryFind (fun g -> g.Key = key)
        | _ -> None

    let offline, staleSince =
        match model.Connectivity with
        | Credential.Degraded(_, last) -> true, last |> Option.map (fun at -> at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) |> Option.defaultValue "never"
        | Credential.Online -> false, ""

    let current = screen model

    [ "screen", text current
      "screenLoading", flag (current = "loading")
      "screenMisconfigured", flag (current = "misconfigured")
      "screenUnconfigured", flag (current = "unconfigured")
      "screenSignIn", flag (current = "signIn")
      "screenDatasets", flag (current = "datasets")
      "screenDataset", flag (current = "dataset")
      "screenPublic", flag (current = "public")
      "configurationProblem", text (defaultArg model.ConfigurationProblem "")
      "signInBusy", flag (model.SignIn = Credential.SigningIn)
      "retentionPage", flag (model.Retention = Credential.ThisPage)
      "retentionTab", flag (model.Retention = Credential.ThisTab)
      "retentionDisclosure", text (Credential.disclosure model.Retention)
      "principal", text (model.Principal |> Option.map (fun p -> p.PrincipalId) |> Option.defaultValue "")
      "busy", flag model.Busy
      // Status bar: credential, connectivity and staleness (ADM-033, ADM-056, ADM-070).
      "credential", text (dataset |> Option.map (fun d -> credentialText d.Credential) |> Option.defaultValue "")
      "credentialState", text (dataset |> Option.map (fun d -> Credential.stateCode d.Credential) |> Option.defaultValue "")
      "readOnly", flag (not (can.Contains AdminState.CanWriteStore) && dataset.IsSome)
      "readOnlyReasons", text (dataset |> Option.map (fun d -> String.concat " " d.ReadOnlyReasons) |> Option.defaultValue "")
      "offline", flag offline
      "lastVerified", text staleSince
      "verification", text (dataset |> Option.map _.Verification |> Option.defaultValue "")
      // Notices: a sentence, with the stable code as technical detail (ADM-031).
      "hasNotice", flag model.Notice.IsSome
      "notice", text (model.Notice |> Option.map _.Message |> Option.defaultValue "")
      "noticeCode", text (model.Notice |> Option.map _.Code |> Option.defaultValue "")
      "noticeInfrastructure", flag (model.Notice |> Option.exists _.Infrastructure)
      "datasets",
      Items(
          model.Deployment
          |> Option.map (fun c -> c.Datasets |> List.map (fun d -> [ "id", Text d.Id; "label", Text d.Label ]))
          |> Option.defaultValue []
      )
      "datasetLabel", text (dataset |> Option.map _.Label |> Option.defaultValue "")
      // Overview: obligations first (ADM-031).
      "obligations",
      Items(
          dataset
          |> Option.map (obligations >> List.map (fun o -> [ "code", Text(AdminState.obligationCode o); "text", Text(AdminState.describeObligation o) ]))
          |> Option.defaultValue []
      )
      "groupsWaiting", number (dataset |> Option.map (fun d -> d.Groups |> List.filter (fun g -> g.Phase = AdminState.GroupReady || g.Phase = AdminState.GroupPartial) |> List.length) |> Option.defaultValue 0)
      "groupsComplete", number (dataset |> Option.map (fun d -> d.Groups |> List.filter (fun g -> g.Phase = AdminState.GroupComplete) |> List.length) |> Option.defaultValue 0)
      // Groups (ADM-007, ADM-032).
      "groups",
      Items(
          let filter = match model.Place.View with Ok(Groups f) -> f | _ -> noFilter
          dataset |> Option.map (fun d -> d.Groups |> List.filter (matches filter) |> List.map (groupItem model)) |> Option.defaultValue []
      )
      "canCreateGroup", enabled AdminState.CanCreateGroup
      "catalog", Items(model.Catalog |> List.map (fun e -> [ "hash", Text e.Hash; "label", Text $"{e.Title} ({e.SurveyIdentifier} {e.Version})"; "selected", Flag(e.Hash = model.NewGroup.Template) ]))
      "newGroupIdentified", flag (model.NewGroup.Mode = IdentifiedGroup)
      "newGroupExpected", text (string model.NewGroup.Expected)
      "newGroupMinimum", text (string model.NewGroup.Minimum)
      // The open group (ADM-007, ADM-008, ADM-066).
      "hasGroup", flag selected.IsSome
      "groupKey", text (selected |> Option.map _.Key |> Option.defaultValue "")
      "groupPhase", text (selected |> Option.map (_.Phase >> AdminState.phaseName) |> Option.defaultValue "")
      "groupProgress", text (selected |> Option.map (fun g -> $"{g.Accepted} of {g.Expected} accepted") |> Option.defaultValue "")
      "sections", Items(selected |> Option.map (fun g -> g.Sections |> List.map (fun (id, s) -> [ "id", Text id; "score", Text(score s) ])) |> Option.defaultValue [])
      "reportFragment", text (selected |> Option.map _.ReportFragment |> Option.defaultValue "")
      // Analysis (ADM-012, ADM-013) and lineage (ADM-020): calculated, never canonical.
      "analysis",
      Items(
          selected
          |> Option.map (fun g -> g.Analysis |> List.map (fun (section, measure, value) -> [ "key", Text $"{section}/{measure}"; "section", Text section; "measure", Text measure; "value", Text value ]))
          |> Option.defaultValue []
      )
      "lineage", text (selected |> Option.map _.Lineage |> Option.defaultValue "")
      yield! charts model selected
      "canImport", enabled AdminState.CanImportBatch
      "importText", text model.ImportText
      "importFile", flag (model.ImportOrigin = ResultRecord.ImportedTextFile)
      "batchStatus", text (selected |> Option.bind _.LastBatch |> Option.map (_.Status >> Intake.statusName) |> Option.defaultValue "")
      "batchCounts",
      text (
          selected
          |> Option.bind _.LastBatch
          |> Option.map (fun b -> $"{b.AcceptedCount} accepted, {b.DuplicateCount} duplicate, {b.RejectedCount} rejected, {b.BlockedCount} blocked, {b.ReconciliationRequiredCount} to reconcile, {b.RemainingCount} remaining")
          |> Option.defaultValue ""
      )
      "importItems", Items(selected |> Option.map (fun g -> g.Items |> List.map (fun (hash, code) -> [ "artifact", Text(hash.Substring(0, min 19 hash.Length)); "outcome", Text code ])) |> Option.defaultValue [])
      // ADM-008's separate views of the last batch, by outcome.
      for view, belongs in importViews do
          view, Items(selected |> Option.map (fun g -> g.Items |> List.filter (snd >> belongs) |> List.map (fun (hash, code) -> [ "artifact", Text(hash.Substring(0, min 19 hash.Length)); "outcome", Text code ])) |> Option.defaultValue [])
      "canClose", enabled AdminState.CanCloseGroup
      "canReopen", enabled AdminState.CanReopenGroup
      "canFinalize", enabled AdminState.CanFinalizeGroup
      "canSeal", enabled AdminState.CanSealGroup
      // Administrators (ADM-052): by account number, with what each holds.
      "administrators",
      Items(
          dataset
          |> Option.map (fun d ->
              d.Roster.Members
              |> Map.toList
              |> List.map (fun (id, m) ->
                  [ "id", Text id
                    "capabilities", Text(allCapabilities |> List.filter m.Capabilities.Contains |> List.map capabilityName |> String.concat ", ") ]))
          |> Option.defaultValue []
      )
      "canManageAdministrators", enabled AdminState.CanManageAdministrators
      "admitAccount", text model.Admit.Account
      "canRepairIndex", enabled AdminState.CanRepairIndex
      // The conflict workspace (ADM-062).
      "hasConflict", flag model.Conflict.IsSome
      "conflictFields",
      Items(
          model.Conflict
          |> Option.map (fun c -> c.Differences |> List.map (fun d -> [ "field", Text d.Field; "base", Text d.Base; "current", Text d.Current; "proposed", Text d.Proposed; "conflicting", Flag d.Conflicting ]))
          |> Option.defaultValue []
      )
      "capabilityList", text (can |> Set.toList |> List.map AdminState.capabilityName |> String.concat " ")
      yield! AdminRouteView.project model selected
      yield! AdminReportView.project model selected ]
