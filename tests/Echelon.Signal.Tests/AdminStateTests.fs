/// Administrator states, capabilities, obligations, the group lifecycle and
/// the conflict workspace (WI-0047): ADM-002, ADM-007, ADM-033, ADM-062,
/// ADM-065, ADM-066.
module Echelon.Signal.Tests.AdminStateTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Access
open Echelon.Signal.Admin.GroupLifecycle
open Echelon.Signal.Admin.AdminState

let private group (seed: byte) = (OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value

let private evidence complete =
    { Complete = complete
      UnreconciledBatches = 0
      RecordProblems = 0
      IndexCurrent = true
      MinimumReportableCount = 5 }

// ---- ADM-066 / ADM-065: the lifecycle ------------------------------------------------------

[<Fact>]
let ``completion and finalization are distinct, and finalization names what blocks it`` () =
    Assert.Equal(Ok Finalized, apply (Finalize(evidence true)) Collecting)
    Assert.Equal(Ok FinalizedIncomplete, apply (Finalize(evidence false)) ClosedIncomplete)

    let blocked =
        apply (Finalize { evidence false with UnreconciledBatches = 2; IndexCurrent = false; RecordProblems = 1 }) Collecting

    Assert.Equal(
        Error(BlockedByObligations [ CompletionNotSettled; ReconcileUnknownImport 2; RepairStoredRecords 1; RebuildDerivedIndex ]),
        blocked
    )

[<Fact>]
let ``only legal transitions apply; a sealed group is never reopened`` () =
    Assert.Equal(Ok ClosedIncomplete, apply Close Collecting)
    Assert.Equal(Ok Collecting, apply Reopen ClosedIncomplete)
    Assert.Equal(Ok Sealed, apply Seal Finalized)
    Assert.Equal(Error(Illegal("Collecting", "seal")), apply Seal Collecting)
    Assert.Equal(Error(Illegal("Sealed", "reopen")), apply Reopen Sealed)
    Assert.Equal(Error(Illegal("Sealed", "close")), apply Close Sealed)

    match apply (Supersede(group 2uy)) Sealed with
    | Ok(Superseded successor) -> Assert.Equal(group 2uy, successor)
    | other -> failwith $"%A{other}"

    Assert.False(acceptsContributions Sealed)
    Assert.False(acceptsConfiguration Finalized)
    Assert.True(acceptsConfiguration ClosedIncomplete)

[<Fact>]
let ``the lifecycle record round-trips`` () =
    let lifecycle = { initial (group 1uy) (Some(group 9uy)) with Status = Superseded(group 2uy); Revision = 3 }
    let text = encode "ds_engagement" lifecycle |> Result.defaultWith (fun e -> failwith $"{e}")
    let record = Arca.Record.decode Arca.Record.DefaultMaxBytes text |> Result.defaultWith (fun e -> failwith $"{e}")
    let back = ofBody record.Body |> Result.defaultWith failwith
    Assert.Equal(sprintf "%A" lifecycle, sprintf "%A" back.Lifecycle)

// ---- ADM-002 / ADM-033: capabilities from authoritative state -------------------------------------

let private everything = Grants.administrator

let private facts status accepted =
    Some
        { Status = status
          Accepted = accepted
          Expected = 3
          BatchRunning = false
          UnreconciledBatches = 0 }

[<Fact>]
let ``capabilities follow storage, the person and the group's state`` () =
    let configured = capabilities StorageConfigured everything (facts Collecting 1)
    Assert.Contains(CanImportBatch, configured)
    Assert.Contains(CanCloseGroup, configured)
    Assert.Contains(CanFinalizeGroup, configured)
    Assert.DoesNotContain(CanSealGroup, configured)
    Assert.DoesNotContain(CanReopenGroup, configured)

    // Degraded: reading only (ADM-033).
    let degraded = capabilities (DegradedReadOnly "offline") everything (facts Collecting 1)
    Assert.Equal<Set<AdminCapability>>(set [ CanReadStore; CanViewPartialReport; CanExport ], degraded)

    // A sealed group accepts nothing (ADM-065).
    let sealedGroup = capabilities StorageConfigured everything (facts Sealed 3)
    Assert.DoesNotContain(CanImportBatch, sealedGroup)
    Assert.DoesNotContain(CanFinalizeGroup, sealedGroup)

    // An analyst reads and exports, nothing else.
    let analyst = capabilities StorageConfigured Grants.analyst (facts Collecting 1)
    Assert.DoesNotContain(CanImportBatch, analyst)
    Assert.Contains(CanExport, analyst)
    Assert.Empty(capabilities Unconfigured everything None)

[<Fact>]
let ``a group's phase is derived, never stored`` () =
    let phaseOf status accepted running unreconciled =
        phase
            { Status = status
              Accepted = accepted
              Expected = 3
              BatchRunning = running
              UnreconciledBatches = unreconciled }

    Assert.Equal(GroupReady, phaseOf Collecting 0 false 0)
    Assert.Equal(GroupPartial, phaseOf Collecting 2 false 0)
    Assert.Equal(GroupComplete, phaseOf Collecting 3 false 0)
    Assert.Equal(Importing, phaseOf Collecting 2 true 0)
    Assert.Equal(Reconciling, phaseOf Collecting 2 true 1)
    Assert.Equal(GroupSealed, phaseOf Sealed 3 false 0)

// ---- ADM-007 / ADM-062: configuration changes and conflicts ------------------------------------------

let private config: GroupRecord.GroupConfig =
    { Group = group 1uy
      Mode = AnonymousGroup
      ExpectedCount = 10
      SurveyIdentifier = Pilot.assessment.Id
      TemplateVersion = Pilot.assessment.Version
      TemplateHash = Canonical.templateHash Pilot.assessment
      MinimumReportableCount = 5
      Retention = GroupRecord.NoneAfterImport
      Revision = 1 }

[<Fact>]
let ``changes that reinterpret accepted results need a successor group`` () =
    Assert.Equal(Ok { config with ExpectedCount = 12; Revision = 2 }, Conflicts.change Collecting 4 config { config with ExpectedCount = 12 })
    Assert.Equal(Error(Conflicts.RequiresSuccessorGroup [ "minimumReportableCount" ]), Conflicts.change Collecting 4 config { config with MinimumReportableCount = 3 })
    Assert.Equal(Error(Conflicts.RequiresSuccessorGroup [ "mode" ]), Conflicts.change Collecting 1 config { config with Mode = IdentifiedGroup })
    // Before any result exists the interpretation may still change.
    Assert.True(Conflicts.change Collecting 0 config { config with Mode = IdentifiedGroup } |> Result.isOk)
    Assert.Equal(Error(Conflicts.NotChangeable "Sealed"), Conflicts.change Sealed 0 config { config with ExpectedCount = 12 })

[<Fact>]
let ``independent changes merge deterministically; conflicting ones need a decision`` () =
    let current = { config with ExpectedCount = 20; Revision = 2 }
    let proposed = { config with Retention = GroupRecord.RetainCanonicalSubmission }
    let independent = Conflicts.analyze 3 config current proposed

    Assert.True(independent.Mergeable)
    let merged = Conflicts.resolve Conflicts.MergeIndependent config current proposed |> Option.get
    Assert.Equal((20, GroupRecord.RetainCanonicalSubmission, 3), (merged.ExpectedCount, merged.Retention, merged.Revision))

    let competing = Conflicts.analyze 3 config current { config with ExpectedCount = 15 }
    Assert.False(competing.Mergeable)
    Assert.Equal<string list>([ "expectedCount" ], competing.Differences |> List.filter _.Conflicting |> List.map _.Field)
    Assert.DoesNotContain(competing.Options, fun (resolution, _) -> resolution = Conflicts.MergeIndependent)
    // History is never rewritten: keeping current or moving to a successor writes no new version here.
    Assert.Equal(None, Conflicts.resolve Conflicts.KeepCurrent config current proposed)

    // A reinterpreting proposal against accepted results offers no in-place application.
    let reinterpreting = Conflicts.analyze 3 config current { config with MinimumReportableCount = 2 }
    Assert.DoesNotContain(reinterpreting.Options, fun (resolution, _) -> resolution = Conflicts.ApplyProposedOnCurrent)
