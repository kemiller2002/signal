/// A dataset's roster as stored, and the rule for changing it: one roster
/// command is one Arca operation, one commit.
///
/// - **Loading** goes through `Loading`: every membership record must prove
///   itself; an unusable one is held aside and never overwritten.
/// - **Changing**: each change names the revision last read, so a
///   membership changed since is a conflict, never a blind overwrite; members
///   the command did not touch are not written, so independent changes
///   survive. After a conflict the caller reloads and runs the command again:
///   Signal's rules decide again, because a clean merge is not proof (two
///   administrators removing each other must not leave the dataset with no
///   one to manage it).
/// - A change needs a `Loading.WriteGrant`: a verified, cleanly loaded
///   dataset whose credential may write directly.
///
/// Pure: the caller reads and commits through an Arca provider.
module Echelon.Signal.Admin.RosterStore

open Arca
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.AdministratorRecord

/// The roster as read, with what each membership was read at.
[<NoComparison>]
type StoredRoster =
    { Roster: Roster
      /// The membership records by principal id.
      Stored: Map<string, Loading.Stored<StoredMembership>>
      /// Paths that cannot be used, with the revision seen.
      Unusable: Map<string, Revision>
      Problems: Problem list }

/// The roster the loaded records make.
let ofLoaded (datasetId: string) (loaded: Loading.Loaded<StoredMembership>) : StoredRoster =
    let byPrincipal =
        loaded.Records |> Map.toList |> List.map (fun (_, found) -> found.Value.Membership.Principal.PrincipalId, found) |> Map.ofList

    { Roster =
        { DatasetId = datasetId
          Members = byPrincipal |> Map.map (fun _ found -> found.Value.Membership) }
      Stored = byPrincipal
      Unusable = loaded.Unusable
      Problems = loaded.Problems }

/// The records a dataset's first administrator is stored as, created in the
/// same commit that sets the dataset up.
let bootstrapRecords (datasetId: string) (founder: Principal) : Result<Change list, Problem list> =
    let roster = founded datasetId founder

    roster.Members
    |> Map.toList
    |> List.map (fun (principalId, membership) ->
        match path principalId, encode datasetId membership with
        | Ok target, Ok content -> Ok(Change.Create(target, content))
        | Error problem, _
        | _, Error problem -> Error problem)
    |> List.fold
        (fun state next ->
            match state, next with
            | Ok changes, Ok change -> Ok(changes @ [ change ])
            | Error problems, Error problem -> Error(problems @ [ problem ])
            | Error problems, Ok _ -> Error problems
            | Ok _, Error problem -> Error [ problem ])
        (Ok [])

/// The Arca changes that take the stored roster to `after`: a create for a
/// new member, an update at the revision last read for a changed one, a
/// delete at that revision for a removed one. A member whose record could
/// not be used must be repaired first.
let changes (stored: StoredRoster) (after: Roster) : Result<Change list, Problem list> =
    let before = stored.Roster.Members

    let changed =
        after.Members
        |> Map.toList
        |> List.filter (fun (principalId, membership) -> before.TryFind principalId <> Some membership)
        |> List.map fst

    let removed = before |> Map.toList |> List.map fst |> List.filter (after.Members.ContainsKey >> not)

    let one (principalId: string) =
        path principalId
        |> Result.mapError List.singleton
        |> Result.bind (fun target ->
            if stored.Unusable.ContainsKey(RelativePath.render target) then
                Error [ UnstorableRecord(principalId, "its stored record must be repaired first") ]
            else
                match after.Members.TryFind principalId, stored.Stored.TryFind principalId with
                | Some membership, existing ->
                    encode after.DatasetId membership
                    |> Result.mapError List.singleton
                    |> Result.map (fun content ->
                        match existing with
                        | Some found -> Change.Update(target, content, found.Revision)
                        | None -> Change.Create(target, content))
                | None, Some found -> Ok(Change.Delete(target, found.Revision))
                | None, None -> Error [ UnstorableRecord(principalId, "nothing to change") ])

    let results = (changed @ removed) |> List.sort |> List.map one

    match results |> List.collect (function Error problems -> problems | Ok _ -> []) with
    | [] -> Ok(results |> List.choose (function Ok change -> Some change | Error _ -> None))
    | problems -> Error problems

/// Why a roster command was not turned into an operation.
type CommandFailure =
    /// Signal's rules refuse it.
    | Refused of AccessRefusal list
    /// It cannot be stored.
    | Unstorable of Problem list

/// One roster command, decided by Signal's rules against the stored roster,
/// as the next roster and the one operation that stores it.
let command
    (grant: Loading.WriteGrant)
    (ns: Namespace)
    (context: Storage.OperationContext)
    (performer: string)
    (rosterCommand: RosterCommand)
    (stored: StoredRoster)
    : Result<Roster * Operation, CommandFailure> =
    if grant.DatasetId <> stored.Roster.DatasetId then
        Error(Unstorable [ UnknownDataset stored.Roster.DatasetId ])
    else
        execute performer rosterCommand stored.Roster
        |> Result.mapError Refused
        |> Result.bind (fun after ->
            changes stored after
            |> Result.bind (fun found -> Storage.operation ns context $"administrators of {after.DatasetId}" found)
            |> Result.mapError Unstorable
            |> Result.map (fun operation -> after, operation))

/// The stored roster after a commit of `after` landed with `receipt`: the
/// new revisions, so the next command conditions on them.
let committed (after: Roster) (receipt: CommitReceipt) (stored: StoredRoster) : StoredRoster =
    let hashOf (membership: Membership) =
        encode after.DatasetId membership
        |> Result.toOption
        |> Option.bind (Record.decode Record.DefaultMaxBytes >> Result.toOption)
        |> Option.map Record.contentHash
        |> Option.defaultValue ""

    let storedAs (principalId: string) (membership: Membership) : Loading.Stored<StoredMembership> option =
        match path principalId with
        | Error _ -> None
        | Ok target ->
            match receipt.Revisions.TryFind(RelativePath.render target), stored.Stored.TryFind principalId with
            | Some(Some revision), _ ->
                Some
                    { Value =
                        { DatasetId = after.DatasetId
                          Membership = membership }
                      Path = target
                      Revision = revision
                      ContentHash = hashOf membership }
            | _, found -> found

    { stored with
        Roster = after
        Stored =
            after.Members
            |> Map.toList
            |> List.choose (fun (principalId, membership) -> storedAs principalId membership |> Option.map (fun found -> principalId, found))
            |> Map.ofList }
