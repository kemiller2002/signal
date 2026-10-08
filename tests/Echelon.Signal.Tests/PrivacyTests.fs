/// Administrator privacy (WI-0051): disclosure controls and differencing
/// across views (ADM-024, ARX-009), audit without PII (ADM-030), lifecycle,
/// retention and honest deletion claims (ADM-045), and reproducibility
/// against deletion (ADM-064).
module Echelon.Signal.Tests.PrivacyTests

open System
open Xunit
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Disclosure
open Echelon.Signal.Admin.Retention

let private at = DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero)
let private policy = Disclosure.defaultPolicy

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

// ---- Disclosure (ADM-024) ----------------------------------------------------------------------

[<Fact>]
let ``small groups, slices, distributions and comparisons are withheld only when anonymous`` () =
    Assert.Equal(Withhold(BelowGroupMinimum(4, 5)), group policy AnonymousGroup 4)
    Assert.Equal(Release 4, group policy IdentifiedGroup 4)
    Assert.Equal(Release 5, group policy AnonymousGroup 5)

    Assert.Equal(Withhold(BelowCellMinimum(3, 5)), filtered policy AnonymousGroup 3)

    let few = [ 1.0; 2.0; 3.0; 4.0; 5.0 ]
    Assert.Equal(Withhold(BelowDistributionMinimum(5, 10)), distribution policy AnonymousGroup few)
    Assert.Equal(Withhold(TooFewDistinctValues(2, 3)), distribution policy AnonymousGroup [ for i in 1..12 -> float (i % 2) ])
    Assert.Equal(Release few, distribution policy IdentifiedGroup few)

    Assert.Equal(Withhold(BelowComparisonMinimum(3, 5)), comparison policy AnonymousGroup 12 3)
    Assert.Equal(Release(12, 8), comparison policy AnonymousGroup 12 8)

[<Fact>]
let ``a single hidden cell is protected from subtraction by suppressing its complement`` () =
    let cells, total = breakdown policy AnonymousGroup [ "self", 2; "peer", 9; "manager", 6 ]
    let decisions = Map.ofList cells
    Assert.Equal(Withhold(BelowCellMinimum(2, 5)), decisions["self"])
    // The smallest shown cell is suppressed too, so total - shown cannot recover "self".
    Assert.Equal(Withhold ComplementaryCell, decisions["manager"])
    Assert.Equal(Release 9, decisions["peer"])
    Assert.Equal(Release 17, total)

    // With one other cell, that cell is the complement; a lone small cell hides the total.
    let pair, pairTotal = breakdown policy AnonymousGroup [ "self", 2; "peer", 9 ]
    Assert.Equal(Withhold ComplementaryCell, (Map.ofList pair)["peer"])
    Assert.Equal(Release 11, pairTotal)
    Assert.Equal(Withhold(BelowGroupMinimum(2, 5)), snd (breakdown policy AnonymousGroup [ "self", 2 ]))

    // Two hidden cells already protect each other.
    let two, _ = breakdown policy AnonymousGroup [ "self", 2; "peer", 9; "manager", 3 ]
    Assert.Equal(Release 9, (Map.ofList two)["peer"])

    // Identified groups are not suppressed.
    let identified, _ = breakdown policy IdentifiedGroup [ "self", 2; "peer", 9 ]
    Assert.Equal(Release 2, (Map.ofList identified)["self"])

[<Fact>]
let ``views whose populations nest and differ by fewer than the minimum are not both released`` () =
    let whole = { Id = "all"; Filters = Set.empty; Count = 20 }
    let first, ledger = release policy AnonymousGroup emptyLedger whole
    Assert.Equal(Release whole, first)

    // Filtering out a small slice: 20 - 18 reveals the 2 excluded responses.
    let narrowed = { Id = "not-managers"; Filters = set [ "role", "not-manager" ]; Count = 18 }
    let second, ledger = release policy AnonymousGroup ledger narrowed
    Assert.Equal(Withhold(Differencing("all", 2)), second)

    // A slice far enough from every nested view is released.
    let peers = { Id = "peers"; Filters = set [ "role", "peer" ]; Count = 9 }
    let third, ledger = release policy AnonymousGroup ledger peers
    Assert.Equal(Release peers, third)

    // Repeated snapshots: one new response would reveal itself; the view freezes until five arrive.
    let later = { whole with Id = "all-later"; Count = 21 }
    Assert.Equal(Withhold(Differencing("all", 1)), fst (release policy AnonymousGroup ledger later))
    let enough = { whole with Id = "all-enough"; Count = 25 }
    Assert.Equal(Release enough, fst (release policy AnonymousGroup ledger enough))

    // A slice below the cell minimum is withheld on its own.
    Assert.Equal(Withhold(BelowCellMinimum(4, 5)), fst (release policy AnonymousGroup ledger { peers with Id = "few"; Count = 4 }))

// ---- Audit (ADM-030) ---------------------------------------------------------------------------

let private hashText = "sha256:" + String('c', 64)

[<Fact>]
let ``audit records hold opaque ids, hashes and codes, and refuse anything personal`` () =
    let record =
        Audit.create Audit.ReportSnapshotCreated [ "snapshot", "snap-0123456789abcdef"; "group", "AAECAwQFBgcICQoLDA0ODw" ] [ "reportData", hashText ] 1 [ "FORMAL-SNAPSHOT" ] None (Some "state-active") (Some at)
        |> ok

    Assert.Equal(Audit.hashOf record, Audit.hashOf { record with ClockEvidence = None })
    Assert.DoesNotContain("2026", Audit.canonical record)

    let refused ids hashes reasons =
        match Audit.create Audit.ExportCreated ids hashes 1 reasons None None None with
        | Error problems -> problems
        | Ok r -> failwith $"accepted %A{r}"

    Assert.Equal<Audit.Problem list>([ Audit.NotOpaque "by" ], refused [ "by", "octocat" ] [] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotOpaque "by" ], refused [ "by", "jane-doe" ] [] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotOpaque "by" ], refused [ "by", "jane@example.com" ] [] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotOpaque "by" ], refused [ "by", "github:583231" ] [] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotOpaque "url" ], refused [ "url", "https://signal.test/#r=abc" ] [] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotOpaque "token" ], refused [ "token", "ghp_0123456789abcdefghij0123456789abcd" ] [] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotAHash "data" ], refused [] [ "data", "not a hash" ] [])
    Assert.Equal<Audit.Problem list>([ Audit.NotAReasonCode "the admin said so" ], refused [] [] [ "the admin said so" ])

// ---- Lifecycle and retention (ADM-045) ---------------------------------------------------------

[<Fact>]
let ``lifecycle transitions are legal only, audited, and a retired dataset is read-only`` () =
    let archived, record = transition "ds_engagement" (Some at) Active Archived |> ok
    Assert.Equal(Archived, archived)
    Assert.Equal(Audit.LifecycleChanged, record.Event)
    Assert.Equal((Some "state-active", Some "state-archived"), (record.Before, record.After))

    match transition "ds_engagement" None ProviderDeletionVerified Active with
    | Error(IllegalTransition(ProviderDeletionVerified, Active)) -> ()
    | other -> failwith $"%A{other}"

    match transition "jane-doe" None Active Archived with
    | Error(NotAudited [ Audit.NotOpaque "artifact" ]) -> ()
    | other -> failwith $"%A{other}"

    Assert.True(writable Active)
    Assert.False(writable Retired)
    Assert.False(writable Archived)

[<Fact>]
let ``retention is per class, and GitHub deletion is never claimed as permanent erasure`` () =
    Assert.True(due defaultPolicy SyntheticArtifact Active (TimeSpan.FromDays 31.0))
    Assert.False(due defaultPolicy SyntheticArtifact Active (TimeSpan.FromDays 29.0))
    Assert.False(due defaultPolicy AcceptedResult Active (TimeSpan.FromDays 9999.0))
    Assert.True(due defaultPolicy DerivedIndex Superseded TimeSpan.Zero)
    Assert.False(due defaultPolicy SyntheticArtifact RetainedForAudit (TimeSpan.FromDays 99.0))

    let github = providerOutcome HistoryRetaining true
    Assert.Equal(ProviderDeletionNotProvable, github)
    Assert.Equal(RemovedFromCurrentTree, claim { FromApplicationState = true; FromRepositoryTree = true; ProviderHistory = Some github })
    Assert.Equal(RemovedFromApplicationState, claim { FromApplicationState = true; FromRepositoryTree = false; ProviderHistory = None })
    Assert.Equal(PermanentlyErased, claim { FromApplicationState = true; FromRepositoryTree = true; ProviderHistory = Some(providerOutcome ProvableErasure true) })
    Assert.Equal(ProviderDeletionNotProvable, providerOutcome ProvableErasure false)

// ---- Reproducibility versus deletion (ADM-064) -------------------------------------------------

let private everything = set [ AcceptedResult; GroupResult; ReportState; ReportSnapshot; AuditRecord; ComparisonData ]

[<Fact>]
let ``deleting accepted results limits reproduction to aggregates and records why`` () =
    Assert.Equal(FullReconstruction, reconstruction everything)
    let held, limitation = delete "AAECAwQFBgcICQoLDA0ODw" (Some at) everything (set [ AcceptedResult ]) |> ok
    let limitation = limitation |> Option.get

    Assert.Equal(AggregateOnly, reconstruction held)
    Assert.Equal((FullReconstruction, AggregateOnly), (limitation.Before, limitation.After))
    Assert.Equal<Break list>([ GroupAggregateRebuild; MigrationVerification; ReportSnapshotRebuild; ComparisonVerification ], limitation.Breaks)
    Assert.True(limitation.LineageClosed)
    Assert.Equal(Audit.ReproducibilityLimited, limitation.Evidence.Event)
    Assert.Contains("BREAKS-GROUP-AGGREGATE-REBUILD", limitation.Evidence.Reasons)
    Assert.Equal(Some "state-aggregate-only", limitation.Evidence.After)

    // Then the aggregates too: only the issued snapshots stand, as history.
    let held, further = delete "AAECAwQFBgcICQoLDA0ODw" None held (set [ GroupResult; ReportState ]) |> ok
    Assert.Equal(HistoricalSnapshotsOnly, reconstruction held)
    Assert.Equal(HistoricalSnapshotsOnly, (Option.get further).After)

    // Removing a rebuildable layer while the sources remain loses nothing.
    let same, none = delete "AAECAwQFBgcICQoLDA0ODw" None everything (set [ ReportState ]) |> ok
    Assert.Equal(FullReconstruction, reconstruction same)
    Assert.True(none.IsNone)

    Assert.Equal<Break list>([ AuditVerification ], breaks everything (set [ AuditRecord ]))
    Assert.Equal(NotReproducible, reconstruction Set.empty)
