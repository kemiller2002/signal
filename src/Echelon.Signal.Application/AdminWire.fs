/// The administrator page's side of the Limen boundary: kernel messages to
/// engine messages, engine effects to work (Fides sign-in, Arca storage)
/// and Limen requests, every step under the Aegis boundary (ADM-035,
/// ADM-077). Deterministic for a given `Env`: the clock, keys, randomness,
/// the storage backend and the identity client arrive as values; the only
/// effectful code is the composition root (`Runtime`).
module Echelon.Signal.Application.AdminWire

open System
open System.Text.Json.Nodes
open Aegis
open Fides.Client
open Echelon.Signal.Admin
open Echelon.Signal.Application.Json
open Echelon.Signal.Application.Boundary
open Echelon.Signal.Application.AdminProtocol
open Echelon.Signal.Application.AdminWork

type Purpose =
    | Navigation
    | Copying
    | ReturnTarget
    | Configuration
    | BridgeCall

[<NoComparison; NoEquality>]
type State =
    { Model: AdminApp.Model option
      Origin: string
      Path: string
      Query: string
      Capabilities: CapabilityOffer list
      Effects: string list
      Pending: Map<string, Purpose>
      Sequence: int
      Fault: FaultView option
      Client: FidesClient option
      Opened: Store.Opened option
      /// The open dataset's template catalog (built-in templates when none is open).
      Catalog: TemplateCatalog.Loaded
      Groups: Map<string, GroupStore.OpenedGroup> }

let initial =
    { Model = None
      Origin = ""
      Path = "/"
      Query = ""
      Capabilities = []
      Effects = []
      Pending = Map.empty
      Sequence = 0
      Fault = None
      Client = None
      Opened = None
      Catalog = TemplateCatalog.builtIn []
      Groups = Map.empty }

let private negotiated (offer: CapabilityOffer) (state: State) = List.contains offer state.Capabilities

/// The deployment's configuration document, beside the page.
let configurationUrl (state: State) =
    let folder = state.Path.Substring(0, state.Path.LastIndexOf '/' + 1)
    state.Origin + folder + "signal.deployment.json"

[<Literal>]
let RequestTimeoutMs = 15000

/// The tab-storage key that carries a sign-in's return target across the
/// identity provider's round trip (SIG-LINK-006).
[<Literal>]
let ReturnTargetKey = "signal.admin.returnTo"

let private page (state: State) (hash: string) : Limen.Routing.PageLocation =
    { Origin = state.Origin; Path = state.Path; Query = state.Query; Hash = hash }

let private kernelRequest (state: State) (id: string) (call: Bridge.KernelCall) =
    let hostCall operation arguments =
        if negotiated host state then
            Some(Host(id, operation, arguments))
        else
            raise (CapabilityFailed("signal.host", "Sign-in needs the signal.host pack, which the kernel did not offer"))

    match call with
    | Bridge.Http(method, url, headers, body, timeoutMs, responseHeaders) -> Some(Http(id, method, url, headers, body, timeoutMs, responseHeaders))
    | Bridge.DeviceGet key -> Some(StorageGet(id, key))
    | Bridge.DeviceSet(key, value) -> Some(StorageSet(id, key, value))
    | Bridge.DeviceRemove key -> Some(StorageRemove(id, key))
    | Bridge.TabGet key -> hostCall "tabGet" [ "key", key ]
    | Bridge.TabSet(key, value) -> hostCall "tabSet" [ "key", key; "value", value ]
    | Bridge.TabRemove key -> hostCall "tabRemove" [ "key", key ]
    | Bridge.Leave url -> hostCall "leave" [ "url", url ]
    | Bridge.ReplaceAddress url -> hostCall "replaceAddress" [ "url", url ]
    | Bridge.Announce message -> hostCall "broadcast" [ "message", message ]
    | Bridge.Sleep milliseconds when negotiated schedule state -> Some(Wake(id, milliseconds))
    | Bridge.Sleep _ -> None

/// Starts the work an engine effect asks for; its outcome arrives later as
/// `Outcome`s through the bridge. Effects the kernel performs directly
/// become Limen requests.
let private perform (env: Env) (state: State) (effect: AdminApp.Effect) : State * Request list =
    let start (work: Async<Outcome list>) = env.Bridge.Start work
    let model = state.Model |> Option.defaultValue (AdminApp.initial env.Catalog)
    let now = env.Now()
    let mint purpose (state: State) =
        let sequence = state.Sequence + 1
        let id = $"admin-{sequence}"
        id, { state with Sequence = sequence; Pending = state.Pending.Add(id, purpose) }

    let withDataset (work: Store.Actor -> Store.Opened -> Async<Outcome list>) =
        match actorOf env model, state.Opened with
        | Some actor, Some opened -> start (work actor opened)
        | _ -> start (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NO_DATASET" "Open a dataset first." false)) ])

    let withGroup (key: string) (work: Store.Actor -> GroupStore.OpenedGroup -> Async<Outcome list>) =
        match actorOf env model, state.Groups.TryFind key with
        | Some actor, Some group -> start (work actor group)
        | _ -> start (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NO_GROUP" "That group is not open." false)) ])

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

        { state with Opened = None; Catalog = TemplateCatalog.builtIn env.Catalog; Groups = Map.empty }, []
    | AdminApp.OpenDataset datasetId ->
        match model.Deployment, actorOf env model, state.Client with
        | Some config, Some actor, Some client ->
            let backend = env.Backend (fun () -> Some(Identity.tokens client)) (fun () -> client.ReportUnauthorized())
            let pinned = state.Opened |> Option.filter (fun o -> o.DatasetId = datasetId) |> Option.map _.RepositoryId
            start (AdminGroupWork.openDataset env backend config actor pinned datasetId now)
        | _ -> start (async.Return [ ToEngine(AdminApp.Failed(notice "SIGNAL.ADMIN.NOT_SIGNED_IN" "Sign in first." false)) ])

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
    | AdminApp.TransitionGroup(key, name) ->
        withGroup key (fun actor group -> AdminGroupWork.transition env actor group name now)
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
    | CatalogReady catalog, _ -> { state with Catalog = catalog }, [ AdminApp.CatalogLoaded(catalog.Offered, catalog.Listing) ]
    | _, None -> state, []

let rec private advance (env: Env) (state: State) (msg: AdminApp.Msg) (sent: Request list) =
    match state.Model with
    | None -> state, sent
    | Some model ->
        let model, effects = AdminApp.update msg model

        let state, made =
            effects
            |> List.fold (fun (state, made) effect -> let next, requests = perform env state effect in next, made @ requests) ({ state with Model = Some model }, [])

        settle env state (sent @ made)

/// The bridge's browser calls become requests; finished work becomes state
/// and engine messages. A wait the kernel cannot time is answered at once.
and private settle (env: Env) (state: State) (sent: Request list) =
    match env.Bridge.Drain() with
    | Result.Error error -> raise error
    | Ok([], []) -> state, sent
    | Ok(calls, finished) ->
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

        finished
        |> List.fold
            (fun (state, sent) outcome ->
                let state, messages = absorb env state outcome
                messages |> List.fold (fun (state, sent) msg -> advance env state msg sent) (state, sent))
            (state, sent)

let private take (id: string) (state: State) =
    match state.Pending.TryFind id with
    | Some purpose -> purpose, { state with Pending = state.Pending.Remove id }
    | None -> raise (CapabilityFailed("correlation", $"A result for {id}, which this engine never requested"))

let private answerBridge (env: Env) (id: string) (answer: Bridge.KernelAnswer) =
    if not (env.Bridge.Answer id answer) then
        raise (CapabilityFailed("correlation", $"A result for {id}, which no operation is waiting on"))

/// Pure apart from `env`: (state, message) -> (state, reply).
let step (env: Env) (state: State) (inbound: Inbound) =
    match inbound with
    | Initialize(protocolVersion, effects, origin, path, query, rawQuery, hash, offer) ->
        if protocolVersion <> 1 then
            raise (MalformedInput("$.protocolVersion", $"Unsupported protocol version {protocolVersion}"))
        elif not (List.contains "Navigation" effects && List.contains "Http" effects && List.contains "Storage" effects) then
            raise (CapabilityFailed("Http", "The administrator page needs the kernel's Navigation, Http and Storage effects"))

        match answer offer with
        | Rejected _ as handshake -> state, encode (view state) [] (Some handshake)
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
            next, encode (view next) sent (Some handshake)
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

        next, encode (view next) sent None

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// state as it was and is shown until the next message.
let handle (aegis: AegisConfig) (env: Env) (state: State) (messageJson: string) =
    let cleared = { state with Fault = None }

    match capture aegis "Signal.Admin.dispatch" (fun () -> step env cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { state with Fault = Some fault }
        faulted, encode (view faulted) [] None
