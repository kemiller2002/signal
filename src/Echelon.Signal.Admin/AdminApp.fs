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

/// A template the catalog offers for new groups.
type CatalogEntry =
    { Hash: string
      SurveyIdentifier: string
      Version: string
      Title: string }

/// What the page shows about one group.
[<NoComparison>]
type GroupSummary =
    { Key: string
      SurveyIdentifier: string
      TemplateVersion: string
      Mode: IdentityMode
      Expected: int
      Accepted: int
      Status: GroupLifecycle.Status
      Phase: AdminState.GroupPhase
      /// Section scores as the report state shows them (suppressed are None).
      Sections: (string * float option) list
      /// Where the report state lives: an embedded fragment or a reference.
      ReportFragment: string
      LastBatch: Intake.BatchSummary option
      /// Each item of the last batch: artifact hash and outcome code.
      Items: (string * string) list
      UnreconciledBatches: int
      /// Calculated measures per section (ADM-012): section, measure, value or reason.
      Analysis: (string * string * string) list
      /// The group result's derivation hash and contribution count (ADM-020).
      Lineage: string
      Problems: string list }

/// What the page shows about the open dataset.
[<NoComparison>]
type DatasetSummary =
    { DatasetId: string
      Label: string
      Roster: Roster
      Credential: Credential.CredentialState
      ReadOnlyReasons: string list
      Verification: string
      Groups: GroupSummary list }

/// The page's areas (ADM-031).
type Route =
    | Overview
    | Groups
    | Group of key: string
    | Administrators
    | Storage

let routeOf (hash: string) =
    match hash.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries) with
    | [| "groups" |] -> Groups
    | [| "groups"; key |] -> Group key
    | [| "administrators" |] -> Administrators
    | [| "storage" |] -> Storage
    | _ -> Overview

let hashOf =
    function
    | Overview -> "#/overview"
    | Groups -> "#/groups"
    | Group key -> $"#/groups/{key}"
    | Administrators -> "#/administrators"
    | Storage -> "#/storage"

/// A message for the person: a stable code and a sentence (ADM-031).
type Notice =
    { Code: string
      Message: string
      /// Domain refusal (the person can act) or infrastructure (the system).
      Infrastructure: bool }

[<NoComparison>]
type Model =
    { Deployment: Deployment.DeploymentConfig option
      ConfigurationProblem: string option
      Catalog: CatalogEntry list
      SignIn: Credential.SignIn
      Principal: Principal option
      Retention: Credential.Retention
      Dataset: DatasetSummary option
      Route: Route
      Filter: string
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
      SignIn = Credential.SignedOut
      Principal = None
      Retention = Credential.defaultRetention
      Dataset = None
      Route = Overview
      Filter = ""
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
    | ChangeRoster of RosterCommand
    | RebuildIndex
    | Navigate of hash: string

/// What happens to the page.
[<NoComparison>]
type Msg =
    | Started of hash: string * query: (string * string) list
    | ConfigurationRead of text: string option
    | IdentityChanged of Credential.SignIn * Principal option
    | TabNoticed of Credential.TabNotice
    | DatasetOpened of DatasetSummary * at: DateTimeOffset
    | GroupUpdated of GroupSummary
    | Failed of Notice
    /// Something worth telling the person that is not a failure.
    | Noted of Notice
    | WentOffline of reason: string
    | ConflictFound of Conflicts.Conflict
    | LocationMoved of hash: string
    /// A page event: its name, key and value (`data-event`).
    | Ui of name: string * key: string option * value: string

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
            match model.Route with
            | Group key ->
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

let private can (model: Model) capability = (capabilities model).Contains capability

let private refuse code message =
    Some
        { Code = code
          Message = message
          Infrastructure = false }

let private grantOf =
    function
    | "importer" -> Grants.importer
    | "draftEditor" -> Grants.draftEditor
    | "reviewer" -> Grants.reviewer
    | "publisher" -> Grants.publisher
    | "administrator" -> Grants.administrator
    | _ -> Grants.analyst

let private positive (text: string) fallback =
    match Int32.TryParse text with
    | true, value when value >= 1 && value <= 100000 -> value
    | _ -> fallback

/// What the page does for an event.
let private onUi (model: Model) (name: string) (key: string option) (value: string) : Model * Effect list =
    let busy effects = { model with Busy = true; Notice = None }, effects
    let routeKey = match model.Route with Group key -> Some key | _ -> None

    match name with
    | "retention" -> { model with Retention = (if value = "tab" then Credential.ThisTab else Credential.ThisPage) }, []
    | "signIn" when model.SignIn <> Credential.SigningIn ->
        match model.Deployment |> Option.bind _.Identity with
        | Some _ -> { model with SignIn = Credential.SigningIn }, [ SignIn model.Retention ]
        | None -> model, []
    | "signIn" -> model, []
    | "signOut" -> { model with Dataset = None }, [ SignOut ]
    | "openDataset" ->
        match key with
        | Some id when model.Principal.IsSome -> busy [ OpenDataset id ]
        | _ -> model, []
    | "navigate" ->
        let route = routeOf (defaultArg key "")
        { model with Route = route }, [ Navigate(hashOf route) ]
    | "openGroup" ->
        match key with
        | Some groupKey ->
            let route = Group groupKey
            { model with Route = route; Busy = true; Notice = None }, [ Navigate(hashOf route); OpenGroup groupKey ]
        | None -> model, []
    | "filter" -> { model with Filter = value }, []
    | "importText" -> { model with ImportText = value }, []
    | "importOrigin" ->
        { model with ImportOrigin = (if value = "file" then ResultRecord.ImportedTextFile else ResultRecord.MultiPaste) }, []
    | "import" ->
        let texts = model.ImportText.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray

        match routeKey with
        | Some groupKey when can model AdminState.CanImportBatch && not texts.IsEmpty ->
            let model, effects = busy [ ImportArtifacts(groupKey, texts, (if texts.Length = 1 && model.ImportOrigin = ResultRecord.MultiPaste then ResultRecord.PastedUrl else model.ImportOrigin)) ]
            { model with ImportText = "" }, effects
        | Some _ when texts.IsEmpty -> model, []
        | _ -> { model with Notice = refuse "SIGNAL.ADMIN.NOT_PERMITTED" "Importing is not available here now." }, []
    | "newGroupTemplate" -> { model with NewGroup = {| model.NewGroup with Template = value |} }, []
    | "newGroupMode" -> { model with NewGroup = {| model.NewGroup with Mode = (if value = "identified" then IdentifiedGroup else AnonymousGroup) |} }, []
    | "newGroupExpected" -> { model with NewGroup = {| model.NewGroup with Expected = positive value model.NewGroup.Expected |} }, []
    | "newGroupMinimum" -> { model with NewGroup = {| model.NewGroup with Minimum = positive value model.NewGroup.Minimum |} }, []
    | "createGroup" when can model AdminState.CanCreateGroup ->
        let g = model.NewGroup
        busy [ CreateGroup(g.Template, g.Mode, g.Expected, g.Minimum) ]
    | "createGroup" -> { model with Notice = refuse "SIGNAL.ADMIN.NOT_PERMITTED" "Creating groups needs ManageGroups and a writable store." }, []
    | "transition" ->
        let needed =
            match value with
            | "close" -> AdminState.CanCloseGroup
            | "reopen" -> AdminState.CanReopenGroup
            | "finalize" -> AdminState.CanFinalizeGroup
            | _ -> AdminState.CanSealGroup

        match routeKey with
        | Some groupKey when can model needed -> busy [ TransitionGroup(groupKey, value) ]
        | _ -> { model with Notice = refuse "SIGNAL.GROUP.ILLEGAL_TRANSITION" $"'{value}' is not available for this group now." }, []
    | "admitAccount" -> { model with Admit = {| model.Admit with Account = value.Trim() |} }, []
    | "admitGrant" -> { model with Admit = {| model.Admit with Grant = value |} }, []
    | "admit" when can model AdminState.CanManageAdministrators ->
        let account = model.Admit.Account

        if account <> "" && account.Length <= 20 && account |> Seq.forall Char.IsAsciiDigit then
            let principal =
                { PrincipalId = $"github:{account}"
                  Kind = Human
                  DisplayName = $"GitHub account {account}" }

            let model, effects = busy [ ChangeRoster(Admit(principal, grantOf model.Admit.Grant)) ]
            { model with Admit = {| model.Admit with Account = "" |} }, effects
        else
            { model with Notice = refuse "SIGNAL.ACCESS.NOT_AN_ACCOUNT_NUMBER" "Enter the person's GitHub account number (digits only), never a login or e-mail." }, []
    | "removeAdministrator" when can model AdminState.CanManageAdministrators ->
        match key with
        | Some principalId -> busy [ ChangeRoster(Remove principalId) ]
        | None -> model, []
    | "admit"
    | "removeAdministrator" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "This needs ManageAdministrators and a writable store." }, []
    | "rebuildIndex" when can model AdminState.CanRepairIndex -> busy [ RebuildIndex ]
    | "rebuildIndex" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Rebuilding the index needs ManageStorage." }, []
    | "refresh" ->
        match model.Dataset with
        | Some dataset -> busy [ OpenDataset dataset.DatasetId ]
        | None -> model, []
    | "dismissNotice" -> { model with Notice = None; Conflict = None }, []
    | other -> invalidOp $"The administrator page sent an event the engine does not know: '{other}'"

/// The next model and the effects to perform.
let update (msg: Msg) (model: Model) : Model * Effect list =
    match msg with
    | Started(hash, query) -> { model with Route = routeOf hash; CallbackQuery = query }, [ ReadConfiguration ]
    | ConfigurationRead None -> { model with ConfigurationProblem = Some "This deployment's configuration could not be read." }, []
    | ConfigurationRead(Some text) ->
        match Deployment.parse text with
        | Error problem -> { model with ConfigurationProblem = Some(Problems.describe problem) }, []
        | Ok config ->
            let model = { model with Deployment = Some config; ConfigurationProblem = None }

            match config.Identity with
            | Some identity -> { model with SignIn = Credential.SigningIn; CallbackQuery = [] }, [ BeginIdentity(identity, model.CallbackQuery) ]
            | None -> model, []
    | IdentityChanged(signIn, principal) ->
        let model = { model with SignIn = signIn; Principal = principal; Busy = false }

        match principal, model.Deployment with
        | Some _, Some config ->
            match config.Datasets with
            | [ only ] -> { model with Busy = true }, [ OpenDataset only.Id ]
            | _ -> model, []
        | None, _ -> { model with Dataset = None }, []
        | _ -> model, []
    | TabNoticed notice ->
        match model.Dataset with
        | Some dataset ->
            let downgraded = Credential.afterNotice notice dataset.Credential

            match notice with
            | Credential.SignedOutElsewhere -> { model with Dataset = None; Principal = None; SignIn = Credential.SignedOut }, []
            | _ -> { model with Dataset = Some { dataset with Credential = downgraded }; Busy = true }, [ OpenDataset dataset.DatasetId ]
        | None -> model, []
    | DatasetOpened(dataset, at) ->
        { model with
            Dataset = Some dataset
            Connectivity = Credential.Online
            LastVerified = Some at
            Busy = false },
        []
    | GroupUpdated group ->
        match model.Dataset with
        | Some dataset ->
            let groups =
                if dataset.Groups |> List.exists (fun g -> g.Key = group.Key) then
                    dataset.Groups |> List.map (fun g -> if g.Key = group.Key then group else g)
                else
                    dataset.Groups @ [ group ]

            { model with Dataset = Some { dataset with Groups = groups }; Busy = false; Route = Group group.Key }, [ Navigate(hashOf (Group group.Key)) ]
        | None -> { model with Busy = false }, []
    | Failed notice
    | Noted notice -> { model with Notice = Some notice; Busy = false }, []
    | WentOffline reason ->
        { model with
            Connectivity = Credential.Degraded(reason, model.LastVerified)
            Busy = false
            Notice = Some { Code = "SIGNAL.STORAGE.OFFLINE"; Message = "The data store cannot be reached. What is shown may be out of date; changes are unavailable."; Infrastructure = true } },
        []
    | ConflictFound conflict -> { model with Conflict = Some conflict; Busy = false }, []
    | LocationMoved hash -> { model with Route = routeOf hash }, []
    | Ui(name, key, value) -> onUi model name key value
