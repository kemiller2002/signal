/// The administrator page's engine (ADM-002, ADM-031..033, ADM-035, ADM-070):
/// its state, the messages that change it, the effects it asks for, and the
/// view it projects. Pure: the application tier performs the effects (sign-in
/// through Fides, storage through Arca) and answers with messages carrying
/// summaries, never tokens.
///
/// Every capability the page shows comes from `AdminState.capabilities`;
/// every refusal is a stable code with a sentence; nothing here reads a
/// clock or the network.
module Echelon.Signal.Admin.AdminApp

open System
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.Routes
open Limen.Routing
open Echelon.Signal.Admin.AdminNavigation
open Echelon.Signal.Admin.AdminTypes

type CatalogEntry = AdminTypes.CatalogEntry
type GroupSummary = AdminTypes.GroupSummary
type DatasetSummary = AdminTypes.DatasetSummary
type Notice = AdminTypes.Notice
type SnapshotSummary = AdminTypes.SnapshotSummary

[<NoComparison>]
type Model =
    { Deployment: Deployment.DeploymentConfig option
      ConfigurationProblem: string option
      Catalog: CatalogEntry list
      /// The dataset's stored template catalog as the console lists it (WI-0073).
      Templates: TemplateListing.Listing
      Authoring: Authoring.Screen
      /// Sample test links for one published version, when asked for (AUT-006 §61).
      TestLinks: TestLinks.Shown option
      /// What the invitation form holds, and the links last issued for a group (VER-003).
      Invite: Invitations.Request
      Issued: {| Group: string; Links: string list |} option
      /// The dataset's formal report snapshots (WI-0075).
      Snapshots: SnapshotSummary list
      /// The saved report definitions and the builder (WI-0075).
      Library: ReportLibrary.Library
      Builder: ReportBuilder.Screen
      SignIn: Credential.SignIn
      Principal: Principal option
      Retention: Credential.Retention
      Dataset: DatasetSummary option
      /// Where the page is: the view its URL names (SIG-LINK-001).
      Place: Place
      /// A sign-in's return target, recalled after the provider's round trip.
      ReturnTarget: string option
      ImportText: string
      ImportOrigin: ResultRecord.ImportOrigin
      NewGroup: {| Template: string; Mode: IdentityMode; Expected: int; Minimum: int |}
      Admit: {| Account: string; Grant: string |}
      Connectivity: Credential.Connectivity
      LastVerified: DateTimeOffset option
      Notice: Notice option
      Busy: bool
      Conflict: Conflicts.Conflict option
      /// The provider's callback parameters the page was opened with, if any.
      CallbackQuery: (string * string) list }

let initial (catalog: CatalogEntry list) =
    { Deployment = None
      ConfigurationProblem = None
      Catalog = catalog
      Templates = TemplateListing.empty
      Authoring = Authoring.emptyScreen
      TestLinks = None
      Invite = Invitations.emptyRequest
      Issued = None
      Snapshots = []
      Library = ReportLibrary.empty
      Builder = ReportBuilder.emptyScreen
      SignIn = Credential.SignedOut
      Principal = None
      Retention = Credential.defaultRetention
      Dataset = None
      Place = AdminNavigation.initial
      ReturnTarget = None
      ImportText = ""
      ImportOrigin = ResultRecord.MultiPaste
      NewGroup = {| Template = (catalog |> List.tryHead |> Option.map _.Hash |> Option.defaultValue ""); Mode = AnonymousGroup; Expected = 10; Minimum = 5 |}
      Admit = {| Account = ""; Grant = "analyst" |}
      Connectivity = Credential.Online
      LastVerified = None
      Notice = None
      Busy = false
      Conflict = None
      CallbackQuery = [] }

/// What the page asks the application to do.
type Effect =
    | ReadConfiguration
    | BeginIdentity of Deployment.IdentityConfig * query: (string * string) list
    | SignIn of Credential.Retention
    | SignOut
    | OpenDataset of datasetId: string
    | CreateGroup of templateHash: string * mode: IdentityMode * expected: int * minimum: int
    | OpenGroup of key: string
    | ImportArtifacts of key: string * texts: string list * origin: ResultRecord.ImportOrigin
    | TransitionGroup of key: string * transition: string
    /// Issue respondent links for a group: how many, and their terms.
    | IssueInvitations of key: string * count: int * terms: Echelon.Signal.Engine.GenericEnvelope.Terms
    | ChangeRoster of RosterCommand
    | RebuildIndex
    /// Authoring (WI-0073): store a draft, publish one, hide a version.
    | SaveDraft of Echelon.Signal.Engine.Drafts.Draft
    | PublishDraft of Echelon.Signal.Engine.Drafts.Draft * acknowledged: Set<string>
    | HideTemplate of survey: string * version: string
    | RequestReview of survey: string
    | ApproveReview of Echelon.Signal.Engine.Drafts.Draft
    /// Reports (WI-0075): take a formal snapshot of the report in view; download one of a snapshot's export files.
    | TakeSnapshot of group: string * family: string * locale: string
    | ExportSnapshot of snapshotId: string * file: string
    /// Hand the person a file to save (Limen's files capability).
    | Download of fileName: string * mimeType: string * data: string
    | SaveReportDefinition of Echelon.Signal.Engine.ReportModel.Definition * ReportLibrary.Pins
    /// A Navigation effect: push or replace a relative `#/…` URL.
    | Go of Move
    /// Write a view's link to the clipboard (SIG-LINK-005).
    | CopyLink of url: string
    /// Keep, recall and forget a sign-in's return target in this tab (SIG-LINK-006).
    | RememberReturn of target: string
    | RecallReturn
    | ForgetReturn

/// What happens to the page.
[<NoComparison>]
type Msg =
    | Started of page: Limen.Routing.PageLocation * query: (string * string) list
    | ConfigurationRead of text: string option
    | IdentityChanged of Credential.SignIn * Principal option
    | TabNoticed of Credential.TabNotice
    | DatasetOpened of DatasetSummary * at: DateTimeOffset
    /// The templates new groups can start from (built in and stored), and the stored listing.
    | CatalogLoaded of CatalogEntry list * TemplateListing.Listing
    /// A draft was stored, or published as a version.
    | DraftSaved of survey: string
    | SnapshotsLoaded of SnapshotSummary list
    | LibraryLoaded of ReportLibrary.Library
    | DefinitionSaved of ReportLibrary.Entry
    /// An export file is ready to hand over.
    | ExportReady of fileName: string * mimeType: string * data: string
    | DraftPublished of survey: string * version: string
    | GroupUpdated of GroupSummary
    /// A group's new respondent links, absolute.
    | InvitationsIssued of group: string * links: string list
    | Failed of Notice
    /// Something worth telling the person that is not a failure.
    | Noted of Notice
    | WentOffline of reason: string
    | ConflictFound of Conflicts.Conflict
    | LocationMoved of page: Limen.Routing.PageLocation
    | ReturnRecalled of target: string option
    | LinkCopied of succeeded: bool
    /// A page event: its name, key and value (`data-event`).
    | Ui of name: string * key: string option * value: string

/// The group the current view is about, if any.
let groupKey (model: Model) =
    match model.Place.View with
    | Ok(Group g | Results(g, _) | Scoring(g, _) | Imports(g, _) | Report(g, _, _)) -> Some g
    | _ -> None

/// What the guards read: sign-in is required when it is configured, has
/// been tried, and nobody is signed in.
let access (model: Model) =
    match model.Deployment |> Option.bind _.Identity, model.Principal, model.SignIn with
    | Some _, None, signIn when signIn <> Credential.SigningIn -> SignInRequired
    | _ -> Open

/// The capabilities the person has now, from authoritative state only.
let capabilities (model: Model) =
    match model.Dataset, model.Principal with
    | Some dataset, Some principal ->
        let storage =
            match model.Connectivity, dataset.ReadOnlyReasons with
            | Credential.Degraded(reason, _), _ -> AdminState.DegradedReadOnly reason
            | Credential.Online, [] -> AdminState.StorageConfigured
            | Credential.Online, reason :: _ -> AdminState.DegradedReadOnly reason

        let usable, _ =
            Credential.usable dataset.Roster principal.PrincipalId dataset.Credential model.Connectivity dataset.ReadOnlyReasons.IsEmpty

        let group =
            match groupKey model with
            | Some key ->
                dataset.Groups
                |> List.tryFind (fun g -> g.Key = key)
                |> Option.map (fun g ->
                    ({ Status = g.Status
                       Accepted = g.Accepted
                       Expected = g.Expected
                       BatchRunning = g.LastBatch |> Option.exists (fun b -> b.Status = Intake.Running)
                       UnreconciledBatches = g.UnreconciledBatches }: AdminState.GroupFacts))
            | _ -> None

        AdminState.capabilities storage usable group
    | _ -> Set.empty
