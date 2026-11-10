/// The administrator wire's state (split from `AdminWire`, SIG-VIS-PENDING-002):
/// what the page holds between kernel messages, which requests are pending
/// and why, and how a bridge call becomes a Limen request.
module Echelon.Signal.Application.AdminWireState

open Fides.Client
open Echelon.Signal.Admin
open Echelon.Signal.Application.Json
open Echelon.Signal.Application.Boundary
open Echelon.Signal.Application.AdminProtocol

type Purpose =
    | Navigation
    | Copying
    | ReturnTarget
    | Downloading
    | Configuration
    | BridgeCall
    | Focusing

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
      /// Operations that ended in an exception during this message, presented
      /// through Aegis once the message is handled (`handle`).
      Faults: exn list
      Client: FidesClient option
      Opened: Store.Opened option
      /// The open dataset's template catalog (built-in templates when none is open).
      Catalog: TemplateCatalog.Loaded
      /// The open dataset's formal report snapshots (WI-0075).
      Snapshots: ReportLibrary.Snapshot list
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
      Faults = []
      Client = None
      Opened = None
      Catalog = TemplateCatalog.builtIn []
      Snapshots = []
      Groups = Map.empty }

let negotiated (offer: CapabilityOffer) (state: State) = List.contains offer state.Capabilities

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

let page (state: State) (hash: string) : Limen.Routing.PageLocation =
    { Origin = state.Origin; Path = state.Path; Query = state.Query; Hash = hash }

let kernelRequest (state: State) (id: string) (call: Bridge.KernelCall) =
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
