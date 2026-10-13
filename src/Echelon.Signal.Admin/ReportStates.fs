/// Report-state persistence and report snapshot generation as ARX-002 state
/// systems (WI-0092 slice 3).
///
/// - **Persistence** (ARP-004 §§47-50): a group's report state travels in
///   the administrator URL when it fits, and is otherwise written once to
///   the store under its content-addressed id. The store is read before any
///   write; a write whose outcome is unknown is reconciled by reading again
///   (the id names the content, so finding it means it landed), never
///   assumed to have failed. `GroupStore.persistReportState` performs this
///   flow; the states here make each step and refusal explicit.
/// - **Snapshots** (ADM-022): a snapshot is taken from a saved definition
///   and the group's exact template, and opening one never reads current
///   state; a snapshot whose definition, template or data is missing or
///   altered stays explicitly unresolved.
///
/// Pure.
module Echelon.Signal.Admin.ReportStates

open Echelon.Signal.Engine.StateModel
open Echelon.Signal.Admin.ReportState
open Echelon.Signal.Admin.ReportLibrary

// ---- Persistence --------------------------------------------------------------------------------

/// Where a report state stands on its way to the URL or the store.
[<NoComparison>]
type Persistence =
    | Placing of AdminReportState
    | Embedded of fragment: string
    | CheckingStore of resultId: string * fragment: string
    | ReadyToWrite of resultId: string * fragment: string
    | Writing of resultId: string * fragment: string
    | WriteOutcomeUnknown of resultId: string * fragment: string
    | WriteFailed of resultId: string * fragment: string * code: string
    | Stored of resultId: string * fragment: string
    | ErasedInStore of resultId: string

/// What the store reported for a read.
type Seen =
    | FoundInStore
    | AbsentFromStore
    | ErasedFromStore

/// What the store reported for a commit.
type Committed =
    | Landed
    | Failed of code: string
    | OutcomeUnknown

type PersistenceCommand =
    | Place of UrlBudget
    | ObserveRead of Seen
    | Commit
    | ObserveCommit of Committed
    | Reconcile

let persistenceStateName =
    function
    | Placing _ -> "Placing"
    | Embedded _ -> "Embedded"
    | CheckingStore _ -> "CheckingStore"
    | ReadyToWrite _ -> "ReadyToWrite"
    | Writing _ -> "Writing"
    | WriteOutcomeUnknown _ -> "WriteOutcomeUnknown"
    | WriteFailed _ -> "WriteFailed"
    | Stored _ -> "Stored"
    | ErasedInStore _ -> "ErasedInStore"

let persistenceCommandName =
    function
    | Place _ -> "place"
    | ObserveRead _ -> "read"
    | Commit -> "commit"
    | ObserveCommit _ -> "committed"
    | Reconcile -> "reconcile"

let persistenceCapabilities =
    function
    | ReadyToWrite _ -> set [ Capability.CanPersistReportState ]
    | WriteFailed _ -> set [ Capability.CanPersistReportState; Capability.CanRetryEffect ]
    | WriteOutcomeUnknown _ -> set [ Capability.CanReconcileEffect ]
    | Embedded _
    | Stored _ -> set [ Capability.CanRenderFinalReport ]
    | _ -> Set.empty

let persistenceRequires =
    function
    | Commit -> Some Capability.CanPersistReportState
    | Reconcile -> Some Capability.CanReconcileEffect
    | _ -> None

let persistenceObligations =
    function
    | ReadyToWrite(id, _) ->
        [ { Code = "PersistReportState"; Detail = $"{id} is too large for the URL and not stored."; DischargedBy = "a write that lands" } ]
    | WriteFailed(id, _, code) ->
        [ { Code = "PersistReportState"; Detail = $"Writing {id} failed ({code})."; DischargedBy = "a retried write that lands" } ]
    | WriteOutcomeUnknown(id, _) ->
        [ { Code = "ReconcileUnknownPersistenceOutcome"
            Detail = $"Whether {id} was written is unknown."
            DischargedBy = "reading the store: the content-addressed record found means it landed" } ]
    | _ -> []

/// What is known about the stored state.
let stored (state: Persistence) : Knowledge<string> =
    match state with
    | Stored(id, _) -> Known id
    | Embedded _ -> Known "embedded in the URL"
    | WriteOutcomeUnknown _ -> Unknown "the write's outcome is unknown, not failed"
    | ReadyToWrite(id, _)
    | WriteFailed(id, _, _) -> SearchedNotFound(id, "the dataset's namespace, as last read")
    | ErasedInStore id -> SearchedNotFound(id, "the dataset's namespace: the record was erased")
    | Placing _
    | CheckingStore _
    | Writing _ -> Unknown "the store has not been read yet"

let persistenceAttempt (command: PersistenceCommand) (state: Persistence) : Result<Persistence, Illegal> =
    let refuse reason = illegal "reportstate" (persistenceStateName state) (persistenceCommandName command) reason

    match command, state with
    | Place budget, Placing s ->
        match persistence budget s with
        | EmbeddedInUrl fragment -> Ok(Embedded fragment)
        | ExternalStore(id, fragment) -> Ok(CheckingStore(id, fragment))
    | ObserveRead FoundInStore, CheckingStore(id, fragment) -> Ok(Stored(id, fragment))
    | ObserveRead AbsentFromStore, CheckingStore(id, fragment) -> Ok(ReadyToWrite(id, fragment))
    | ObserveRead ErasedFromStore, CheckingStore(id, _) -> Ok(ErasedInStore id)
    | Commit, (ReadyToWrite(id, fragment) | WriteFailed(id, fragment, _)) -> Ok(Writing(id, fragment))
    | ObserveCommit Landed, Writing(id, fragment) -> Ok(Stored(id, fragment))
    | ObserveCommit(Failed code), Writing(id, fragment) -> Ok(WriteFailed(id, fragment, code))
    | ObserveCommit OutcomeUnknown, Writing(id, fragment) -> Ok(WriteOutcomeUnknown(id, fragment))
    // Reconciling reads again; the read decides.
    | Reconcile, WriteOutcomeUnknown(id, fragment) -> Ok(CheckingStore(id, fragment))
    | Commit, WriteOutcomeUnknown _ -> refuse "the last write's outcome is unknown: reconcile before sending again"
    | _, (Embedded _ | Stored _) -> refuse "the report state is already where it belongs"
    | _, ErasedInStore _ -> refuse "the stored record was erased; it is not written again"
    | _ -> refuse $"{persistenceCommandName command} is not a step from {persistenceStateName state}"

let persistenceSystem: System<Persistence, PersistenceCommand> =
    { Name = "reportstate"
      StateName = persistenceStateName
      CommandName = persistenceCommandName
      Attempt = persistenceAttempt
      Capabilities = persistenceCapabilities
      Requires = persistenceRequires
      Obligations = persistenceObligations
      Terminal =
        function
        | Embedded _
        | Stored _
        | ErasedInStore _ -> true
        | _ -> false }

// ---- Snapshots ----------------------------------------------------------------------------------

/// The library, the catalog and the snapshots taken.
[<NoComparison>]
type Snapshots =
    { Library: Library
      Catalog: Echelon.Signal.Engine.Publication.Catalog
      Taken: Snapshot list }

type SnapshotCommand =
    | Take of Source
    | Open of snapshotId: string

let snapshotCommandName =
    function
    | Take _ -> "take"
    | Open _ -> "open"

let snapshotStateName (s: Snapshots) = if s.Taken.IsEmpty then "NoSnapshots" else "HasSnapshots"

let private problemText =
    function
    | DefinitionMissing(id, v) -> $"definition {id} version {v} is not in the library"
    | ReportFromAnotherDefinition(id, v) -> $"the report was not built from {id} version {v}"
    | TemplateUnresolved survey -> $"no published template of {survey} resolves the pin"
    | TemplateMismatch(pinned, group) -> $"the pin names {pinned.SurveyId} {pinned.Version}; the group was scored with {group.SurveyId} {group.Version}"
    | SchemaUnsupported(name, pinned, supported) -> $"{name} schema {pinned} is not the supported {supported}"
    | WouldRevealDifference(earlier, difference) -> $"it would differ from {earlier} by {difference}, revealing individuals"

let private missingText =
    function
    | MissingDefinition(id, v) -> $"definition {id} version {v}"
    | MissingTemplate t -> $"template {t.SurveyId} {t.Version}"
    | DataAltered -> "its data was altered"

let snapshotCapabilities (s: Snapshots) =
    set
        [ if s.Taken |> List.exists (fun x -> match openSnapshot s.Library s.Catalog x with Resolved _ -> true | Unresolved _ -> false) then
              Capability.CanRenderFinalReport ]

let snapshotObligations (s: Snapshots) =
    s.Taken
    |> List.choose (fun x ->
        match openSnapshot s.Library s.Catalog x with
        | Resolved _ -> None
        | Unresolved missing when List.contains DataAltered missing ->
            Some
                { Code = "RepairCorruptRecords"
                  Detail = $"{x.SnapshotId}: its data no longer matches its hash."
                  DischargedBy = "the snapshot's original record restored" }
        | Unresolved missing ->
            Some
                { Code = "ResolveSnapshotDependency"
                  Detail = $"""{x.SnapshotId} names {missing |> List.map missingText |> String.concat ", "}."""
                  DischargedBy = "the pinned definition and template restored to the library and catalog" })

/// What is known about the template a snapshot pins.
let snapshotTemplate (s: Snapshots) (snapshot: Snapshot) : Knowledge<TemplateRef> =
    match Echelon.Signal.Engine.Publication.resolveHash s.Catalog snapshot.Template.Hash with
    | Some t when t.SurveyId = snapshot.Template.SurveyId && t.Version = snapshot.Template.Version -> Known snapshot.Template
    | _ ->
        SearchedNotFound(
            $"{snapshot.Template.SurveyId} {snapshot.Template.Version} ({snapshot.Template.Hash})",
            $"the template catalog of {s.Catalog.Templates.Length} published version(s)"
        )

let snapshotAttempt (command: SnapshotCommand) (s: Snapshots) : Result<Snapshots, Illegal> =
    let refuse reason = illegal "snapshot" (snapshotStateName s) (snapshotCommandName command) reason

    match command with
    | Take source ->
        match take s.Library s.Catalog s.Taken source with
        | Ok(snapshot, library) -> Ok { s with Library = library; Taken = s.Taken @ [ snapshot ] }
        | Error problem -> refuse (problemText problem)
    | Open id ->
        match s.Taken |> List.tryFind (fun x -> x.SnapshotId = id) with
        | None -> refuse $"there is no snapshot {id}"
        | Some x ->
            match openSnapshot s.Library s.Catalog x with
            // Opening never changes anything, and never reads current state.
            | Resolved _ -> Ok s
            | Unresolved missing -> refuse $"""it cannot be opened: missing {missing |> List.map missingText |> String.concat ", "}"""

let snapshotSystem: System<Snapshots, SnapshotCommand> =
    { Name = "snapshot"
      StateName = snapshotStateName
      CommandName = snapshotCommandName
      Attempt = snapshotAttempt
      Capabilities = snapshotCapabilities
      Requires = fun _ -> None
      Obligations = snapshotObligations
      Terminal = fun _ -> false }
