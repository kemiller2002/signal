/// Loading a dataset as untrusted input (ADM-046, ADM-025, ADM-005).
///
/// Everything a provider returns is untrusted, even what Signal wrote: a
/// person may edit the repository by hand, a compromised credential may
/// alter it, and a provider may return truncated, stale, duplicated or
/// malformed content. Loading is therefore staged, and the stages are types:
///
/// 1. `verify` checks Arca's manifest and Signal's storage manifest and is
///    the only way to obtain a `VerifiedDataset`.
/// 2. `load` takes a `VerifiedDataset`, so no record of a dataset is read
///    as domain data before its manifests are proven. Each object must be a
///    canonical record of the expected type at its own path, within the size
///    limit, at a schema version this Signal reads, for this dataset; ids are
///    unique; references resolve; references have no cycles; a partial
///    listing is reported. An unusable object is held aside with its revision
///    and never overwritten on a guess, and every failure has a typed code.
/// 3. `writable` grants changes only for a verified, completely loaded
///    dataset whose credential may write directly (ADM-073): until then the
///    dataset is read-only.
///
/// Malformed input cannot cause unbounded work: Arca bounds object size and
/// JSON depth, and the reference walk visits each record once.
///
/// Pure.
module Echelon.Signal.Admin.Loading

open Arca
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

/// A dataset whose Arca manifest and Signal storage manifest were checked.
/// Only `verify` makes one.
[<NoComparison>]
type VerifiedDataset =
    private
        { DatasetId: string
          Namespace: Namespace
          ArcaManifest: Manifest
          StorageManifest: DatasetManifest.StorageManifest }

    member this.Id = this.DatasetId
    member this.Folder = this.Namespace
    member this.Manifest = this.StorageManifest

/// Stage 1: both manifests, read from the dataset's folder, checked.
let verify
    (supported: DatasetManifest.FormatVersions)
    (ns: Namespace)
    (datasetId: string)
    (arcaManifest: ReadOutcome)
    (storageManifest: ReadOutcome)
    : Result<VerifiedDataset, Problem list> =
    Storage.openNamespace ns arcaManifest
    |> Result.bind (fun arca ->
        DatasetManifest.read supported datasetId (Storage.root ns) storageManifest
        |> Result.mapError List.singleton
        |> Result.map (fun manifest ->
            { DatasetId = datasetId
              Namespace = ns
              ArcaManifest = arca
              StorageManifest = manifest }))

/// How to read one record type.
[<NoComparison; NoEquality>]
type RecordReader<'a> =
    { Type: RecordType
      Schema: SchemaSupport
      MaxBytes: int64
      Decode: Json -> Decoded<'a>
      /// The record's id as its body states it.
      IdOf: 'a -> string
      /// The dataset the record says it belongs to, when it says.
      DatasetOf: 'a -> string option
      /// The ids of the records of this type it refers to.
      References: 'a -> string list }

/// One record as loaded.
[<NoComparison>]
type Stored<'a> =
    { Value: 'a
      Path: RelativePath
      Revision: Revision
      /// `sha256:` of the canonical record, to detect later changes.
      ContentHash: string }

/// What a folder holds, as far as Signal may trust it.
[<NoComparison>]
type Loaded<'a> =
    { /// Valid records, by id.
      Records: Map<string, Stored<'a>>
      /// Objects that cannot be used, by path, with the revision seen.
      Unusable: Map<string, Revision>
      /// Every problem found, in a stable order.
      Problems: Problem list }

let private decodeOne (verified: VerifiedDataset) (reader: RecordReader<'a>) (stored: StoredObject) : Result<Stored<'a>, Problem> =
    let where = RelativePath.render stored.Path

    match Layout.keyOf stored.Path with
    | Some key when key.Type = reader.Type ->
        Integrity.validate key reader.Schema reader.MaxBytes stored
        |> Result.mapError (fun failure -> InvalidStoredRecord(where, describeIntegrity failure))
        |> Result.bind (fun valid ->
            reader.Decode valid.Record.Body
            |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail))
            |> Result.map (fun value ->
                { Value = value
                  Path = stored.Path
                  Revision = stored.Revision
                  ContentHash = valid.ContentHash }))
        |> Result.bind (fun found ->
            if reader.IdOf found.Value <> RecordId.value key.Id then
                Error(MisplacedRecord where)
            else
                match reader.DatasetOf found.Value with
                | Some other when other <> verified.DatasetId -> Error(WrongDataset(where, other))
                | _ -> Ok found)
    | _ -> Error(MisplacedRecord where)

/// Cycles in the reference graph, each reported once by its sorted members.
let private cycles (edges: Map<string, string list>) =
    // Depth-first search; each node is finished once, so the walk is linear.
    let rec visit (path: string list) (finished: Set<string>, found: Set<string list>) (node: string) =
        if List.contains node path then
            let cycle = node :: (path |> List.takeWhile (fun n -> n <> node)) |> List.sort
            finished, found.Add cycle
        elif finished.Contains node then
            finished, found
        else
            let finished', found' =
                edges.TryFind node
                |> Option.defaultValue []
                |> List.filter edges.ContainsKey
                |> List.fold (visit (node :: path)) (finished, found)

            finished'.Add node, found'

    edges |> Map.keys |> Seq.fold (visit []) (Set.empty, Set.empty) |> snd |> Set.toList

/// Stage 2: the objects read from one record type's folder, validated.
/// `listing` is the folder's listing, so a partial one is reported.
let load (verified: VerifiedDataset) (reader: RecordReader<'a>) (folder: RelativePath) (listing: Listing) (objects: StoredObject list) : Loaded<'a> =
    let decoded = objects |> List.map (fun stored -> stored, decodeOne verified reader stored)

    let invalid =
        decoded |> List.choose (fun (stored, result) -> match result with Error p -> Some(stored, p) | Ok _ -> None)

    let valid = decoded |> List.choose (fun (_, result) -> match result with Ok found -> Some found | Error _ -> None)

    let duplicated =
        valid |> List.countBy (fun found -> reader.IdOf found.Value) |> List.filter (fun (_, n) -> n > 1) |> List.map fst |> Set.ofList

    let records =
        valid
        |> List.filter (fun found -> not (duplicated.Contains(reader.IdOf found.Value)))
        |> List.map (fun found -> reader.IdOf found.Value, found)
        |> Map.ofList

    let edges = records |> Map.map (fun _ found -> reader.References found.Value)

    let dangling =
        edges
        |> Map.toList
        |> List.collect (fun (id, targets) ->
            targets
            |> List.filter (fun target -> not (records.ContainsKey target) && not (duplicated.Contains target))
            |> List.map (fun target -> DanglingReference(id, target)))

    { Records = records
      Unusable =
        (invalid |> List.map (fun (stored, _) -> RelativePath.render stored.Path, stored.Revision))
        @ (valid
           |> List.filter (fun found -> duplicated.Contains(reader.IdOf found.Value))
           |> List.map (fun found -> RelativePath.render found.Path, found.Revision))
        |> Map.ofList
      Problems =
        (if listing.Complete then [] else [ IncompleteRead(RelativePath.render folder) ])
        @ (invalid |> List.map snd)
        @ (duplicated |> Set.toList |> List.map DuplicateRecord)
        @ dangling
        @ (cycles edges |> List.map ReferenceCycle) }

/// The record files of a folder's listing worth reading.
let recordFiles (listing: Listing) : RelativePath list =
    listing.Entries |> List.filter (fun entry -> not entry.IsFolder) |> List.map _.Path

/// Permission to change a dataset: only `writable` makes one.
type WriteGrant =
    private
        { Dataset: string }

    member this.DatasetId = this.Dataset

/// Stage 3: changes are allowed only when the dataset is verified, every
/// loaded folder was complete and clean, the provider offers what writing
/// needs, and the credential may write directly. Otherwise every reason.
let writable
    (verified: VerifiedDataset)
    (problems: Problem list)
    (capabilities: ProviderCapabilities)
    (mode: ProviderContract.WriteMode)
    : Result<WriteGrant, Problem list> =
    let capability =
        ProviderContract.missing ProviderContract.writeNeeds capabilities
        |> List.map (fun refusal -> CapabilityUnavailable($"%A{refusal.Capability}", refusal.Reason))

    let direct =
        match mode with
        | ProviderContract.DirectWriteAvailable -> []
        | other -> [ WritesUnavailable(ProviderContract.writeModeCode other) ]

    match problems @ capability @ direct with
    | [] -> Ok { Dataset = verified.DatasetId }
    | reasons -> Error reasons
