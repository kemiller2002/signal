/// What the administrator page's work brings back, the environment it runs
/// in, and the translations from store results to page messages (the work
/// itself is started by `AdminWire`).
module Echelon.Signal.Application.AdminWork

open System
open Fides.Client
open Echelon.Signal.Admin

/// What finished work brings back: engine messages, or stored state the
/// wire keeps (the engine sees only summaries).
[<NoComparison; NoEquality>]
type Outcome =
    | ToEngine of AdminApp.Msg
    | DatasetReady of Store.Opened * GroupStore.OpenedGroup list * TemplateCatalog.Loaded
    | GroupReady of GroupStore.OpenedGroup * GroupStore.Imported option * unreconciled: int
    | RosterChanged of Store.Opened

[<NoComparison; NoEquality>]
type Env =
    { Now: unit -> DateTimeOffset
      /// A fresh unique key with a prefix (idempotency keys, group ids).
      NewKey: string -> string
      RandomBytes: int -> byte array
      Bridge: Bridge.Bridge<Outcome>
      /// The storage backend for a token provider and an unauthorized report.
      Backend: (unit -> Arca.TokenProvider option) -> (unit -> Async<unit>) -> Store.Backend
      /// The identity client for a deployment's identity settings over ports.
      Identity: Deployment.IdentityConfig -> ClientPorts -> FidesClient
      /// The built-in templates; a dataset's stored catalog joins them when it opens.
      Catalog: AdminApp.CatalogEntry list
      ApplicationVersion: string }

let notice code message infrastructure : AdminApp.Notice =
    { Code = code
      Message = message
      Infrastructure = infrastructure }

let openFailure (failure: Store.OpenFailure) =
    match failure with
    | Store.Unreachable meaning -> AdminApp.WentOffline meaning.Message
    | Store.Misconfigured problem -> AdminApp.Failed(notice (Problems.code problem) (Problems.describe problem) false)
    | Store.NeedsBootstrapAdministrator dataset ->
        AdminApp.Failed(notice "SIGNAL.ACCESS.NOT_A_BOOTSTRAP_ADMINISTRATOR" $"Dataset '{dataset}' has not been set up, and only one of its configured administrators can set it up." false)
    | Store.CredentialNotUsable state -> AdminApp.Failed(notice ("SIGNAL.CREDENTIAL." + Credential.stateCode state) $"Your sign-in cannot open this dataset ({Credential.stateCode state})." false)
    | Store.Refused problems ->
        let first = problems |> List.tryHead
        AdminApp.Failed(notice (first |> Option.map Problems.code |> Option.defaultValue "SIGNAL.STORAGE.REFUSED") (first |> Option.map Problems.describe |> Option.defaultValue "The dataset cannot be opened.") false)
    | Store.StorageFailed meaning -> AdminApp.Failed(notice meaning.Code meaning.Message true)

let groupFailure (failure: GroupStore.GroupFailure) =
    match failure with
    | GroupStore.Offline meaning -> AdminApp.WentOffline meaning.Message
    | GroupStore.Storage meaning -> AdminApp.Failed(notice meaning.Code meaning.Message true)
    | GroupStore.NotPermitted refusal -> AdminApp.Failed(notice (Access.refusalCode refusal) (Access.explainRefusal refusal) false)
    | GroupStore.ReadOnly _ -> AdminApp.Failed(notice "SIGNAL.STORAGE.READ_ONLY" "The dataset is read-only now." false)
    | GroupStore.Unusable problems ->
        let first = problems |> List.tryHead
        AdminApp.Failed(notice (first |> Option.map Problems.code |> Option.defaultValue "SIGNAL.STORAGE.UNUSABLE") (first |> Option.map Problems.describe |> Option.defaultValue "The group cannot be used.") false)
    | GroupStore.TemplateUnavailable reason -> AdminApp.Failed(notice "SIGNAL.ADMIN.TEMPLATE_UNAVAILABLE" $"The group's template is not available: {reason}." false)
    | GroupStore.LifecycleRefused(GroupLifecycle.BlockedByObligations obligations) ->
        AdminApp.Failed(
            notice
                "SIGNAL.GROUP.BLOCKED_BY_OBLIGATIONS"
                ("Finalization is blocked: " + (obligations |> AdminState.ofGroup |> List.map AdminState.describeObligation |> String.concat " "))
                false
        )
    | GroupStore.LifecycleRefused refusal -> AdminApp.Failed(notice (GroupLifecycle.refusalCode refusal) "That is not possible for this group now." false)

let actorOf (env: Env) (model: AdminApp.Model) : Store.Actor option =
    model.Principal
    |> Option.map (fun principal ->
        { Principal = principal
          SignIn = model.SignIn
          NewContext =
            fun () ->
                let ok = function Ok value -> value | Error _ -> invalidOp "internal: invalid operation identifier"

                { Actor =
                    { Kind = Arca.ActorKind.Human
                      Id = Arca.ActorId.create principal.PrincipalId |> ok }
                  ProviderIdentity = None
                  CorrelationId = Arca.CorrelationId.create (env.NewKey "corr") |> ok
                  IdempotencyKey = Arca.IdempotencyKey.create (env.NewKey "op") |> ok
                  At = env.Now() } })

let loadGroups (env: Env) (resolve: GroupRecord.TemplateResolver) (opened: Store.Opened) =
    async {
        match! GroupAdmin.listGroups (env.Now()) opened with
        | Error failure -> return Error failure
        | Ok ids ->
            // One after another on this thread. Async.Sequential is Async.Parallel
            // with one worker: it hops to the thread pool, so the result would
            // land after the kernel step that started it and wait for the next
            // message (the page sat "busy" after sign-in).
            let rec openEach (opened': Store.Opened) acc ids =
                async {
                    match ids with
                    | [] -> return List.rev acc
                    | id :: rest ->
                        let! group = GroupStore.openGroup resolve id (env.Now()) opened'
                        return! openEach opened' (group :: acc) rest
                }

            let! groups = openEach opened [] ids
            return Ok(groups |> List.choose Result.toOption)
    }

