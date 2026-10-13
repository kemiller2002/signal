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
open Echelon.Signal.Tests.DurableFixture

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

    let quarantined = Intake.quarantine (System.DateOnly(2026, 6, 1)) reopened.Definition (Incremental.acceptedFor reopened.Accumulator) [ Intake.artifact wrongGroup ]
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

[<Fact>]
let ``an item blocked in a run is not retried in the same run, so an import always ends`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    let imported = import [ anonymous 1uy Often; "https://signal.example/web/#r=AAAA" ] group
    Assert.Equal(1, imported.Summary.AcceptedCount)
    Assert.Equal(1, imported.Batch.Items |> List.filter (fun (_, o) -> match o with Some(Intake.BlockedUnsupportedEncoding _) -> true | _ -> false) |> List.length)

[<Fact>]
let ``a retained URL artifact and its exact template reconstruct the stored result (URLC-005)`` () =
    let github = InMemoryStore()
    let group = setUp github (fun () -> true) AnonymousGroup GroupRecord.RetainCanonicalSubmission
    import [ anonymous 1uy Often; anonymous 2uy Never ] group |> ignore
    let reopened = GroupStore.openGroup resolve groupId at group.Dataset |> run |> ok
    let contributions = reopened.Contributions |> Map.toList |> List.map (fun (_, c) -> c.Value.Contribution)
    Assert.All(contributions, fun c -> Assert.Equal(Intake.Reproduced, Intake.reconstruct reopened.Definition c))

    // Another template is not the exact template: the artifact does not reproduce the result.
    let other = { reopened.Definition with Template = { pilot with MinimumNumericAnswers = 5 } }
    Assert.All(contributions, fun c -> Assert.NotEqual(Intake.Reproduced, Intake.reconstruct other c))

    let plain = setUp (InMemoryStore()) (fun () -> true) AnonymousGroup GroupRecord.NoneAfterImport
    import [ anonymous 1uy Often ] plain |> ignore
    let reopenedPlain = GroupStore.openGroup resolve groupId at plain.Dataset |> run |> ok
    Assert.All(reopenedPlain.Contributions |> Map.toList, fun (_, c) -> Assert.Equal(Intake.NotRetained, Intake.reconstruct reopenedPlain.Definition c.Value.Contribution))
