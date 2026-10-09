/// Policy packs and configuration packages (WI-0075, ADM-047, ADM-048,
/// ADM-061): deterministic resolution with explicit conflicts, packages that
/// are canonical, hash-identified and quarantined until every item
/// validates, and activation in another dataset with its provenance.
module Echelon.Signal.Tests.ConfigPackageTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Admin.PolicyPack
open Echelon.Signal.Admin.ReportLibrary
open Echelon.Signal.Application
open Echelon.Signal.Tests.StoreFixture

let private pack id version parent rules = { PolicyPackId = id; Version = version; Parent = parent; Rules = rules; MinimumSchema = 1 }

[<Fact>]
let ``a policy pack resolves through its parents into explicit constraints, and conflicts fail`` () =
    let executive = PolicyPack.resolve builtIns "ExecutiveReporting" 1 |> ok
    let policy = disclosure executive Disclosure.defaultPolicy
    Assert.Equal((7, 7, 14, Some 3), (policy.MinimumGroup, policy.MinimumCell, policy.MinimumDistribution, policy.MinimumDiversity))
    Assert.True(requiresSnapshot executive)
    Assert.True(allowsChart executive "Bar")
    Assert.False(allowsChart executive "Pie")
    Assert.Equal(("StrictAnonymous", 1), snd executive.Rules["minimum-group"])

    // A child that repeats a parent's rule with another value is a conflict, never an override.
    let loose = pack "Loose" 1 (Some("StrictAnonymous", 1)) [ MinimumGroup 3 ]

    match PolicyPack.resolve (loose :: builtIns) "Loose" 1 with
    | Error [ ConflictingRules("minimum-group", [ ("Loose", 1); ("StrictAnonymous", 1) ]) ] -> ()
    | other -> failwith $"%A{other}"

    // The same value again is not a conflict.
    Assert.True(PolicyPack.resolve (pack "Same" 1 (Some("StrictAnonymous", 1)) [ MinimumGroup 7 ] :: builtIns) "Same" 1 |> Result.isOk)

    let a = pack "Aa" 1 (Some("Bb", 1)) []
    let b = pack "Bb" 1 (Some("Aa", 1)) []
    Assert.True(match PolicyPack.resolve [ a; b ] "Aa" 1 with Error [ ParentCycle _ ] -> true | _ -> false)
    Assert.Equal(Error [ UnknownPack("Nope", 1) ], PolicyPack.resolve builtIns "Nope" 1)
    Assert.True(match PolicyPack.resolve [ pack "Twice" 1 None [ MinimumCell 5; MinimumCell 6 ] ] "Twice" 1 with Error [ DuplicateRule _ ] -> true | _ -> false)
    Assert.True(match PolicyPack.resolve [ pack "Zero" 1 None [ MinimumCell 0 ] ] "Zero" 1 with Error [ InvalidRule _ ] -> true | _ -> false)

let private entry = { Definition = { ReportExport.executiveSummary with Id = "quarterly" }; Pins = currentPins (LatestTemplate "SDRA"); Used = false }

let private package =
    { ConfigPackage.PackageId = "team-defaults"
      ConfigPackage.SchemaVersion = ConfigPackage.PackageSchema
      ConfigPackage.SourceVersion = Some "signal-admin/1"
      ConfigPackage.Items = [ ConfigPackage.ReportDefinitionItem entry; ConfigPackage.PolicyPackItem(pack "TeamStrict" 1 (Some("StrictAnonymous", 1)) [ AuditRequired true ]) ] }

[<Fact>]
let ``a package is canonical, identified by its hash, and quarantined until it validates`` () =
    let text = ConfigPackage.encode package
    let q = ConfigPackage.quarantine builtIns text |> ok
    Assert.Empty(q.Problems)
    Assert.Equal(package, q.Package)
    Assert.Equal(ConfigPackage.hashOf text, q.Hash)

    Assert.True(ConfigPackage.quarantine builtIns (text.Replace("\"form\"", "\"email\":\"x\",\"form\"")) |> Result.isError)
    Assert.True(ConfigPackage.quarantine builtIns (text.Replace("team-defaults", "jane@example.com")) |> Result.isError)
    Assert.True(ConfigPackage.quarantine builtIns (text.Replace("team-defaults", "ghp_0123456789abcdefghij0123")) |> Result.isError)
    Assert.True(ConfigPackage.quarantine builtIns (text + " ") |> Result.isError)

    // Every item validates before activation: a conflicting pack is held in quarantine with its reason.
    let bad = { package with Items = [ ConfigPackage.PolicyPackItem(pack "Loose" 1 (Some("StrictAnonymous", 1)) [ MinimumGroup 2 ]) ] }
    let held = ConfigPackage.quarantine builtIns (ConfigPackage.encode bad) |> ok
    Assert.NotEmpty(held.Problems)

    Assert.Equal<ConfigPackage.Impact list>(
        [ ConfigPackage.NewDefinition "quarterly"; ConfigPackage.NewPack("TeamStrict", 1) ],
        ConfigPackage.impact ReportLibrary.empty builtIns q
    )

[<Fact>]
let ``a dataset's configuration moves to another through a package, activated explicitly with provenance`` () =
    let _, source, _ = dataset Access.Grants.analyst
    ReportStore.saveDefinition (actor octocat) at source entry.Definition entry.Pins |> run |> ok |> ignore
    let text = ConfigStore.export (actor octocat) at source "team-defaults" |> run |> ok
    Assert.DoesNotContain("acme", text)
    Assert.DoesNotContain("583231", text)

    let github, target, _ = dataset Access.Grants.analyst
    let q = ConfigPackage.quarantine builtIns text |> ok
    // Quarantined: nothing in the target yet.
    Assert.True((ReportStore.load at target |> run |> ok).Library.IsEmpty)

    let impact = ConfigStore.activate (actor octocat) at target q |> run |> ok
    Assert.Equal<ConfigPackage.Impact list>([ ConfigPackage.NewDefinition "quarterly" ], impact)
    Assert.Equal(1, (ReportStore.load at target |> run |> ok).Library["quarterly"].Length)

    let records, _ = GovernanceStore.audit at target |> run |> ok
    let provenance = records |> List.find (fun r -> r.Record.Event = Audit.ConfigurationActivated)
    Assert.Equal<(string * string) list>([ "package", q.Hash ], provenance.Record.Hashes)

    // Activating the same package again changes nothing.
    let before = github.State.History.Length
    Assert.Equal<ConfigPackage.Impact list>([ ConfigPackage.SameDefinition "quarterly" ], ConfigStore.activate (actor octocat) at target q |> run |> ok)
    Assert.Equal(before, github.State.History.Length)

    // A pack that differs from a stored one at the same version is refused.
    let first = ConfigPackage.quarantine builtIns (ConfigPackage.encode { package with Items = [ ConfigPackage.PolicyPackItem(pack "TeamStrict" 1 None [ AuditRequired true ]) ] }) |> ok
    ConfigStore.activate (actor octocat) at target first |> run |> ok |> ignore
    let changed = ConfigPackage.quarantine builtIns (ConfigPackage.encode { package with Items = [ ConfigPackage.PolicyPackItem(pack "TeamStrict" 1 None [ AuditRequired false ]) ] }) |> ok

    match ConfigStore.activate (actor octocat) at target changed |> run with
    | Error(ConfigStore.NotActivatable [ reason ]) -> Assert.Contains("needs a new version", reason)
    | other -> failwith $"%A{other}"
