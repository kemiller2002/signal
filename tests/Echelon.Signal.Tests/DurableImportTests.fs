/// Durable import over Arca (WI-0041): ADM-008..011, ADM-027, ADM-060,
/// ADM-061, ADM-067, ARX-008, LURL-003, against Arca's in-memory provider.
module Echelon.Signal.Tests.DurableImportTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Application

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private run work = Async.RunSynchronously work
let private pilot = Pilot.assessment
let private opaque (seed: byte) = (OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value
let private groupId = opaque 200uy
let private resolve: GroupRecord.TemplateResolver = fun hash -> if hash = Canonical.templateHash pilot then Some pilot else None

let private all (answer: Answer) = pilot.Items |> List.map (fun item -> item.Id, answer) |> Map.ofList

let private link (binding: Binding) (answers: Answers) =
    "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = answers }

let private anonymous (seed: byte) answer = link (Anonymous(opaque seed, groupId)) (all (Rated answer))
let private identified (seed: byte) answer = link (Identified(opaque seed, groupId)) (all (Rated answer))

let private configText =
    """{"environment":"production","environmentName":"production",
"identity":{"exchange":"https://fides.test","application":"signal-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://signal.test/admin/"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""

let private config = Deployment.parse configText |> ok
let private at = DateTimeOffset(2026, 10, 8, 16, 0, 0, TimeSpan.Zero)
let private octocat = { PrincipalId = "github:583231"; Kind = Human; DisplayName = "octocat" }
let private keys = ref 0

let private actor: Store.Actor =
    { Principal = octocat
      SignIn = Credential.SignedIn "583231"
      NewContext =
        fun () ->
            { Actor = { Kind = ActorKind.Human; Id = ActorId.create octocat.PrincipalId |> ok }
              ProviderIdentity = None
              CorrelationId = CorrelationId.create "req-import" |> ok
              IdempotencyKey = IdempotencyKey.create $"op-import-{Threading.Interlocked.Increment keys:D6}" |> ok
              At = at } }

let private backend (github: InMemoryStore) (reachable: unit -> bool) : Store.Backend =
    let offline () = async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "offline")))

    { Provider =
        fun _ ->
            let real = github.Provider

            { real with
                Read = fun ns p -> if reachable () then real.Read ns p else offline ()
                List = fun ns p -> if reachable () then real.List ns p else offline ()
                Commit = fun o -> if reachable () then real.Commit o else offline () }
      Resolve =
        fun location ->
            async.Return(
                Ok
                    { Identity = { Provider = "github"; Subject = "583231"; Login = Some "octocat"; Kind = IdentityKind.User }
                      RepositoryId = "R_1"
                      Repository = location.Repository
                      Visibility = RepositoryVisibility.Private
                      CanRead = true
                      CanWrite = true
                      Archived = false
                      Branch = BranchAccess.Writable }
            ) }

let private groupConfig mode expected retention : GroupRecord.GroupConfig =
    { Group = groupId
      Mode = mode
      ExpectedCount = expected
      SurveyIdentifier = pilot.Id
      TemplateVersion = pilot.Version
      TemplateHash = Canonical.templateHash pilot
      MinimumReportableCount = 2
      Retention = retention
      Revision = 1 }

/// A dataset with one group, opened.
let private setUp (github: InMemoryStore) (reachable: unit -> bool) mode retention =
    let opened = Store.openDataset (backend github reachable) config actor None "signal-test" "ds_engagement" at |> run |> ok
    GroupStore.create actor (groupConfig mode 10 retention) at opened |> run |> ok
    GroupStore.openGroup resolve groupId at opened |> run |> ok

let private import texts group = GroupStore.importBatch actor resolve ResultRecord.MultiPaste texts at group |> run |> ok

let private stateOf (texts: string list) mode =
    texts
    |> List.fold (fun s t -> importOne s t |> fst) (empty { Group = groupId; Mode = mode; ExpectedCount = 10; Template = pilot })

// ---- ADM-008/010: accepted contributions are durable and reproduce the result -------------

[<Fact>]
let ``an imported batch is stored and the reopened group reproduces the same result`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let texts = [ anonymous 1uy Often; anonymous 2uy Never; anonymous 3uy Sometimes ]
    let imported = import texts group

    Assert.Equal(Intake.Complete, imported.Summary.Status)
    Assert.Equal(3, imported.Summary.AcceptedCount)

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    Assert.Empty(reopened.Problems)
    let expected = Aggregation.aggregate (GroupRecord.policy reopened.Config) (stateOf texts AnonymousGroup)
    Assert.Equal(expected.Lineage, (GroupStore.result reopened).Lineage)
    Assert.Equal(sprintf "%A" expected, sprintf "%A" (GroupStore.result reopened))

[<Fact>]
let ``contributions are immutable records sharded by identity, and no raw URL is kept`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let text = anonymous 1uy Often
    import [ text ] group |> ignore
    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    let stored = reopened.Contributions |> Map.toList |> List.exactlyOne |> snd
    let rendered = RelativePath.render stored.Path

    Assert.StartsWith($"records/signal.result/{GroupRecord.groupKey groupId}/", rendered)
    Assert.Equal(None, stored.Value.Contribution.Submission)
    Assert.Equal(ResultRecord.MultiPaste, stored.Value.Contribution.Provenance.Origin)
    Assert.Equal(ResultRecord.artifactHash text, stored.Value.Contribution.Provenance.ArtifactHash)

    let everything = github.State.Objects |> Map.toList |> List.map (fun (_, o) -> o.Content) |> String.concat "\n"
    Assert.DoesNotContain("https://signal.example", everything)
    Assert.DoesNotContain(text.Substring(text.IndexOf "#r=" + 3), everything)
    // The import commits name Signal's import service, never the person (ADM-067).
    Assert.DoesNotContain("583231", github.State.History.Head.Message)

[<Fact>]
let ``with retention the canonical submission is kept, never the URL`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) IdentifiedGroup GroupRecord.RetainCanonicalSubmission
    let text = identified 4uy Often
    import [ text ] group |> ignore

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    let contribution = (reopened.Contributions |> Map.toList |> List.exactlyOne |> snd).Value.Contribution
    Assert.Equal(payloadOf text, contribution.Submission)
    let everything = github.State.Objects |> Map.toList |> List.map (fun (_, o) -> o.Content) |> String.concat "\n"
    Assert.DoesNotContain("https://signal.example", everything)

// ---- ADM-009 / ARX-008: idempotent, commuting, never overwritten -------------------------------

[<Fact>]
let ``replaying a batch is idempotent and accepts nothing twice`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let texts = [ anonymous 1uy Often; anonymous 2uy Never ]
    let first = import texts group
    let commitsAfterFirst = github.State.History.Length

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    let again = import (List.rev texts) reopened

    Assert.Equal(first.Batch.BatchId, again.Batch.BatchId)
    Assert.Equal(commitsAfterFirst, github.State.History.Length)
    Assert.Equal(2, again.Group.Accumulator.Accepted.Count)

    // The same artifacts in a new batch (with one more) are duplicates, not second surveys.
    let more = import (anonymous 3uy Sometimes :: texts) again.Group
    Assert.Equal((1, 2), (more.Summary.AcceptedCount, more.Summary.DuplicateCount))

[<Fact>]
let ``batch order never changes the accepted set or the result`` () =
    let texts = [ for seed in 1uy .. 7uy -> anonymous seed (if seed % 2uy = 0uy then Often else Rarely) ]

    let resultFor (ordered: string list) =
        let github = InMemoryStore()
        let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
        let imported = import ordered group
        GroupStore.result imported.Group

    Assert.Equal(sprintf "%A" (resultFor texts), sprintf "%A" (resultFor (List.rev texts)))

[<Fact>]
let ``two different artifacts for one identity in a batch: the lower hash wins, whatever the order`` () =
    let a = identified 5uy Often
    let b = identified 5uy Never

    let acceptedHash ordered =
        let github = InMemoryStore()
        let group = setUp github (fun () -> true) IdentifiedGroup GroupRecord.NoneAfterImport
        let imported = import ordered group
        Assert.Equal((1, 1), (imported.Summary.AcceptedCount, imported.Summary.RejectedCount))
        imported.Group.Accumulator.Accepted |> Map.toList |> List.exactlyOne |> snd

    Assert.Equal(acceptedHash [ a; b ], acceptedHash [ b; a ])

[<Fact>]
let ``concurrent imports of distinct submissions commute; the same identity is decided again`` () =
    let github = InMemoryStore()
    let first = setUp github (fun () -> true) IdentifiedGroup GroupRecord.NoneAfterImport
    // A second administrator's tab opened the group at the same state.
    let second = GroupStore.openGroup resolve groupId at first.Dataset |> run |> ok

    let mine = import [ identified 1uy Often ] first
    let theirs = import [ identified 2uy Never; identified 1uy Rarely ] second

    // Distinct identities both land; the stale tab's different artifact for
    // identity 1 is a conflict, decided again: a duplicate, never an overwrite.
    Assert.Equal(1, mine.Summary.AcceptedCount)
    Assert.Equal((1, 1), (theirs.Summary.AcceptedCount, theirs.Summary.RejectedCount))
    let final = GroupStore.openGroup resolve groupId at first.Dataset |> run |> ok
    Assert.Equal(2, final.Accumulator.Accepted.Count)
    Assert.Equal(mine.Group.Accumulator.Accepted["instance:" + string (opaque 1uy)], final.Accumulator.Accepted["instance:" + string (opaque 1uy)])

[<Fact>]
let ``an unknown outcome that landed is reconciled, not imported twice`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    github.Arrange InMemoryFault.OutcomeUnknownLanded
    let imported = import [ anonymous 1uy Often ] group

    Assert.Equal(Intake.Complete, imported.Summary.Status)
    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    Assert.Equal(1, reopened.Accumulator.Accepted.Count)

// ---- ADM-060: resumable batches; offline without a queue ----------------------------------------

[<Fact>]
let ``an interrupted batch resumes where it stopped and counts nothing twice`` () =
    let github = InMemoryStore()
    let mutable reachable = true
    let group = setUp github (fun () -> reachable) AnonymousGroup GroupRecord.NoneAfterImport
    let texts = [ for seed in 1uy .. 40uy -> anonymous seed Often ]

    // A batch larger than one chunk is committed chunk by chunk.
    let whole = import texts group
    Assert.Equal(Intake.Complete, whole.Summary.Status)
    Assert.Equal(40, whole.Summary.AcceptedCount)
    ignore reachable

    // The connection drops after the first chunk's commit: the batch stops
    // with the rest remaining, and nothing is queued.
    let github2 = InMemoryStore()
    let allowed = ref 3 // the dataset's two set-up commits, the group, then one chunk
    let backend2 = backend github2 (fun () -> true)

    let flaky: Store.Backend =
        { backend2 with
            Provider =
                fun location ->
                    let real = backend2.Provider location

                    { real with
                        Commit =
                            fun operation ->
                                if allowed.Value > 0 then
                                    allowed.Value <- allowed.Value - 1
                                    real.Commit operation
                                else
                                    async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "offline"))) } }

    let opened = Store.openDataset flaky config actor None "signal-test" "ds_engagement" at |> run |> ok
    GroupStore.create actor (groupConfig AnonymousGroup 50 GroupRecord.NoneAfterImport) at opened |> run |> ok
    allowed.Value <- 1
    let group2 = GroupStore.openGroup resolve groupId at opened |> run |> ok
    let stopped = GroupStore.importBatch actor resolve ResultRecord.ImportedTextFile texts at group2 |> run |> ok

    match stopped.StoppedBecause with
    | Some(GroupStore.Offline _) -> ()
    | other -> failwith $"%A{other}"

    Assert.Equal((25, 15, Intake.Running), (stopped.Summary.AcceptedCount, stopped.Summary.RemainingCount, stopped.Summary.Status))
    // Cancelling the remainder keeps what was committed.
    let cancelled = Intake.summarize (Intake.cancel stopped.Batch)
    Assert.Equal((Intake.CancelledBeforeProcessingRemainder, 25), (cancelled.Status, cancelled.AcceptedCount))

    // Back online, the same batch resumes from its stored record and counts nothing twice.
    allowed.Value <- 100
    let reopened = GroupStore.openGroup resolve groupId at opened |> run |> ok
    Assert.Equal(25, reopened.Accumulator.Accepted.Count)
    let resumed = GroupStore.importBatch actor resolve ResultRecord.ImportedTextFile (List.rev texts) at reopened |> run |> ok
    Assert.Equal(stopped.Batch.BatchId, resumed.Batch.BatchId)
    Assert.Equal((40, 0, Intake.Complete), (resumed.Summary.AcceptedCount, resumed.Summary.DuplicateCount, resumed.Summary.Status))
    Assert.Equal(40, resumed.Group.Accumulator.Accepted.Count)

// ---- ADM-061: quarantine; rejected artifacts change nothing ----------------------------------------

[<Fact>]
let ``rejected and blocked artifacts never reach results, and leave only codes`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let wrongGroup = link (Anonymous(opaque 9uy, opaque 99uy)) (all (Rated Often))
    let imported = import [ anonymous 1uy Often; wrongGroup; "not a url at all" ] group

    Assert.Equal(Intake.CompleteWithRejections, imported.Summary.Status)
    Assert.Equal((1, 2), (imported.Summary.AcceptedCount, imported.Summary.RejectedCount))
    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    Assert.Equal(1, reopened.Accumulator.Accepted.Count)

    let quarantined = Intake.quarantine reopened.Definition (Incremental.acceptedFor reopened.Accumulator) [ Intake.artifact wrongGroup ]
    Assert.Empty(quarantined.Head.Evidence)
    Assert.Equal(None, Intake.promote groupId GroupRecord.NoneAfterImport ResultRecord.PastedUrl "b" quarantined.Head)

[<Fact>]
let ``a group whose template is not in the catalog is blocked, not guessed`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport

    match GroupStore.openGroup (fun _ -> None) groupId at group.Dataset |> run with
    | Error(GroupStore.TemplateUnavailable reason) -> Assert.Contains("not in the catalog", reason)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a contribution edited outside Signal is held aside and does not count`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    import [ anonymous 1uy Often; anonymous 2uy Never ] group |> ignore
    let before = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    let stored = before.Contributions |> Map.toList |> List.head |> snd
    let ns = group.Dataset.Namespace
    let full = Namespace.resolve ns stored.Path |> ok

    // Someone edits a score by hand in the repository.
    let tampered = (InMemory.read ns stored.Path github.State |> fst |> ok |> function ReadOutcome.Found f -> f.Content | _ -> "").Replace("\"scored\":true", "\"scored\":true ")
    github.WriteExternally(ns.Location, full.Path, Some tampered)

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    Assert.Equal(1, reopened.Accumulator.Accepted.Count)
    Assert.Equal<string list>([ "SIGNAL.STORAGE.INVALID_RECORD" ], reopened.Problems |> List.map Problems.code)

// ---- ADM-027: the contribution index is derived and rebuildable -------------------------------------

[<Fact>]
let ``the contribution index is rebuilt from the records, detected stale, and rebuilt the same`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    import [ anonymous 1uy Often ] group |> ignore

    Assert.Equal(IndexStatus.Missing, GroupStore.validateIndex at group.Dataset |> run |> ok)
    let built = GroupStore.rebuildIndex actor at group.Dataset |> run |> ok
    Assert.Equal(IndexStatus.Current, GroupStore.validateIndex at group.Dataset |> run |> ok)
    Assert.Equal(1, built.Entries.Length)

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    import [ anonymous 2uy Often ] reopened |> ignore

    match GroupStore.validateIndex at group.Dataset |> run |> ok with
    | IndexStatus.Stale _ -> ()
    | other -> failwith $"%A{other}"

    let rebuilt = GroupStore.rebuildIndex actor at group.Dataset |> run |> ok
    Assert.Equal(2, rebuilt.Entries.Length)
    // The same records rebuild the same index.
    Assert.Equal(rebuilt, GroupStore.rebuildIndex actor at group.Dataset |> run |> ok)
    Assert.Empty((Derived.compare rebuilt rebuilt).Added)

// ---- ADM-030: every import is audited in its own commit, by hash only ----------------------------

[<Fact>]
let ``an import writes its audit records in the same commit, naming artifacts by hash only`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let texts = [ anonymous 1uy Often; anonymous 2uy Never ]
    let first = import texts group
    let importCommit = github.State.History.Head
    let more = import (anonymous 3uy Sometimes :: texts) first.Group
    Assert.Equal((1, 2), (more.Summary.AcceptedCount, more.Summary.DuplicateCount))

    let records, problems = GovernanceStore.audit at group.Dataset |> run |> ok
    Assert.Empty(problems)
    let counts = records |> List.countBy (fun r -> Audit.eventName r.Record.Event) |> Map.ofList
    Assert.Equal<Map<string, int>>(Map [ "StorageConfigured", 1; "GroupCreated", 1; "SubmissionAccepted", 2; "DuplicateRejected", 1 ], counts)

    // The first import's contributions and its audit record landed in one commit.
    Assert.True(importCommit.Touched |> Seq.exists (fun path -> (string path).Contains "signal.audit"))

    let accepted = records |> List.filter (fun r -> r.Record.Event = Audit.SubmissionAccepted) |> List.collect _.Record.Hashes |> List.map snd |> Set.ofList
    Assert.Equal<Set<string>>(texts @ [ anonymous 3uy Sometimes ] |> List.map ResultRecord.artifactHash |> Set.ofList, accepted)

    // No URL, answer or person reaches an audit record.
    let stored = github.State.Objects |> Map.toList |> List.filter (fun (path, _) -> (string path).Contains "signal.audit") |> List.map (fun (_, o) -> o.Content)
    Assert.Contains(stored, fun text -> text.Contains "SubmissionAccepted")

    for text in stored do
        for forbidden in [ "https://"; "signal.example"; "octocat"; "583231"; "#" ] do
            Assert.DoesNotContain(forbidden, text)

// ---- ADM-045 / ADM-064: retiring a finalized group's sources ------------------------------------

[<Fact>]
let ``retiring a finalized group's results removes them from Signal's state, claims no more, and records the limit`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let imported = import [ anonymous 1uy Often; anonymous 2uy Never; anonymous 3uy Sometimes ] group

    match GovernanceStore.retireSources actor at GovernanceStore.DeletionRequest imported.Group |> run with
    | Error(GovernanceStore.GroupNotFinalized "Collecting") -> ()
    | other -> failwith $"%A{other}"

    GroupStore.rebuildIndex actor at group.Dataset |> run |> ok |> ignore
    let closed = GroupAdmin.transition actor "close" at imported.Group |> run |> ok
    let finalized = GroupAdmin.transition actor "finalize" at closed |> run |> ok

    let thirtyDays = Map [ Retention.AcceptedResult, Retention.KeepForDays 30 ]

    match GovernanceStore.retireSources actor at (GovernanceStore.RetentionExpired thirtyDays) finalized |> run with
    | Error GovernanceStore.RetentionNotDue -> ()
    | other -> failwith $"%A{other}"

    let retired = GovernanceStore.retireSources actor (at.AddDays 31.0) (GovernanceStore.RetentionExpired thirtyDays) finalized |> run |> ok
    Assert.Equal(3, retired.Removed)
    // Arca never deletes an immutable record: the results leave Signal's state, not the tree.
    Assert.Equal(Retention.RemovedFromApplicationState, retired.Claim)
    let limitation = Option.get retired.Limitation
    Assert.Equal((Retention.FullReconstruction, Retention.NotReproducible), (limitation.Before, limitation.After))
    Assert.Contains(Retention.GroupAggregateRebuild, limitation.Breaks)
    Assert.True(limitation.LineageClosed)
    Assert.Contains("TREE-REMOVAL-UNAVAILABLE", limitation.Evidence.Reasons)
    Assert.Contains("RETENTION-EXPIRED", limitation.Evidence.Reasons)

    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    Assert.Empty(reopened.Contributions)
    Assert.Equal(Some "state-not-reproducible", reopened.SourcesRetired |> Option.map _.Remaining)
    Assert.Equal(0, reopened.Accumulator.Accepted.Count)
    Assert.Equal(3, github.State.Objects |> Map.filter (fun path _ -> path.Contains "signal.result") |> Map.count)

    let records, _ = GovernanceStore.audit at group.Dataset |> run |> ok
    Assert.Equal("state-not-reproducible", GovernanceStore.reconstructionOf records (string groupId))
    Assert.Equal("state-full-reconstruction", GovernanceStore.reconstructionOf records "AAECAwQFBgcICQoLDA0ODw")
