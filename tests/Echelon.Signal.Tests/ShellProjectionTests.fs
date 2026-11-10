/// The administrator shell's projections (SIG-ECHELON-APP-002 (b), review of
/// signal#136): unresolved work is one item per obligation per group, each
/// naming its group and a path through the console that resolves it; the
/// group filters read as the phase chips do; the destructive dataset
/// lifecycle targets are marked; the Retention panel never stands empty.
module Echelon.Signal.Tests.ShellProjectionTests

open System
open Xunit
open Echelon.Signal.Admin
open Echelon.Signal.Tests.AdminPageTests

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
    // Batches are identified by the exact set of links they carry: only the
    // same links, imported again, resume one (signal#136 review round 2).
    Assert.Equal(AdminView.resumeImport, resolution)
    Assert.Equal(
        "Import exactly the same completed-survey links again to resume: Signal finishes what was interrupted, skips what it already decided and never counts a link twice. Resume before finalizing the group: finalizing does not wait for it.",
        resolution
    )
    // Finalizing does not wait for it: an unknown chunk is never stored
    // (signal#136 re-review), so the resolution never claims it blocks.
    Assert.DoesNotContain("cannot be finalized", resolution)

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

[<Fact>]
let ``an import whose outcome stays unknown is unresolved work naming its group, its link and how to resume`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "/groups")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")
    let key = page.Text "groupKey"
    let id = (Echelon.Signal.Engine.UrlState.OpaqueId.ofBytes (Convert.FromHexString key)).Value
    Assert.True(page.Flag "noObligations", page.ViewText)
    Assert.Equal(AdminView.noUnresolvedWork, page.Text "noUnresolvedWork")
    Assert.Equal(
        "No unresolved work found. An import that did not finish, or whose outcome Signal could not confirm, is not always listed here; when it is, only until the page is refreshed or reopened.",
        page.Text "noUnresolvedWork"
    )

    // The import's commit is sent but the provider never says whether it
    // landed: the batch needs reconciliation.
    page.CommitsStayUnknown(true)
    page.Event("importText", value = String.concat "\n" [ link 1uy id; link 2uy id ])
    page.Event("import")

    Assert.True(page.Flag "hasObligations", page.ViewText)
    Assert.False(page.Flag "noObligations")
    let item = page.Items "obligations" |> List.exactlyOne
    let field (name: string) = item[name].GetValue<string>()
    Assert.Equal("ReconcileUnknownWrite", field "code")
    // What it says is true: no Refresh reconciles it; importing the same
    // links again is the one resolution (signal#136 re-review).
    Assert.Equal("1 import(s) did not finish or could not be confirmed: import the same links again.", field "text")
    Assert.Equal(key, field "group")
    Assert.Equal(Routes.href (Routes.Imports(key, [])), field "href")
    Assert.Equal("Open the group's imported surveys", field "linkLabel")
    Assert.Equal(AdminView.resumeImport, field "resolution")
    // The Imported surveys tab says the same, where the import is.
    Assert.Equal(AdminView.resumeImport, page.Text "resumeImport")
    // The notice says how to resume, not the provider's general "it is
    // checked before anything is sent again" (signal#136 re-review).
    Assert.Equal("SIGNAL.STORAGE.OUTCOME_UNKNOWN", page.Text "noticeCode")
    Assert.Equal(Echelon.Signal.Application.AdminWork.importOutcomeUnknown, page.Text "notice")
    Assert.StartsWith("The provider did not say whether the import was saved. Import exactly the same completed-survey links again to resume", page.Text "notice")
    Assert.DoesNotContain("checked before anything is sent again", page.Text "notice")

[<Fact>]
let ``an unfinished import is described by what resolves it, on the overview and in finalize's refusal`` () =
    let said = AdminState.describeObligation (AdminState.ReconcileUnknownWrite 2)
    Assert.Equal("2 import(s) did not finish or could not be confirmed: import the same links again.", said)
    Assert.DoesNotContain("before anything is sent again", said)
    // Finalize's refusal (BLOCKED_BY_OBLIGATIONS) names stored part-way and
    // blocked batches with the same sentence.
    Assert.Equal<AdminState.Obligation list>(
        [ AdminState.ReconcileUnknownWrite 3 ],
        AdminState.ofGroup [ GroupLifecycle.ReconcileUnknownImport 3 ]
    )

[<Fact>]
let ``the release record's unknown outcome says nothing about the import's`` () =
    Assert.Equal(
        "The provider did not say whether the record of which results may be shown was saved. Refresh to see what the store holds.",
        Echelon.Signal.Application.AdminWork.releaseOutcomeUnknown
    )
    Assert.DoesNotContain("import", Echelon.Signal.Application.AdminWork.releaseOutcomeUnknown)
