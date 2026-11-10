/// The administrator shell's projections (SIG-ECHELON-APP-002 (b), review of
/// signal#136): unresolved work is one item per obligation per group, each
/// naming its group and a path through the console that resolves it; the
/// group filters read as the phase chips do; the destructive dataset
/// lifecycle targets are marked; the Retention panel never stands empty.
module Echelon.Signal.Tests.ShellProjectionTests

open Xunit
open Echelon.Signal.Admin

[<Fact>]
let ``two groups with the same obligation are two items, each naming its group`` () =
    let items = AdminView.obligationsOf [ "g-one", 1, 0; "g-two", 1, 0; "g-three", 0, 0 ]
    Assert.Equal<(string * AdminState.Obligation) list>(
        [ "g-one", AdminState.ReconcileUnknownWrite 1; "g-two", AdminState.ReconcileUnknownWrite 1 ],
        items
    )

[<Fact>]
let ``a group with both obligations has both, and a clean dataset has none`` () =
    Assert.Equal<(string * AdminState.Obligation) list>(
        [ "g", AdminState.ReconcileUnknownWrite 2; "g", AdminState.RepairCorruptRecords 3 ],
        AdminView.obligationsOf [ "g", 2, 3 ]
    )
    Assert.Empty(AdminView.obligationsOf [ "g", 0, 0 ])

[<Fact>]
let ``each obligation links where it is resolved, and says what resolves it`` () =
    let link, label, resolution = AdminView.obligationPath "g-one" (AdminState.ReconcileUnknownWrite 1)
    Assert.Equal(Routes.href (Routes.Imports("g-one", [])), link)
    Assert.Equal("Open the group's imported surveys", label)
    Assert.Contains("Requiring reconciliation", resolution)

    let link, label, resolution = AdminView.obligationPath "g-one" (AdminState.RepairCorruptRecords 2)
    Assert.Equal(Routes.href Routes.Storage, link)
    Assert.Equal("Open Storage", label)
    Assert.Contains("cannot be finalized", resolution)

    let link, _, resolution = AdminView.obligationPath "g-one" AdminState.RebuildDerivedProjection
    Assert.Equal(Routes.href Routes.Storage, link)
    Assert.Contains("rebuilding the contribution index", resolution)

    // Two groups' identical obligations lead to their own groups.
    let one, _, _ = AdminView.obligationPath "g-one" (AdminState.ReconcileUnknownWrite 1)
    let two, _, _ = AdminView.obligationPath "g-two" (AdminState.ReconcileUnknownWrite 1)
    Assert.NotEqual<string>(one, two)
    Assert.Contains("g-two", two)

[<Fact>]
let ``the group filters read as the phase chips do`` () =
    Assert.Equal<string list>(
        [ "Collecting"; "Closed (incomplete)"; "Finalized"; "Finalized (incomplete)"; "Sealed"; "Superseded" ],
        Routes.statusValues |> List.map StateTones.filterLabel
    )
    Assert.Equal<string list>([ "Anonymous"; "Identified" ], Routes.modeValues |> List.map StateTones.filterLabel)

[<Fact>]
let ``retiring and deleting the dataset from active state are the destructive targets`` () =
    let all =
        [ Retention.Active; Retention.Archived; Retention.RetainedForAudit; Retention.Superseded; Retention.Retired
          Retention.DeletedFromActiveState; Retention.ProviderDeletionRequested; Retention.ProviderDeletionVerified; Retention.ProviderDeletionNotProvable ]
    Assert.Equal<Retention.State list>([ Retention.Retired; Retention.DeletedFromActiveState ], all |> List.filter RetentionScreen.isDestructive)

[<Fact>]
let ``the Retention panel says why nothing can be retired, and nothing when it can be`` () =
    Assert.Equal<string option>(Some "Accepted results can be retired once the group is finalized or sealed.", RetentionScreen.retentionNote GroupLifecycle.Collecting false false)
    Assert.Equal<string option>(Some "Accepted results can be retired once the group is finalized or sealed.", RetentionScreen.retentionNote GroupLifecycle.ClosedIncomplete false false)
    Assert.Equal<string option>(Some "Retiring accepted results needs a writable store and the ManageStorage grant.", RetentionScreen.retentionNote GroupLifecycle.Finalized false false)
    Assert.Equal<string option>(None, RetentionScreen.retentionNote GroupLifecycle.Finalized false true)
    Assert.Equal<string option>(None, RetentionScreen.retentionNote GroupLifecycle.Sealed true false)
