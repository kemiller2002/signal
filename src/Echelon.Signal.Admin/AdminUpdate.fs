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

/// The model with `operation` running, or None while another runs: no two
/// store operations overlap (the guard in `Operations`).
let private started (operation: Operations.Operation) (model: Model) : Model option =
    Operations.start operation model.Activity |> Option.map (fun activity -> { model with Activity = activity })

/// A person's activation: starts `operation` with its effects and clears the
/// last notice, or is ignored while an operation runs, so a second click
/// never queues a duplicate write.
let private launch (operation: Operations.Operation) (effects: Effect list) (model: Model) : Model * Effect list =
    match started operation model with
    | Some model -> { model with Notice = None }, effects
    | None -> model, []

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

/// Buttons that name their action in the event itself. Limen sends a value
/// only for form fields, never a button's `value` attribute, so a button
/// sending "transition" or "exportSnapshot" arrived with an empty value: the
/// lifecycle buttons were refused and the exports did nothing (GH-113,
/// UI-LAYOUT-INTERACTION ULI-A01). Each maps to the original event and value.
let private buttonActions =
    Map.ofList
        [ "closeGroup", ("transition", "close")
          "reopenGroup", ("transition", "reopen")
          "finalizeGroup", ("transition", "finalize")
          "sealGroup", ("transition", "seal")
          "exportReportJson", ("exportSnapshot", "report.json")
          "exportSectionsCsv", ("exportSnapshot", "sections.csv")
          "exportLineage", ("exportSnapshot", "lineage.json") ]

/// What the page does for an event.
let private onNamedEvent (model: Model) (name: string) (key: string option) (value: string) : Model * Effect list =
    let busy operation effects = launch operation effects model
    let routeKey = groupKey model
    let place = model.Place
    // A checkbox sends its value when checked and "" when not (protocol 1.2).
    let toggle (member': string) (members: string list) =
        if value = "" then members |> List.filter ((<>) member') else member' :: members |> List.distinct |> List.sort

    match name with
    // A confirmation whose change is running cannot be cancelled: it closes
    // when the change is answered (SIG-VIS-PENDING-002).
    | "cancelDeleteDraft"
    | "cancelRetire" when Operations.isRunning model.Activity -> model, []
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
        // While an open (such as the one after sign-in) is under way, a second
        // would race it and report its own setup as a change made elsewhere
        // (GH-113): it is ignored (`launch`), and the button says it is opening.
        | Some id when model.Principal.IsSome -> busy (Operations.Operation.OpenDataset id) [ OpenDataset id ]
        | _ -> model, []
    | "navigate" ->
        // A location the page names (`/groups`), as a link would.
        match key |> Option.map (RouteCodec.parse codec Router.allowAll) with
        | Some(Ok route) -> go (navigate place route) model
        | _ -> model, []
    | "openGroup" ->
        match key with
        | Some groupKey ->
            // The view moves at once; the reread waits for nothing else to run.
            let model, moves = go (navigate place (Group groupKey)) model
            let model, effects = launch (Operations.Operation.OpenGroup groupKey) [ OpenGroup groupKey ] model
            model, moves @ effects
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
            let origin = if texts.Length = 1 && model.ImportOrigin = ResultRecord.MultiPaste then ResultRecord.PastedUrl else model.ImportOrigin

            match started Operations.Operation.ImportArtifacts model with
            | Some model -> { model with ImportText = ""; Notice = None }, [ ImportArtifacts(groupKey, texts, origin) ]
            | None -> model, []
        | Some _ when texts.IsEmpty -> model, []
        | _ -> { model with Notice = refuse "SIGNAL.ADMIN.NOT_PERMITTED" "Importing is not available here now." }, []
    | "inviteCount" -> { model with Invite = { model.Invite with Count = value } }, []
    | "inviteLocale" -> { model with Invite = { model.Invite with Locale = value } }, []
    | "inviteExpires" -> { model with Invite = { model.Invite with Expires = value } }, []
    | "issueInvitations" ->
        match routeKey, Invitations.parse model.Invite with
        | Some groupKey, Ok(count, terms) when can model AdminState.CanCreateGroup -> busy Operations.Operation.IssueInvitations [ IssueInvitations(groupKey, count, terms) ]
        | Some _, Error problem -> { model with Notice = refuse "SIGNAL.INVITE.INVALID" problem }, []
        | _ -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Issuing invitations needs ManageGroups and a writable store." }, []
    | "saveInvitations" ->
        match model.Issued with
        | Some issued -> model, [ Download($"invitations-{issued.Links.Length}.csv", "text/csv", Invitations.csv issued.Links) ]
        | None -> model, []
    | "newGroupTemplate" -> { model with NewGroup = {| model.NewGroup with Template = value |} }, []
    | "newGroupMode" -> { model with NewGroup = {| model.NewGroup with Mode = (if value = "identified" then IdentifiedGroup else AnonymousGroup) |} }, []
    | "newGroupExpected" -> { model with NewGroup = {| model.NewGroup with Expected = positive value model.NewGroup.Expected |} }, []
    | "newGroupMinimum" -> { model with NewGroup = {| model.NewGroup with Minimum = positive value model.NewGroup.Minimum |} }, []
    | "createGroup" when can model AdminState.CanCreateGroup ->
        let g = model.NewGroup
        busy Operations.Operation.CreateGroup [ CreateGroup(g.Template, g.Mode, g.Expected, g.Minimum) ]
    | "createGroup" -> { model with Notice = refuse "SIGNAL.ADMIN.NOT_PERMITTED" "Creating groups needs ManageGroups and a writable store." }, []
    | "transition" ->
        let needed =
            match value with
            | "close" -> AdminState.CanCloseGroup
            | "reopen" -> AdminState.CanReopenGroup
            | "finalize" -> AdminState.CanFinalizeGroup
            | _ -> AdminState.CanSealGroup

        match routeKey with
        | Some groupKey when can model needed -> busy (Operations.Operation.TransitionGroup value) [ TransitionGroup(groupKey, value) ]
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

            match started Operations.Operation.Admit model with
            | Some model -> { model with Admit = {| model.Admit with Account = "" |}; Notice = None }, [ ChangeRoster(Admit(principal, grantOf model.Admit.Grant)) ]
            | None -> model, []
        else
            { model with Notice = refuse "SIGNAL.ACCESS.NOT_AN_ACCOUNT_NUMBER" "Enter the person's GitHub account number (digits only), never a login or e-mail." }, []
    | "removeAdministrator" when can model AdminState.CanManageAdministrators ->
        match key with
        | Some principalId -> busy (Operations.Operation.RemoveAdministrator principalId) [ ChangeRoster(Remove principalId) ]
        | None -> model, []
    | "admit"
    | "removeAdministrator" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "This needs ManageAdministrators and a writable store." }, []
    | "rebuildIndex" when can model AdminState.CanRepairIndex -> busy Operations.Operation.RebuildIndex [ RebuildIndex ]
    | "rebuildIndex" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Rebuilding the index needs ManageStorage." }, []
    | "refresh" ->
        match model.Dataset with
        | Some dataset -> busy Operations.Operation.Refresh [ OpenDataset dataset.DatasetId ]
        | None -> model, []
    | "dismissNotice" -> { model with Notice = None; Conflict = None }, []
    | "takeSnapshot" when can model AdminState.CanCreateSnapshot ->
        match place.View with
        | Ok(Report(g, family, locale)) -> busy Operations.Operation.TakeSnapshot [ TakeSnapshot(g, family, locale) ]
        | _ -> { model with Notice = refuse "SIGNAL.REPORT.NO_REPORT" "Open a group's report first." }, []
    | "takeSnapshot" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Taking a snapshot needs BuildReports and a writable store." }, []
    | "exportSnapshot" when can model AdminState.CanExport ->
        match key with
        | Some id when [ "report.json"; "sections.csv"; "lineage.json" ] |> List.contains value -> busy (Operations.Operation.ExportSnapshot(id, value)) [ ExportSnapshot(id, value) ]
        | _ -> model, []
    | "exportSnapshot" -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Exporting needs ExportData." }, []
    // The respondent site's copy of a published version (DF-SIGNAL-2026-0005):
    // its canonical bytes, named by its reference, to commit to published-templates/.
    | "testLinks" ->
        match key |> Option.bind (Echelon.Signal.Engine.Publication.resolveHash model.Templates.Catalog) with
        | Some t -> { model with TestLinks = Some(TestLinks.forVersion t) }, []
        | None -> { model with Notice = refuse "SIGNAL.TEMPLATE.NOT_FOUND" "That version is not in the catalog." }, []
    | "downloadSiteFile" ->
        match key |> Option.bind (Echelon.Signal.Engine.Publication.resolveHash model.Templates.Catalog) with
        | Some t ->
            let path, bytes = Echelon.Signal.Engine.GenericSession.publishedFile t.SurveyId t.Version t.Content
            model, [ Download(path.Substring(path.LastIndexOf '/' + 1), "application/json", System.Text.Encoding.UTF8.GetString bytes) ]
        | None -> { model with Notice = refuse "SIGNAL.TEMPLATE.NOT_FOUND" "That version is not in the catalog." }, []
    | building when ReportBuilder.events.Contains building ->
        let surveys = model.Catalog |> List.map _.SurveyIdentifier |> List.distinct
        let screen, commands = ReportBuilder.update model.Library surveys name key value model.Builder
        let model = { model with Builder = screen; Notice = None }

        match commands with
        | [ ReportBuilder.SaveDefinition(d, p) ] when can model AdminState.CanBuildReport -> launch Operations.Operation.SaveReportDefinition [ SaveReportDefinition(d, p) ] model
        | [ _ ] -> { model with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "Saving a report definition needs BuildReports and a writable store." }, []
        | _ -> model, []
    | authoring when Authoring.events.Contains authoring ->
        let survey = match place.View with Ok(Draft s) -> Some s | _ -> None
        let screen, commands = Authoring.update model.Templates survey name key value model.Authoring
        let model = { model with Authoring = screen; Notice = None }
        let permitted capability = can model capability

        // A write starts its operation, or is ignored while one runs (`launch`).
        let write operation effect (m: Model, effects) =
            let m, begun = launch operation [ effect ] m
            m, effects @ begun

        commands
        |> List.fold
            (fun (m: Model, effects) command ->
                match command with
                | Authoring.OpenDraft s -> let m, moves = go (navigate m.Place (Draft s)) m in m, effects @ moves
                | Authoring.SaveDraft d when permitted AdminState.CanEditDrafts -> write Operations.Operation.SaveDraft (SaveDraft d) (m, effects)
                | Authoring.PublishDraft(d, a) when permitted AdminState.CanPublishTemplates -> write Operations.Operation.PublishDraft (PublishDraft(d, a)) (m, effects)
                | Authoring.HideVersion(s, v) when permitted AdminState.CanPublishTemplates -> write (Operations.Operation.HideVersion(s, v)) (HideTemplate(s, v)) (m, effects)
                | Authoring.RequestReview s when permitted AdminState.CanEditDrafts -> write Operations.Operation.RequestReview (RequestReview s) (m, effects)
                | Authoring.ApproveReview d when permitted AdminState.CanReviewTemplates -> write Operations.Operation.ApproveReview (ApproveReview d) (m, effects)
                | Authoring.DeleteDraft s when permitted AdminState.CanEditDrafts -> write Operations.Operation.DeleteDraft (DeleteDraft s) (m, effects)
                | _ -> { m with Notice = refuse "SIGNAL.ACCESS.CAPABILITY_NOT_HELD" "This needs EditDrafts (saving, asking for review), ReviewTemplates (approving) or PublishTemplates (publishing, hiding) and a writable store." }, effects)
            (model, [])
    // The draft preview (AUT-003 §§20-22): the survey page's engine on the
    // draft, as a test artifact; nothing is stored or sent.
    | previewing when DraftPreview.events.Contains previewing ->
        let survey = match place.View with Ok(Draft s) -> Some s | _ -> None
        let editor = survey |> Option.bind (fun s -> Authoring.current model.Templates s model.Authoring)
        let screen = model.Authoring
        let set preview = { model with Authoring = { screen with Preview = preview; Problem = None } }, []

        match name, editor, screen.Preview with
        | "previewStart", Some e, _ -> set (Some(DraftPreview.start e.Draft (model.LastVerified |> Option.defaultValue DateTimeOffset.UnixEpoch)))
        | "previewClose", _, _ -> set None
        | "previewKeep", Some e, Some session ->
            match DraftPreview.answers session with
            | Some answers when not answers.IsEmpty ->
                match Authoring.apply (Authoring.AddFixture(DraftPreview.fixtureOf e.Draft answers)) e with
                | Ok next -> { model with Authoring = { screen with Editor = Some next; Problem = None } }, []
                | Error problem -> { model with Authoring = { screen with Problem = Some problem } }, []
            | _ -> { model with Authoring = { screen with Problem = Some "Answer something in the preview first." } }, []
        | _, _, Some session -> set (Some(DraftPreview.step name key value session))
        | _ -> model, []
    | retention when RetentionScreen.events.Contains retention ->
        let selected =
            match routeKey, model.Dataset with
            | Some k, Some d -> d.Groups |> List.tryFind (fun g -> g.Key = k)
            | _ -> None

        // A target button sits in a row keyed by its target; Limen sends that
        // key and never a button's value (GH-113).
        let target = if value = "" then defaultArg key "" else value
        RetentionScreen.update (can model) selected name target model
    | other -> invalidOp $"The administrator page sent an event the engine does not know: '{other}'"

/// The next model and the effects to perform.
/// What the page does for an event, a button's own action event included.
let private onUi (model: Model) (name: string) (key: string option) (value: string) : Model * Effect list =
    match Map.tryFind name buttonActions with
    | Some(original, action) -> onNamedEvent model original key action
    | None -> onNamedEvent model name key value

let private apply (msg: Msg) (model: Model) : Model * Effect list =
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
        let model, moves = settleAccess { model with SignIn = signIn; Principal = principal }

        match principal, model.Deployment with
        | Some _, Some config ->
            match config.Datasets with
            | [ only ] ->
                match started (Operations.Operation.OpenDataset only.Id) model with
                | Some opening -> opening, moves @ [ OpenDataset only.Id ]
                | None -> model, moves
            | _ -> model, moves
        | None, _ -> { model with Dataset = None }, moves
        | _ -> model, moves
    | TabNoticed notice ->
        match model.Dataset with
        | Some dataset ->
            let downgraded = Credential.afterNotice notice dataset.Credential

            match notice with
            | Credential.SignedOutElsewhere -> settleAccess { model with Dataset = None; Principal = None; SignIn = Credential.SignedOut }
            | _ ->
                let model = { model with Dataset = Some { dataset with Credential = downgraded } }

                // Reloaded now; while an operation runs, the reload would race it
                // (`Operations`), so the page asks for a refresh once it is answered.
                match started Operations.Operation.Refresh (if Operations.isRunning model.Activity then model else { model with Activity = Operations.oweReload model.Activity }) with
                | Some model -> model, [ OpenDataset dataset.DatasetId ]
                | None -> { model with Activity = Operations.oweReload model.Activity }, []
        | None -> model, []
    | DatasetOpened(dataset, at) ->
        { model with
            Dataset = Some dataset
            Connectivity = Credential.Online
            LastVerified = Some at },
        []
    | CatalogLoaded(catalog, listing) ->
        let template = if catalog |> List.exists (fun e -> e.Hash = model.NewGroup.Template) then model.NewGroup.Template else catalog |> List.tryHead |> Option.map _.Hash |> Option.defaultValue ""
        { model with Catalog = catalog; Templates = listing; NewGroup = {| model.NewGroup with Template = template |} }, []
    | SnapshotsLoaded snapshots -> { model with Snapshots = snapshots }, []
    | LibraryLoaded library -> { model with Library = library }, []
    | DraftDeleted survey -> { model with Authoring = Authoring.deleted survey model.Authoring }, []
    | DefinitionSaved entry -> { model with Builder = ReportBuilder.saved entry model.Builder }, []
    | ExportReady(name, mime, data) -> model, [ Download(name, mime, data) ]
    | DraftSaved stored -> { model with Authoring = Authoring.saved stored model.Authoring }, []
    | DraftPublished(survey, _) -> { model with Authoring = Authoring.published survey model.Authoring }, []
    | GroupUpdated group ->
        match model.Dataset with
        | Some dataset ->
            let groups =
                if dataset.Groups |> List.exists (fun g -> g.Key = group.Key) then
                    dataset.Groups |> List.map (fun g -> if g.Key = group.Key then group else g)
                else
                    dataset.Groups @ [ group ]

            let model = { model with Dataset = Some { dataset with Groups = groups } }
            // A new group opens; an update to the group in view stays where it is.
            if groupKey model = Some group.Key then model, [] else go (navigate model.Place (Group group.Key)) model
        | None -> model, []
    | InvitationsIssued(group, links) -> { model with Issued = Some {| Group = group; Links = links |} }, []
    | Failed notice
    | Noted notice -> { model with Notice = Some notice }, []
    | WentOffline reason ->
        { model with
            Connectivity = Credential.Degraded(reason, model.LastVerified)
            Notice = Some { Code = "SIGNAL.STORAGE.OFFLINE"; Message = "The data store cannot be reached. What is shown may be out of date; changes are unavailable."; Infrastructure = true } },
        []
    | ConflictFound conflict -> { model with Conflict = Some conflict }, []
    | LocationMoved page -> go (arrive (access model) model.Place page) model
    | ReturnRecalled target ->
        match target |> Option.bind (ReturnTo.capture table) with
        | Some target -> settleAccess { model with ReturnTarget = Some target }
        | None -> model, [ ForgetReturn ]
    | LinkCopied true -> { model with Notice = Some { Code = "SIGNAL.LINK.COPIED"; Message = "Link copied. It opens this view for anyone who may see it."; Infrastructure = false } }, []
    | LinkCopied false ->
        { model with Notice = Some { Code = "SIGNAL.LINK.NOT_COPIED"; Message = "The browser did not allow copying. Copy the address from the address bar instead."; Infrastructure = false } }, []
    | Ui(name, key, value) -> onUi model name key value
    // Settled as unknown before it is applied (`AdminApp.answer`); the wire
    // presents the fault itself through Aegis.
    | OperationFaulted -> model, []

/// A confirmation held open while its change ran closes once it is answered.
let private closeConfirmation (operation: Operations.Operation) (model: Model) =
    match operation with
    | Operations.Operation.DeleteDraft -> { model with Authoring = { model.Authoring with ConfirmDelete = None } }
    | Operations.Operation.RetireSources -> { model with RetireConfirm = None }
    | _ -> model

/// Where focus goes once a change that moves or removes the control it was
/// on has answered: to what the change produced, beside where it was made
/// (SIG-VIS-PENDING-003). A retire that worked lands on the open group's
/// retired results, shown once the store holds its record; otherwise (also
/// a group with nothing to retire) back on the button that starts it. A
/// group or dataset transition lands on the state it changed. A deleted
/// draft or hidden version leaves nothing beside it, so focus goes to the
/// outcome shown beside the actions it was made with (.admin-outcome; the
/// status region #activity, which announces it, sits at the top of the page,
/// out of view on a long one: signal#136 review round 2).
let private focusAfter (operation: Operations.Operation) (outcome: Operations.Outcome) (model: Model) =
    let retiredShown =
        match groupKey model, model.Dataset with
        | Some key, Some dataset -> dataset.Groups |> List.exists (fun g -> g.Key = key && g.SourcesRetired.IsSome)
        | _ -> false

    match operation, outcome with
    | Operations.Operation.RetireSources, Operations.Outcome.Confirmed when retiredShown -> [ Focus "sources-retired" ]
    | Operations.Operation.RetireSources, _ -> [ Focus "retire-sources" ]
    | Operations.Operation.TransitionGroup _, _ -> [ Focus "group-phase" ]
    | Operations.Operation.TransitionDataset _, _ -> [ Focus "dataset-lifecycle" ]
    | Operations.Operation.DeleteDraft, _
    | Operations.Operation.HideVersion _, _ -> [ Focus "outcome" ]
    | _ -> []

/// The next model and the effects to perform.
let update (msg: Msg) (model: Model) : Model * Effect list =
    match model.Activity.Running |> Option.bind (fun running -> AdminApp.answer running msg |> Option.map (fun outcome -> running, outcome)) with
    | Some(running, outcome) ->
        let model, effects = apply msg (closeConfirmation running { model with Activity = Operations.settle outcome model.Activity })
        model, effects @ focusAfter running outcome model
    | None -> apply msg model
