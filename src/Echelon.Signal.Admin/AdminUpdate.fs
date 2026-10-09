/// The administrator page's update (ADM-002, ADM-031..033, ADM-035): what
/// each message and page event does to the model and which effects it asks
/// for. Kept apart from `AdminApp` (its state, messages, effects and the
/// capabilities it derives) so each stays readable.
///
/// Pure.
module Echelon.Signal.Admin.AdminUpdate

open System
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.Routes
open Limen.Routing
open Echelon.Signal.Admin.AdminNavigation
open Echelon.Signal.Admin.AdminTypes
open Echelon.Signal.Admin.AdminApp

let private go (moved: Place * Move option) (model: Model) =
    let place, move = moved
    { model with Place = place }, (move |> Option.map Go |> Option.toList)

/// After the person or the access changed: resume a return target once
/// signed in, or send a signed-out person to sign-in (SIG-LINK-006).
let private settleAccess (model: Model) : Model * Effect list =
    match model.Principal, model.Place.View, model.ReturnTarget with
    | Some _, Ok(AdminRoute.SignIn target), _ -> go (resume model.Place (target |> Option.orElse model.ReturnTarget)) { model with ReturnTarget = None }
    | Some _, _, Some target ->
        let model, effects = go (resume model.Place (Some target)) { model with ReturnTarget = None }
        model, effects @ [ ForgetReturn ]
    | Some _, _, None -> model, []
    | None, _, _ -> go (reconsider (access model) model.Place) model

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
    let routeKey = groupKey model
    let place = model.Place
    // A checkbox sends its value when checked and "" when not (protocol 1.2).
    let toggle (member': string) (members: string list) =
        if value = "" then members |> List.filter ((<>) member') else member' :: members |> List.distinct |> List.sort

    match name with
    | "retention" -> { model with Retention = (if value = "tab" then Credential.ThisTab else Credential.ThisPage) }, []
    | "signIn" when model.SignIn <> Credential.SigningIn ->
        match model.Deployment |> Option.bind _.Identity with
        | Some _ ->
            // The view to return to crosses the provider's round trip in this tab (SIG-LINK-006).
            let remember = returnTarget place |> Option.map RememberReturn |> Option.toList
            { model with SignIn = Credential.SigningIn }, remember @ [ SignIn model.Retention ]
        | None -> model, []
    | "signIn" -> model, []
    | "signOut" -> { model with Dataset = None }, [ SignOut ]
    | "openDataset" ->
        match key with
        | Some id when model.Principal.IsSome -> busy [ OpenDataset id ]
        | _ -> model, []
    | "navigate" ->
        // A location the page names (`/groups`), as a link would.
        match key |> Option.map (RouteCodec.parse codec Router.allowAll) with
        | Some(Ok route) -> go (navigate place route) model
        | _ -> model, []
    | "openGroup" ->
        match key with
        | Some groupKey ->
            let model, moves = go (navigate place (Group groupKey)) { model with Busy = true; Notice = None }
            model, moves @ [ OpenGroup groupKey ]
        | None -> model, []
    | "exploreSort"
    | "exploreDisplay"
    | "exploreSection" ->
        // Refinements of the results view: replace, so Back leaves the view (SIG-LINK-004).
        match place.View with
        | Ok(Results(g, view)) ->
            let next =
                match name with
                | "exploreSort" -> { view with Sort = (if value = "value" then ByValue else ByOrder) }
                | "exploreDisplay" -> { view with Display = (if value = "percent" then Percentages else Counts) }
                | _ -> { view with Section = (match key with Some "all" | None -> None | Some section -> Some section) }

            go (refine place (Results(g, next))) model
        | _ -> model, []
    | "filterStatus"
    | "filterMode"
    | "filterSurvey" ->
        match place.View, key with
        | Ok(Groups filter), _ when name = "filterSurvey" -> go (refine place (Groups { filter with Survey = (if value = "" then None else Some value) })) model
        | Ok(Groups filter), Some item when name = "filterStatus" -> go (refine place (Groups { filter with Status = toggle item filter.Status })) model
        | Ok(Groups filter), Some item -> go (refine place (Groups { filter with Mode = toggle item filter.Mode })) model
        | _ -> model, []
    | "filterOutcome" ->
        match place.View, key with
        | Ok(Imports(g, outcomes)), Some item -> go (refine place (Imports(g, toggle item outcomes))) model
        | _ -> model, []
    | "compareGroup"
    | "compareSection" ->
        match place.View, key with
        | Ok(Compare(groups, section)), _ when name = "compareSection" -> go (refine place (Compare(groups, (if value = "" then None else Some value)))) model
        | Ok(Compare(groups, section)), Some g -> go (refine place (Compare(toggle g groups, section))) model
        | _ -> model, []
    | "copyLink" -> model, [ CopyLink(shareLink place) ]
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
    | "takeSnapshot" when can model AdminState.CanCreateSnapshot ->
        match place.View with
        | Ok(Report(g, family, locale)) -> busy [ TakeSnapshot(g, family, locale) ]
        | _ -> { model with Notice = refuse "SIGNAL.REPORT.NO_REPORT" "Open a group's report first." }, []
    | "takeSnapshot" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Taking a snapshot needs BuildReports and a writable store." }, []
    | "exportSnapshot" when can model AdminState.CanExport ->
        match key with
        | Some id when [ "report.json"; "sections.csv"; "lineage.json" ] |> List.contains value -> busy [ ExportSnapshot(id, value) ]
        | _ -> model, []
    | "exportSnapshot" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Exporting needs ExportData." }, []
    | building when ReportBuilder.events.Contains building ->
        let surveys = model.Catalog |> List.map _.SurveyIdentifier |> List.distinct
        let screen, commands = ReportBuilder.update model.Library surveys name key value model.Builder
        let model = { model with Builder = screen; Notice = None }

        match commands with
        | [ ReportBuilder.SaveDefinition(d, p) ] when can model AdminState.CanBuildReport -> busy [ SaveReportDefinition(d, p) ]
        | [ _ ] -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Saving a report definition needs BuildReports and a writable store." }, []
        | _ -> model, []
    | authoring when Authoring.events.Contains authoring ->
        let survey = match place.View with Ok(Draft s) -> Some s | _ -> None
        let screen, commands = Authoring.update model.Templates survey name key value model.Authoring
        let model = { model with Authoring = screen; Notice = None }
        let permitted capability = can model capability

        commands
        |> List.fold
            (fun (m: Model, effects) command ->
                match command with
                | Authoring.OpenDraft s -> let m, moves = go (navigate m.Place (Draft s)) m in m, effects @ moves
                | Authoring.SaveDraft d when permitted AdminState.CanEditDrafts -> { m with Busy = true }, effects @ [ SaveDraft d ]
                | Authoring.PublishDraft(d, a) when permitted AdminState.CanPublishTemplates -> { m with Busy = true }, effects @ [ PublishDraft(d, a) ]
                | Authoring.HideVersion(s, v) when permitted AdminState.CanPublishTemplates -> { m with Busy = true }, effects @ [ HideTemplate(s, v) ]
                | Authoring.RequestReview s when permitted AdminState.CanEditDrafts -> { m with Busy = true }, effects @ [ RequestReview s ]
                | Authoring.ApproveReview d when permitted AdminState.CanReviewTemplates -> { m with Busy = true }, effects @ [ ApproveReview d ]
                | _ -> { m with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "This needs EditDrafts (saving, asking for review), ReviewTemplates (approving) or PublishTemplates (publishing, hiding) and a writable store." }, effects)
            (model, [])
    | other -> invalidOp $"The administrator page sent an event the engine does not know: '{other}'"

/// The next model and the effects to perform.
let update (msg: Msg) (model: Model) : Model * Effect list =
    match msg with
    | Started(page, query) ->
        // A provider callback is consumed and removed from the address by
        // Fides; the view to return to was kept in this tab (SIG-LINK-006).
        let callback = query |> List.exists (fun (k, _) -> k = "state")
        let model, moves = go (arrive (access model) model.Place page) { model with CallbackQuery = query }
        let place = if callback then withoutCallback model.Place else model.Place
        { model with Place = place }, moves @ [ ReadConfiguration ] @ (if callback then [ RecallReturn ] else [])
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
        let model, moves = settleAccess { model with SignIn = signIn; Principal = principal; Busy = false }

        match principal, model.Deployment with
        | Some _, Some config ->
            match config.Datasets with
            | [ only ] -> { model with Busy = true }, moves @ [ OpenDataset only.Id ]
            | _ -> model, moves
        | None, _ -> { model with Dataset = None }, moves
        | _ -> model, moves
    | TabNoticed notice ->
        match model.Dataset with
        | Some dataset ->
            let downgraded = Credential.afterNotice notice dataset.Credential

            match notice with
            | Credential.SignedOutElsewhere -> settleAccess { model with Dataset = None; Principal = None; SignIn = Credential.SignedOut }
            | _ -> { model with Dataset = Some { dataset with Credential = downgraded }; Busy = true }, [ OpenDataset dataset.DatasetId ]
        | None -> model, []
    | DatasetOpened(dataset, at) ->
        { model with
            Dataset = Some dataset
            Connectivity = Credential.Online
            LastVerified = Some at
            Busy = false },
        []
    | CatalogLoaded(catalog, listing) ->
        let template = if catalog |> List.exists (fun e -> e.Hash = model.NewGroup.Template) then model.NewGroup.Template else catalog |> List.tryHead |> Option.map _.Hash |> Option.defaultValue ""
        { model with Catalog = catalog; Templates = listing; NewGroup = {| model.NewGroup with Template = template |} }, []
    | SnapshotsLoaded snapshots -> { model with Snapshots = snapshots; Busy = false }, []
    | LibraryLoaded library -> { model with Library = library }, []
    | DefinitionSaved entry -> { model with Builder = ReportBuilder.saved entry model.Builder; Busy = false }, []
    | ExportReady(name, mime, data) -> { model with Busy = false }, [ Download(name, mime, data) ]
    | DraftSaved survey -> { model with Authoring = Authoring.saved survey model.Authoring; Busy = false }, []
    | DraftPublished(survey, _) -> { model with Authoring = Authoring.published survey model.Authoring; Busy = false }, []
    | GroupUpdated group ->
        match model.Dataset with
        | Some dataset ->
            let groups =
                if dataset.Groups |> List.exists (fun g -> g.Key = group.Key) then
                    dataset.Groups |> List.map (fun g -> if g.Key = group.Key then group else g)
                else
                    dataset.Groups @ [ group ]

            let model = { model with Dataset = Some { dataset with Groups = groups }; Busy = false }
            // A new group opens; an update to the group in view stays where it is.
            if groupKey model = Some group.Key then model, [] else go (navigate model.Place (Group group.Key)) model
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
    | LocationMoved page -> go (arrive (access model) model.Place page) model
    | ReturnRecalled target ->
        match target |> Option.bind (ReturnTo.capture table) with
        | Some target -> settleAccess { model with ReturnTarget = Some target }
        | None -> model, [ ForgetReturn ]
    | LinkCopied true -> { model with Notice = Some { Code = "SIGNAL.LINK.COPIED"; Message = "Link copied. It opens this view for anyone who may see it."; Infrastructure = false } }, []
    | LinkCopied false ->
        { model with Notice = Some { Code = "SIGNAL.LINK.NOT_COPIED"; Message = "The browser did not allow copying. Copy the address from the address bar instead."; Infrastructure = false } }, []
    | Ui(name, key, value) -> onUi model name key value
