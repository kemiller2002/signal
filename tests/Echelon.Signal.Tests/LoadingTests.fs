/// Loading a dataset as untrusted input (WI-0039, ADM-046, ADM-025,
/// ADM-005): manifests first, then records that must prove themselves, and
/// write permission only for a clean, verified dataset.
module Echelon.Signal.Tests.LoadingTests

open System
open Xunit
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private configText =
    """{"environment":"test","environmentName":"test",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"test"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""

let private config = Deployment.parse configText |> ok
let private binding = Storage.binding config |> ok
let private ns = Storage.datasetNamespace config binding "ds_engagement" |> ok
let private at = DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero)

let private context: Storage.OperationContext =
    { Actor =
        { Kind = ActorKind.Human
          Id = ActorId.create "github:583231" |> ok }
      ProviderIdentity = None
      CorrelationId = CorrelationId.create "req-1" |> ok
      IdempotencyKey = IdempotencyKey.create "op-load-0001" |> ok
      At = at }

/// A dataset set up in memory.
let private state =
    let commit operation (current: InMemoryState) =
        match InMemory.commit operation current with
        | Ok _, next -> next
        | Error failure, _ -> failwith $"%A{failure}"

    InMemory.empty
    |> commit (Storage.initializeApplication binding RepositoryVisibility.Private None context |> ok)
    |> commit (Storage.initializeDataset config binding RepositoryVisibility.Private None context "signal-test" "ds_engagement" [] |> ok)

let private read path = InMemory.read ns path state |> fst |> ok

let private verified =
    Loading.verify DatasetManifest.current ns "ds_engagement" (read (Layout.manifestPath |> ok)) (read (DatasetManifest.path "ds_engagement" |> ok))
    |> ok

// ---- A fixture record type ------------------------------------------------------------

type private Note =
    { Id: string
      Dataset: string
      Refs: string list }

let private noteType = RecordType.create "signal.note" |> ok

let private reader: Loading.RecordReader<Note> =
    { Type = noteType
      Schema = { Type = noteType; OldestReadable = 1; Current = 2 }
      MaxBytes = 4096L
      Decode =
        fun body ->
            closed [ "dataset"; "id"; "refs" ] body
            |> Result.bind (fun () ->
                match text "id" body, text "dataset" body, texts "refs" body with
                | Ok id, Ok dataset, Ok refs -> Ok { Id = id; Dataset = dataset; Refs = refs }
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e)
      IdOf = _.Id
      DatasetOf = fun note -> Some note.Dataset
      References = _.Refs }

let private folder = RelativePath.parse "records/signal.note" |> ok

let private pathOf (partition: string list) (id: string) =
    Layout.recordPath
        { Type = noteType
          Partition = partition |> List.map (Segment.create >> ok)
          Id = RecordId.create id |> ok }
    |> ok

let private encoded (schemaVersion: int) (note: Note) =
    { Id = RecordId.create note.Id |> ok
      Type = noteType
      SchemaVersion = schemaVersion
      Mutability = Mutability.Mutable
      Body = Json.objectOf [ "id", Json.String note.Id; "dataset", Json.String note.Dataset; "refs", textArray note.Refs ] }
    |> Record.encode Int64.MaxValue
    |> ok

let private stored (path: RelativePath) (content: string) : StoredObject =
    { Path = path
      Content = content
      Revision = Revision("rev-" + RelativePath.render path) }

let private note id refs = { Id = id; Dataset = "ds_engagement"; Refs = refs }
let private object' (n: Note) = stored (pathOf [] n.Id) (encoded 1 n)
let private complete = { Entries = []; Complete = true }

let private load objects = Loading.load verified reader folder complete objects
let private codesOf (loaded: Loading.Loaded<Note>) = loaded.Problems |> List.map code

// ---- Stage 1: manifests before records -----------------------------------------------

[<Fact>]
let ``a dataset is verified only when both manifests are proven`` () =
    Assert.Equal("ds_engagement", verified.Id)

    let without =
        Loading.verify DatasetManifest.current ns "ds_engagement" (read (Layout.manifestPath |> ok)) ReadOutcome.Absent

    Assert.Equal<string list>([ "SIGNAL.STORAGE.DATASET_NOT_INITIALIZED" ], without |> Result.mapError (List.map code) |> function Error c -> c | Ok _ -> [])

    let uninitialized = Loading.verify DatasetManifest.current ns "ds_engagement" ReadOutcome.Absent ReadOutcome.Absent
    Assert.Equal<string list>([ "SIGNAL.STORAGE.NOT_INITIALIZED" ], uninitialized |> Result.mapError (List.map code) |> function Error c -> c | Ok _ -> [])

// ---- Stage 2: records prove themselves -----------------------------------------------------

[<Fact>]
let ``valid records load with their revision and content hash`` () =
    let loaded = load [ object' (note "n1" [ "n2" ]); object' (note "n2" []) ]

    Assert.Empty(loaded.Problems)
    Assert.Equal<string list>([ "n1"; "n2" ], loaded.Records |> Map.keys |> List.ofSeq)
    Assert.Equal(Revision("rev-records/signal.note/n1.json"), loaded.Records["n1"].Revision)
    Assert.StartsWith("sha256:", loaded.Records["n1"].ContentHash)

[<Fact>]
let ``tampered, truncated, oversized and future records are held aside with typed reasons`` () =
    let good = object' (note "n1" [])
    let edit (f: string -> string) (o: StoredObject) = { o with Content = f o.Content }

    let cases =
        [ edit (fun t -> t.Replace("\"dataset\"", "\"dataset\" ")) good // not canonical
          edit (fun t -> t.Substring(0, t.Length - 3)) good // truncated
          stored (pathOf [] "big") (encoded 1 (note "big" [ String('x', 5000) ])) // over the reader's limit
          stored (pathOf [] "future") (encoded 3 (note "future" [])) ] // schema 3 > 2

    for case in cases do
        let loaded = load [ case ]
        Assert.Empty(loaded.Records)
        Assert.Equal<string list>([ "SIGNAL.STORAGE.INVALID_RECORD" ], codesOf loaded)
        Assert.True(loaded.Unusable.ContainsKey(RelativePath.render case.Path))

[<Fact>]
let ``a record at another record's path, or of another dataset, is refused`` () =
    // n1's content stored at n2's path: moved or forged outside Signal.
    let moved = stored (pathOf [] "n2") (encoded 1 (note "n1" []))
    Assert.Equal<string list>([ "SIGNAL.STORAGE.INVALID_RECORD" ], load [ moved ] |> codesOf)

    // A record of another dataset copied in (ADM-025).
    let foreign = object' { note "n3" [] with Dataset = "ds_other" }
    Assert.Equal<string list>([ "SIGNAL.STORAGE.WRONG_DATASET" ], load [ foreign ] |> codesOf)

    // An object outside any record folder.
    let stray = stored (RelativePath.parse "records/signal.note" |> ok) "{}"
    Assert.Equal<string list>([ "SIGNAL.STORAGE.MISPLACED_RECORD" ], load [ stray ] |> codesOf)

[<Fact>]
let ``an id stored twice is trusted in neither copy`` () =
    let first = object' (note "n1" [])
    let second = stored (pathOf [ "aa" ] "n1") (encoded 1 (note "n1" []))
    let loaded = load [ first; second ]

    Assert.Empty(loaded.Records)
    Assert.Equal<string list>([ "SIGNAL.STORAGE.DUPLICATE_RECORD" ], codesOf loaded)
    Assert.Equal(2, loaded.Unusable.Count)

[<Fact>]
let ``dangling references and reference cycles are reported`` () =
    let loaded = load [ object' (note "a" [ "b" ]); object' (note "b" [ "c" ]); object' (note "c" [ "a" ]); object' (note "d" [ "missing" ]) ]

    Assert.Equal<string list>([ "SIGNAL.STORAGE.DANGLING_REFERENCE"; "SIGNAL.STORAGE.REFERENCE_CYCLE" ], codesOf loaded)
    Assert.Contains(ReferenceCycle [ "a"; "b"; "c" ], loaded.Problems)
    Assert.Contains(DanglingReference("d", "missing"), loaded.Problems)

[<Fact>]
let ``a partial listing is a problem of its own`` () =
    let loaded = Loading.load verified reader folder { Entries = []; Complete = false } [ object' (note "n1" []) ]
    Assert.Equal<string list>([ "SIGNAL.STORAGE.INCOMPLETE_READ" ], codesOf loaded)

[<Fact>]
let ``a long reference chain is walked once, without deep recursion limits mattering`` () =
    let chain = [ for i in 1..2000 -> object' (note $"n{i}" (if i < 2000 then [ $"n{i + 1}" ] else [])) ]
    let loaded = load chain
    Assert.Empty(loaded.Problems)
    Assert.Equal(2000, loaded.Records.Count)

// ---- Stage 3: writes only for a clean, verified dataset -----------------------------------------

[<Fact>]
let ``writes are granted only to a clean dataset whose credential may write directly`` () =
    let clean = load [ object' (note "n1" []) ]
    let grant = Loading.writable verified clean.Problems InMemory.capabilities ProviderContract.DirectWriteAvailable
    Assert.Equal("ds_engagement", (grant |> ok).DatasetId)

    let refused problems mode =
        match Loading.writable verified problems InMemory.capabilities mode with
        | Ok _ -> []
        | Error reasons -> reasons |> List.map code

    let dirty = load [ object' (note "d" [ "missing" ]) ]
    Assert.Equal<string list>([ "SIGNAL.STORAGE.DANGLING_REFERENCE" ], refused dirty.Problems ProviderContract.DirectWriteAvailable)
    Assert.Equal<string list>([ "SIGNAL.STORAGE.WRITES_UNAVAILABLE" ], refused [] (ProviderContract.ProtectedBranchRequiresReview [ "reviews" ]))
    Assert.Equal<string list>([ "SIGNAL.STORAGE.WRITES_UNAVAILABLE" ], refused [] ProviderContract.ReadOnlyByPermission)
