/// Administrators and capability-based authorization (ADM-052, AUT-006 role
/// separation, ADM-056).
///
/// Signing in says who someone is (Fides: GitHub's stable account number).
/// This module says what they may do in a dataset, and it is separate: every
/// check is "does this principal hold this capability in this dataset",
/// never a role name. Roles are only templates that produce capability sets
/// (`Grants`). Each dataset has its own roster; a person may administer
/// several datasets with different capabilities in each.
///
/// The first administrators of a dataset come only from the deployment's
/// configuration (`Deployment.isBootstrapAdministrator`), by GitHub numeric
/// id; no one becomes an administrator by opening a dataset.
///
/// Pure.
module Echelon.Signal.Admin.Access

/// What kind of principal acts. An agent is never recorded as a person.
type PrincipalKind =
    | Human
    | Agent
    | Service
    | Integration

type Principal =
    { /// `github:<numeric id>` for a person signed in through Fides.
      PrincipalId: string
      Kind: PrincipalKind
      /// For display only; never identity.
      DisplayName: string }

/// What a principal may do in a dataset.
type Capability =
    /// Read results, aggregates and reports.
    | ViewResults
    /// Import submissions into groups.
    | ImportSubmissions
    /// Create, configure, close groups.
    | ManageGroups
    /// Edit template drafts (DraftEditor).
    | EditDrafts
    /// Review a draft for publication (Reviewer).
    | ReviewTemplates
    /// Publish a reviewed draft (Publisher).
    | PublishTemplates
    /// Build report and dashboard definitions.
    | BuildReports
    /// Export data and reports.
    | ExportData
    /// Seal a dataset or finalize a group (ADM-065, ADM-066).
    | SealAndFinalize
    /// Change the dataset's roster.
    | ManageAdministrators
    /// Change storage settings, run migrations and repairs.
    | ManageStorage

let allCapabilities =
    [ ViewResults
      ImportSubmissions
      ManageGroups
      EditDrafts
      ReviewTemplates
      PublishTemplates
      BuildReports
      ExportData
      SealAndFinalize
      ManageAdministrators
      ManageStorage ]

/// The capability's stable name, as records and diagnostics state it.
let capabilityName (capability: Capability) =
    match capability with
    | ViewResults -> "ViewResults"
    | ImportSubmissions -> "ImportSubmissions"
    | ManageGroups -> "ManageGroups"
    | EditDrafts -> "EditDrafts"
    | ReviewTemplates -> "ReviewTemplates"
    | PublishTemplates -> "PublishTemplates"
    | BuildReports -> "BuildReports"
    | ExportData -> "ExportData"
    | SealAndFinalize -> "SealAndFinalize"
    | ManageAdministrators -> "ManageAdministrators"
    | ManageStorage -> "ManageStorage"

let capabilityOf (name: string) =
    allCapabilities |> List.tryFind (fun capability -> capabilityName capability = name)

let kindName =
    function
    | Human -> "Human"
    | Agent -> "Agent"
    | Service -> "Service"
    | Integration -> "Integration"

let kindOf (name: string) =
    [ Human; Agent; Service; Integration ] |> List.tryFind (fun kind -> kindName kind = name)

/// Capabilities that only change what is stored. Viewing and exporting what
/// is already loaded is not among them, so a read-only session keeps them
/// (ADM-070).
let mutating =
    set
        [ ImportSubmissions
          ManageGroups
          EditDrafts
          ReviewTemplates
          PublishTemplates
          BuildReports
          SealAndFinalize
          ManageAdministrators
          ManageStorage ]

/// Statements only a person can make: approving a template for publication,
/// publishing it, and sealing or finalizing. An agent, service or
/// integration may import and draft, but never vouch.
let personOnly = set [ ReviewTemplates; PublishTemplates; SealAndFinalize ]

/// Whether a principal of this kind may hold the capability at all.
let permitsKind (kind: PrincipalKind) (capability: Capability) =
    kind = Human || not (personOnly.Contains capability)

/// Templates that produce capability sets. They are conveniences for
/// granting, never checked by name.
module Grants =
    let analyst = set [ ViewResults; BuildReports; ExportData ]
    let importer = set [ ViewResults; ImportSubmissions ]
    let draftEditor = set [ ViewResults; EditDrafts ]
    let reviewer = set [ ViewResults; ReviewTemplates ]
    let publisher = set [ ViewResults; PublishTemplates ]
    let administrator = Set.ofList allCapabilities

    /// What the template allows a principal of this kind.
    let forKind (kind: PrincipalKind) (grant: Set<Capability>) = grant |> Set.filter (permitsKind kind)

/// One principal's membership of one dataset's roster.
type Membership =
    { Principal: Principal
      Capabilities: Set<Capability>
      /// Optimistic-concurrency revision of this membership, from 1.
      Revision: int }

/// A dataset's administrators, by principal id.
type Roster =
    { DatasetId: string
      Members: Map<string, Membership> }

/// Why an access request is refused.
type AccessRefusal =
    | WrongDataset of expected: string * found: string
    | NotAMember of principalId: string
    | NotHeld of capability: Capability
    | NotForKind of capability: Capability * kind: PrincipalKind
    | AlreadyAMember of principalId: string
    /// The roster would be left with no one who can manage it.
    | LastAdministrator
    /// Only a configured bootstrap administrator may set a dataset up.
    | NotABootstrapAdministrator of principalId: string

let refusalCode =
    function
    | WrongDataset _ -> "SIGNAL.ACCESS.WRONG_DATASET"
    | NotAMember _ -> "SIGNAL.ACCESS.NOT_A_MEMBER"
    | NotHeld _ -> "SIGNAL.ACCESS.CAPABILITY_NOT_HELD"
    | NotForKind _ -> "SIGNAL.ACCESS.NOT_FOR_KIND"
    | AlreadyAMember _ -> "SIGNAL.ACCESS.ALREADY_A_MEMBER"
    | LastAdministrator -> "SIGNAL.ACCESS.LAST_ADMINISTRATOR"
    | NotABootstrapAdministrator _ -> "SIGNAL.ACCESS.NOT_A_BOOTSTRAP_ADMINISTRATOR"

/// Why, as one sentence (ADM-052 capability explanation).
let explainRefusal =
    function
    | WrongDataset(expected, found) -> $"This roster is dataset '{found}', not '{expected}'."
    | NotAMember principalId -> $"'{principalId}' is not an administrator of this dataset."
    | NotHeld capability -> $"This needs {capabilityName capability}, which you do not hold here."
    | NotForKind(capability, kind) -> $"{capabilityName capability} is only for people, not a {kindName kind}."
    | AlreadyAMember principalId -> $"'{principalId}' is already an administrator."
    | LastAdministrator -> "The dataset would have no one left who can manage its administrators."
    | NotABootstrapAdministrator principalId -> $"'{principalId}' is not one of the dataset's configured administrators."

/// The roster a configured administrator starts a dataset with: they hold
/// every capability their kind may hold, so the dataset is never without
/// someone who can manage it.
let founded (datasetId: string) (founder: Principal) =
    { DatasetId = datasetId
      Members =
        Map.ofList
            [ founder.PrincipalId,
              { Principal = founder
                Capabilities = Grants.forKind founder.Kind Grants.administrator
                Revision = 1 } ] }

/// What the principal holds in the roster's dataset; empty for a non-member.
let capabilitiesOf (roster: Roster) (principalId: string) =
    roster.Members.TryFind principalId |> Option.map _.Capabilities |> Option.defaultValue Set.empty

/// Ok when the principal holds the capability in the dataset.
let authorize (roster: Roster) (datasetId: string) (principalId: string) (capability: Capability) : Result<unit, AccessRefusal> =
    if roster.DatasetId <> datasetId then
        Error(WrongDataset(datasetId, roster.DatasetId))
    else
        match roster.Members.TryFind principalId with
        | None -> Error(NotAMember principalId)
        | Some membership when membership.Capabilities.Contains capability -> Ok()
        | Some _ -> Error(NotHeld capability)

let permits (roster: Roster) (principalId: string) (capability: Capability) =
    authorize roster roster.DatasetId principalId capability |> Result.isOk

/// Changes to a roster. Each needs ManageAdministrators.
type RosterCommand =
    | Admit of Principal * Set<Capability>
    | Grant of principalId: string * Capability
    | Revoke of principalId: string * Capability
    | Remove of principalId: string

let private managers (roster: Roster) =
    roster.Members |> Map.filter (fun _ m -> m.Capabilities.Contains ManageAdministrators) |> Map.count

let private kindProblems (principal: Principal) (capabilities: Set<Capability>) =
    capabilities |> Set.toList |> List.filter (permitsKind principal.Kind >> not) |> List.map (fun c -> NotForKind(c, principal.Kind))

let private memberOf (roster: Roster) (principalId: string) =
    roster.Members.TryFind principalId |> Option.map Ok |> Option.defaultValue (Error [ NotAMember principalId ])

let private replace (roster: Roster) (membership: Membership) =
    { roster with Members = roster.Members.Add(membership.Principal.PrincipalId, membership) }

/// Applies a roster change made by `performer`: the next roster, or every
/// reason it is refused. The dataset always keeps someone who can manage its
/// administrators, and person-only capabilities go only to people.
let execute (performer: string) (command: RosterCommand) (roster: Roster) : Result<Roster, AccessRefusal list> =
    let keepsAManager (next: Roster) =
        if managers next = 0 then Error [ LastAdministrator ] else Ok next

    authorize roster roster.DatasetId performer ManageAdministrators
    |> Result.mapError List.singleton
    |> Result.bind (fun () ->
        match command with
        | Admit(principal, capabilities) ->
            if roster.Members.ContainsKey principal.PrincipalId then
                Error [ AlreadyAMember principal.PrincipalId ]
            else
                match kindProblems principal capabilities with
                | [] ->
                    Ok(
                        replace
                            roster
                            { Principal = principal
                              Capabilities = capabilities
                              Revision = 1 }
                    )
                | problems -> Error problems
        | Grant(principalId, capability) ->
            memberOf roster principalId
            |> Result.bind (fun found ->
                match kindProblems found.Principal (set [ capability ]) with
                | [] ->
                    Ok(
                        replace
                            roster
                            { found with
                                Capabilities = found.Capabilities.Add capability
                                Revision = found.Revision + 1 }
                    )
                | problems -> Error problems)
        | Revoke(principalId, capability) ->
            memberOf roster principalId
            |> Result.map (fun found ->
                replace
                    roster
                    { found with
                        Capabilities = found.Capabilities.Remove capability
                        Revision = found.Revision + 1 })
            |> Result.bind keepsAManager
        | Remove principalId ->
            memberOf roster principalId
            |> Result.map (fun _ -> { roster with Members = roster.Members.Remove principalId })
            |> Result.bind keepsAManager)

/// Why a principal may or may not do something here, for the person
/// (ADM-052): the capability, whether it is held, and which grant template
/// would give it.
let explain (roster: Roster) (principalId: string) (capability: Capability) =
    match authorize roster roster.DatasetId principalId capability with
    | Ok() -> $"You hold {capabilityName capability} in this dataset."
    | Error refusal -> explainRefusal refusal
