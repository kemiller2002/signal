/// The administrator page's side of the Limen boundary: kernel messages to
/// engine messages, engine effects to work (Fides sign-in, Arca storage)
/// and Limen requests, every step under the Aegis boundary (ADM-035,
/// ADM-077). Deterministic for a given `Env`: the clock, keys, randomness,
/// the storage backend and the identity client arrive as values; the only
/// effectful code is the composition root (`Runtime`).
module Echelon.Signal.Application.AdminWire

open System
open System.Runtime.ExceptionServices
open System.Text.Json.Nodes
open Aegis
open Fides.Client
open Echelon.Signal.Admin
open Echelon.Signal.Application.Json
open Echelon.Signal.Application.Boundary
open Echelon.Signal.Application.AdminProtocol
open Echelon.Signal.Application.AdminWork
open Echelon.Signal.Application.AdminWireState

type Purpose = AdminWireState.Purpose
type State = AdminWireState.State
let initial = AdminWireState.initial

/// Starts the work an engine effect asks for; its outcome arrives later as
/// `Outcome`s through the bridge. Effects the kernel performs directly
/// become Limen requests.
let private perform (env: Env) (state: State) (effect: AdminApp.Effect) : State * Request list =
    // Background work (identity, sign-in, sign-out, local answers) versus the
    // running operation's own work, whose failure alone settles it.
    let start (work: Async<Outcome list>) = env.Bridge.Start work
    let operate (work: Async<Outcome list>) = env.Bridge.StartOperation work
    let model = state.Model |> Option.defaultValue (AdminApp.initial env.Catalog)
    let now = env.Now()
    let mint purpose (state: State) =
        let sequence = state.Sequence + 1
        let id = $"admin-{sequence}"
        id, { state with Sequence = sequence; Pending = state.Pending.Add(id, purpose) }

    let withDataset (work: Store.Actor -> Store.Opened -> Async<Outcome list>) =
        match actorOf env model, state.Opened with
        | Some actor, Some opened -> operate (work actor opened)
        | _ -> operate (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NO_DATASET" "Open a dataset first." false)) ])

    let withGroup (key: string) (work: Store.Actor -> GroupStore.OpenedGroup -> Async<Outcome list>) =
        match actorOf env model, state.Groups.TryFind key with
        | Some actor, Some group -> operate (work actor group)
        | _ -> operate (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NO_GROUP" "That group is not open." false)) ])

    match effect with
    | AdminApp.ReadConfiguration when List.contains "Http" state.Effects ->
        let id, state = mint Configuration state
        state, [ Http(id, "GET", configurationUrl state, [], None, RequestTimeoutMs, []) ]
    | AdminApp.ReadConfiguration ->
        start (async.Return [ ToEngine(AdminApp.ConfigurationRead None) ])
        state, []
    | AdminApp.BeginIdentity(config, query) ->
        let client = env.Identity config (AdminPorts.fidesPorts env.Bridge env.Now env.RandomBytes config.Exchange)

        start (
            async {
                let! signIn, principal = Identity.start client query
                return [ ToEngine(AdminApp.IdentityChanged(signIn, principal)) ]
            }
        )

        { state with Client = Some client }, []
    | AdminApp.SignIn retention ->
        state.Client
        |> Option.iter (fun client ->
            start (
                async {
                    match! client.SignIn(Identity.retentionOf retention) with
                    | Ok() -> return []
                    | Error reason -> return [ ToEngine(AdminApp.IdentityChanged(Credential.SignedOut, None)); ToEngine(AdminApp.Failed(notice "SIGNAL.SIGN_IN.FAILED" reason true)) ]
                }
            ))

        state, []
    | AdminApp.SignOut ->
        state.Client
        |> Option.iter (fun client ->
            start (
                async {
                    let! _ = client.SignOut()
                    return [ ToEngine(AdminApp.IdentityChanged(Credential.SignedOut, None)) ]
                }
            ))

        { state with Opened = None; Catalog = TemplateCatalog.builtIn env.Catalog; Snapshots = []; Groups = Map.empty }, []
    | AdminApp.OpenDataset datasetId ->
        match model.Deployment, actorOf env model, state.Client with
        | Some config, Some actor, Some client ->
            let backend = env.Backend (fun () -> Some(Identity.tokens client)) (fun () -> client.ReportUnauthorized())
            let pinned = state.Opened |> Option.filter (fun o -> o.DatasetId = datasetId) |> Option.map _.RepositoryId
            operate (AdminGroupWork.openDataset env state.Catalog backend config actor pinned datasetId now)
        | _ -> operate (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NOT_SIGNED_IN" "Sign in first." false)) ])

        state, []
    | AdminApp.CreateGroup(templateHash, mode, expected, minimum) ->
        withDataset (fun actor opened -> AdminGroupWork.create env state.Catalog actor opened templateHash mode expected minimum now)
        state, []
    | AdminApp.OpenGroup key ->
        withGroup key (fun _ group -> AdminGroupWork.reopen state.Catalog group now)
        state, []
    | AdminApp.ImportArtifacts(key, texts, origin) ->
        withGroup key (fun actor group -> AdminGroupWork.import state.Catalog actor group origin texts now)
        state, []
    | AdminApp.IssueInvitations(key, count, terms) ->
        withGroup key (fun _ group -> AdminGroupWork.invite env group count terms (state.Origin + state.Path) now)
        state, []
    | AdminApp.TransitionGroup(key, name) ->
        withGroup key (fun actor group -> AdminGroupWork.transition env actor group name now)
        state, []
    | AdminApp.TransitionDataset target ->
        match model.Deployment, actorOf env model, state.Client, state.Opened with
        | Some config, Some actor, Some client, Some opened ->
            let backend = env.Backend (fun () -> Some(Identity.tokens client)) (fun () -> client.ReportUnauthorized())
            operate (RetentionWork.transition env state.Catalog backend config actor opened target now)
        | _ -> operate (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NOT_SIGNED_IN" "Sign in and open the dataset first." false)) ])

        state, []
    | AdminApp.RetireSources key ->
        withGroup key (fun actor group -> RetentionWork.retire env state.Catalog actor group now)
        state, []
    | AdminApp.SaveDraft draft ->
        withDataset (fun actor opened -> AuthoringWork.save env state.Catalog actor opened draft now)
        state, []
    | AdminApp.PublishDraft(draft, acknowledged) ->
        withDataset (fun actor opened -> AuthoringWork.publish env actor opened draft acknowledged now)
        state, []
    | AdminApp.HideTemplate(survey, version) ->
        withDataset (fun actor opened -> AuthoringWork.hide env actor opened survey version now)
        state, []
    | AdminApp.RequestReview survey ->
        withDataset (fun actor opened -> AuthoringWork.requestReview env actor opened survey now)
        state, []
    | AdminApp.DeleteDraft survey ->
        withDataset (fun actor opened -> AuthoringWork.deleteDraft env actor opened survey now)
        state, []
    | AdminApp.ApproveReview draft ->
        withDataset (fun actor opened -> AuthoringWork.approveReview env actor opened draft now)
        state, []
    | AdminApp.TakeSnapshot(key, family, locale) ->
        withGroup key (fun actor group -> ReportWork.take state.Catalog actor model group family locale now)
        state, []
    | AdminApp.ExportSnapshot(id, file) ->
        match state.Snapshots |> List.tryFind (fun s -> s.SnapshotId = id) with
        | Some snapshot -> withDataset (fun actor opened -> ReportWork.export actor opened snapshot file now)
        | None -> operate (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.REPORT.NO_SUCH_SNAPSHOT" "That snapshot is not in this dataset." false)) ])

        state, []
    | AdminApp.SaveReportDefinition(definition, pins) ->
        withDataset (fun actor opened -> ReportWork.saveDefinition actor opened definition pins now)
        state, []
    | AdminApp.Download(name, mime, data) when negotiated files state ->
        let id, state = mint Downloading state
        state, [ Download(id, name, mime, data) ]
    | AdminApp.Download _ ->
        start (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.REPORT.NO_DOWNLOADS" "This browser page cannot save files; the export was not saved." false)) ])
        state, []
    | AdminApp.ChangeRoster command ->
        withDataset (fun actor opened ->
            async {
                match! Store.changeRoster actor command now opened with
                | Ok next ->
                    let! _ = env.Bridge.Call(Bridge.Announce(Credential.announcement opened.DatasetId Credential.RosterChangedElsewhere))
                    return [ RosterChanged next ]
                | Error(Store.RefusedByRules(refusal :: _)) -> return [ ToEngine(AdminApp.Failed(notice (Access.refusalCode refusal) (Access.explainRefusal refusal) false)) ]
                | Error(Store.Offline meaning) -> return [ ToEngine(AdminApp.WentOffline meaning.Message) ]
                | Error(Store.Failed meaning) -> return [ ToEngine(AdminApp.Failed(notice meaning.Code meaning.Message true)) ]
                | Error Store.ConflictPersists ->
                    return [ ToEngine(AdminApp.Failed(notice "SIGNAL.STORAGE.CONFLICT" "The administrators changed elsewhere and the change no longer applies; it was not saved." false)) ]
                // Unknown, not refused: the provider could not say whether it
                // landed. The page sends no change until a reload has shown what
                // the store holds (`Operations.Reconcile`, ARX-015).
                | Error(Store.OutcomeStillUnknown _) ->
                    return
                        [ ToEngine(
                              AdminApp.Failed(
                                  notice Echelon.Signal.Admin.Operations.OutcomeUnknownCode "The provider did not say whether the change to the administrators was saved. Refresh to check what the store holds; no change is sent until then." true
                              )
                          ) ]
                | Error other -> return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ACCESS.REFUSED" $"%A{other}" false)) ]
            })

        state, []
    | AdminApp.RebuildIndex ->
        withDataset (fun actor opened ->
            async {
                match! GroupStore.rebuildIndex actor now opened with
                | Ok index -> return [ ToEngine(AdminApp.Noted(notice "SIGNAL.INDEX.REBUILT" $"The contribution index was rebuilt from {index.Source.Count} record(s)." false)) ]
                | Error failure -> return [ ToEngine(groupFailure failure) ]
            })

        state, []
    // Relative `#/…` URLs: same-origin by construction (SIG-LINK-002).
    | AdminApp.Go move ->
        let id, state = mint Navigation state

        match move with
        | AdminNavigation.Push url -> state, [ Push(id, url) ]
        | AdminNavigation.Replace url -> state, [ Replace(id, url) ]
    | AdminApp.CopyLink url when List.contains "Clipboard" state.Effects ->
        let id, state = mint Copying state
        state, [ Copy(id, url) ]
    | AdminApp.CopyLink _ ->
        start (async.Return [ ToEngine(AdminApp.LinkCopied false) ])
        state, []
    // The return target lives in this tab only, through signal.host (DF-SIGNAL-2026-0003 §3).
    | AdminApp.RememberReturn target when negotiated host state ->
        let id, state = mint ReturnTarget state
        state, [ Host(id, "tabSet", [ "key", ReturnTargetKey; "value", target ]) ]
    | AdminApp.RecallReturn when negotiated host state ->
        let id, state = mint ReturnTarget state
        state, [ Host(id, "tabGet", [ "key", ReturnTargetKey ]) ]
    | AdminApp.ForgetReturn when negotiated host state ->
        let id, state = mint Navigation state
        state, [ Host(id, "tabRemove", [ "key", ReturnTargetKey ]) ]
    | AdminApp.RememberReturn _
    | AdminApp.RecallReturn
    | AdminApp.ForgetReturn -> state, []
    // Focus moves only where the kernel offers the focus pack; elsewhere the
    // status still says what happened.
    | AdminApp.Focus target when negotiated focus state ->
        let id, state = mint Focusing state
        state, [ Focus(id, target) ]
    | AdminApp.Focus _ -> state, []

// ---- The loop ---------------------------------------------------------------------------------

let private view (state: State) =
    let page = state.Model |> Option.map AdminView.project |> Option.defaultValue []
    page @ faultView state.Fault

let private localConfig: Deployment.DeploymentConfig =
    { Environment = Arca.EnvironmentKind.Local
      EnvironmentName = "local"
      Identity = None
      Profiles = []
      Datasets = [] }

/// What finished work brought back: kept state, and engine messages.
let private absorb (env: Env) (state: State) (outcome: Outcome) : State * AdminApp.Msg list =
    match outcome, state.Model with
    | ToEngine msg, _ -> state, [ msg ]
    | DatasetReady(opened, groups, catalog), Some model ->
        let config = model.Deployment |> Option.defaultValue localConfig
        let summaries = groups |> List.map (GroupAdmin.summary 0 None)

        { state with
            Opened = Some opened
            Catalog = catalog
            Groups = groups |> List.map (fun g -> GroupRecord.groupKey g.Config.Group, g) |> Map.ofList },
        [ AdminApp.CatalogLoaded(catalog.Offered, catalog.Listing); AdminApp.DatasetOpened(GroupAdmin.datasetSummary config opened summaries, env.Now()) ]
    | GroupReady(group, imported, unreconciled), _ ->
        { state with Groups = state.Groups.Add(GroupRecord.groupKey group.Config.Group, group) }, [ AdminApp.GroupUpdated(GroupAdmin.summary unreconciled imported group) ]
    | RosterChanged opened, Some model ->
        let config = model.Deployment |> Option.defaultValue localConfig
        let summaries = state.Groups |> Map.toList |> List.map (snd >> GroupAdmin.summary 0 None)
        { state with Opened = Some opened }, [ AdminApp.DatasetOpened(GroupAdmin.datasetSummary config opened summaries, env.Now()) ]
    | SnapshotsReady snapshots, _ -> { state with Snapshots = snapshots }, []
    | CatalogReady catalog, _ -> { state with Catalog = catalog }, [ AdminApp.CatalogLoaded(catalog.Offered, catalog.Listing) ]
    | _, None -> state, []

let rec private advance (env: Env) (state: State) (msg: AdminApp.Msg) (sent: Request list) =
    match state.Model with
    | None -> state, sent
    | Some model ->
        let model, effects = AdminUpdate.update msg model

        let state, made =
            effects
            |> List.fold (fun (state, made) effect -> let next, requests = perform env state effect in next, made @ requests) ({ state with Model = Some model }, [])

        settle env state (sent @ made)

/// The bridge's browser calls become requests; finished work becomes state
/// and engine messages. A wait the kernel cannot time is answered at once.
and private settle (env: Env) (state: State) (sent: Request list) =
    match env.Bridge.Drain() with
    | [], [], [] -> state, sent
    | calls, finished, failed ->
        let state, made, untimed =
            calls
            |> List.fold
                (fun (state: State, made, untimed) (id, call) ->
                    match kernelRequest state id call with
                    | Some request -> { state with Pending = state.Pending.Add(id, BridgeCall) }, made @ [ request ], untimed
                    | None -> state, made, untimed @ [ id ])
                (state, [], [])

        untimed |> List.iter (fun id -> env.Bridge.Answer id Bridge.Done |> ignore)
        let state, sent = if untimed.IsEmpty then state, sent @ made else settle env state (sent @ made)

        let state, sent =
            finished
            |> List.fold
                (fun (state, sent) outcome ->
                    let state, messages = absorb env state outcome
                    messages |> List.fold (fun (state, sent) msg -> advance env state msg sent) (state, sent))
                (state, sent)

        // Every failure is presented through Aegis by `handle`. Only the
        // running operation's own work settles it, as unknown (it may have
        // written before it failed), after every other message of this drain
        // was applied; a failed sign-out or cross-tab receive leaves the
        // operation running and the guard in place (SIG-VIS-PENDING-002).
        let state = { state with Faults = state.Faults @ (failed |> List.map _.Error) }

        match failed |> List.exists (fun failure -> failure.Work = Bridge.Work.Operation) with
        | false -> state, sent
        | true -> advance env state AdminApp.OperationFaulted sent

let private take (id: string) (state: State) =
    match state.Pending.TryFind id with
    | Some purpose -> purpose, { state with Pending = state.Pending.Remove id }
    | None -> raise (CapabilityFailed("correlation", $"A result for {id}, which this engine never requested"))

let private answerBridge (env: Env) (id: string) (answer: Bridge.KernelAnswer) =
    if not (env.Bridge.Answer id answer) then
        raise (CapabilityFailed("correlation", $"A result for {id}, which no operation is waiting on"))

/// Pure apart from `env`: (state, message) -> (state, requests, handshake answer).
let private run (env: Env) (state: State) (inbound: Inbound) : State * Request list * Handshake option =
    match inbound with
    | Initialize(protocolVersion, effects, origin, path, query, rawQuery, hash, offer) ->
        if protocolVersion <> 1 then
            raise (MalformedInput("$.protocolVersion", $"Unsupported protocol version {protocolVersion}"))
        elif not (List.contains "Navigation" effects && List.contains "Http" effects && List.contains "Storage" effects) then
            raise (CapabilityFailed("Http", "The administrator page needs the kernel's Navigation, Http and Storage effects"))

        match answer offer with
        | Rejected _ as handshake -> state, [], Some handshake
        | Accepted(_, _, capabilities) as handshake ->
            let started =
                { state with
                    Model = Some(AdminApp.initial env.Catalog)
                    Origin = origin
                    Path = path
                    // Fides removes a provider callback from the address (LCP-109).
                    Query = (if query |> List.exists (fun (k, _) -> k = "state") then "" else rawQuery)
                    Capabilities = capabilities
                    Effects = effects }

            let next, sent = advance env started (AdminApp.Started(page started hash, query)) []
            next, sent, Some handshake
    | other ->
        let state, msg =
            match other with
            | Event(name, key, value) -> state, Some(AdminApp.Ui(name, key, defaultArg value ""))
            | LocationChanged hash -> state, Some(AdminApp.LocationMoved(page state hash))
            | NavigationResult(id, outcome) ->
                let _, state = take id state

                match outcome with
                | Moved hash -> state, Some(AdminApp.LocationMoved(page state hash))
                | Dispatched
                | NavigationFailed _ -> state, None
            | ClipboardResult(id, succeeded) ->
                match take id state with
                | Copying, state -> state, Some(AdminApp.LinkCopied succeeded)
                | _, state -> state, None
            | CapabilityResult(id, _, outcome) ->
                match take id state, outcome with
                | (BridgeCall, state), Completed result ->
                    match tryField "kind" result |> Option.map (asString "$.result.kind") with
                    | Some "Fired" -> answerBridge env id Bridge.Done
                    | _ ->
                        answerBridge env id (Bridge.Read(tryField "value" result |> Option.map (asString "$.result.value")))

                    state, None
                | (BridgeCall, state), NotExecuted _ ->
                    answerBridge env id (Bridge.Read None)
                    state, None
                | (ReturnTarget, state), Completed result ->
                    // tabGet answers { kind: "Value", value? }; tabSet answers { kind: "Done" }.
                    match tryField "kind" result |> Option.map (asString "$.result.kind") with
                    | Some "Value" -> state, Some(AdminApp.ReturnRecalled(tryField "value" result |> Option.map (asString "$.result.value")))
                    | _ -> state, None
                | (ReturnTarget, state), NotExecuted _ -> state, Some(AdminApp.ReturnRecalled None)
                | (Downloading, state), Completed result when (tryField "kind" result |> Option.map (asString "$.result.kind")) = Some "Downloaded" ->
                    state, Some(AdminApp.Noted(notice "SIGNAL.REPORT.EXPORTED" "The export was handed to the browser to save." false))
                | (Downloading, state), _ -> state, Some(AdminApp.Failed(notice "SIGNAL.REPORT.NOT_SAVED" "The browser did not save the export; try again with the button." false))
                | (_, state), _ -> state, None
            | HttpResponse(id, result) ->
                match take id state, result with
                | (Configuration, state), Bridge.HttpSucceeded(200, _, body) -> state, Some(AdminApp.ConfigurationRead(Some body))
                | (Configuration, state), _ -> state, Some(AdminApp.ConfigurationRead None)
                | (BridgeCall, state), result ->
                    answerBridge env id (Bridge.Answered result)
                    state, None
                | _ -> raise (CapabilityFailed("Http", $"An Http result for {id}, which was not an Http request"))
            | StorageResponse(id, result) ->
                match take id state with
                | BridgeCall, state ->
                    answerBridge
                        env
                        id
                        (match result with
                         | StorageValue value -> Bridge.Read value
                         | StorageFailed reason -> Bridge.Refused reason)

                    state, None
                | _ -> raise (CapabilityFailed("Storage", $"A Storage result for {id}, which was not a Storage request"))
            | CapabilityFact(capability, fact) when capability = host.Id ->
                match tryField "kind" fact |> Option.map (asString "$.fact.kind"), state.Client, state.Model |> Option.bind _.Deployment |> Option.bind _.Identity with
                | Some "Broadcast", Some client, Some identity ->
                    let message = required "message" "$.fact" asString fact

                    match state.Opened |> Option.bind (fun opened -> Credential.ofAnnouncement opened.DatasetId message) with
                    // Another tab changed this dataset: reload it from the provider (ADM-072).
                    | Some notice -> state, Some(AdminApp.TabNoticed notice)
                    | None ->
                        env.Bridge.Start(
                            async {
                                do! client.Receive message
                                return Identity.noticeOf identity message |> Option.map (AdminApp.TabNoticed >> ToEngine) |> Option.toList
                            }
                        )

                        state, None
                | Some "Broadcast", _, _ -> state, None
                | _ -> raise (MalformedInput("$.fact.kind", "a known signal.host fact"))
            | CapabilityFact(capability, _) -> raise (CapabilityFailed(capability, $"Unexpected fact from {capability}: this page watches only signal.host"))
            | Initialize _ -> invalidOp "handled above"

        let next, sent =
            match msg with
            | Some msg -> advance env state msg []
            | None -> settle env state []

        next, sent, None

/// Pure apart from `env`: (state, message) -> (state, reply).
let step (env: Env) (state: State) (inbound: Inbound) =
    let next, sent, handshake = run env state inbound
    next, encode (view next) sent handshake

/// What handling one kernel message gave: the state to keep, the reply, and
/// a programming defect to re-raise once that state is kept.
[<NoComparison; NoEquality>]
type Handled =
    { State: State
      Reply: string
      Defect: ExceptionDispatchInfo option }

/// Handles one kernel message under the Aegis boundary. A fault in handling
/// the message leaves the state as it was and is shown until the next
/// message. A fault inside an operation keeps everything else the message
/// did: the operation was already settled as unknown, and the fault is
/// presented through Aegis here. A programming defect inside an operation is
/// still re-raised (Aegis fails loud), but as `Defect`, after the settled
/// state: the caller keeps that state first, so the page is never left
/// running an operation that already ended (WI-0114).
let handle (aegis: AegisConfig) (env: Env) (state: State) (messageJson: string) : Handled =
    let cleared = { state with Fault = None; Faults = [] }

    match capture aegis "Signal.Admin.dispatch" (fun () -> run env cleared (decode messageJson)) with
    | Ok(next, sent, handshake) ->
        let next, defect =
            match next.Faults with
            | [] -> next, None
            | error :: _ ->
                try
                    match capture aegis "Signal.Admin.operation" (fun () -> (raise error: unit)) with
                    | Result.Error fault -> { next with Fault = Some fault; Faults = [] }, None
                    | Ok() -> { next with Faults = [] }, None
                with defect ->
                    { next with Faults = [] }, Some(ExceptionDispatchInfo.Capture defect)

        { State = next; Reply = encode (view next) sent handshake; Defect = defect }
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        { State = faulted; Reply = encode (view faulted) [] None; Defect = None }
