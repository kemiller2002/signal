/// Portable configuration packages (WI-0075, ADM-047, ADM-061): report
/// definitions and policy packs that move between datasets without any
/// response data. A package is canonical JSON, versioned and identified by
/// its hash; reading one quarantines it, and only a package whose every item
/// validates can be activated, explicitly, with its provenance.
///
/// A package can hold no credential, token, provider secret, result or PII:
/// its items have no such fields, unknown fields are refused, and every text
/// it carries is checked for credential and identity shapes.
///
/// Pure.
module Echelon.Signal.Admin.ConfigPackage

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Admin.Codec
open Echelon.Signal.Admin.ReportLibrary

[<Literal>]
let PackageSchema = 1

type Item =
    | ReportDefinitionItem of Entry
    | PolicyPackItem of PolicyPack.Pack

type Package =
    { PackageId: string
      SchemaVersion: int
      SourceVersion: string option
      Items: Item list }

// ---- Policy packs as JSON ---------------------------------------------------------------------

let private ruleJson (rule: PolicyPack.Rule) =
    let kind = PolicyPack.kindOf rule

    match rule with
    | PolicyPack.MinimumGroup n
    | PolicyPack.MinimumCell n
    | PolicyPack.MinimumDistribution n
    | PolicyPack.MinimumComparison n
    | PolicyPack.MinimumDiversity n -> Json.objectOf [ "rule", Json.String kind; "value", Json.Number(decimal n) ]
    | PolicyPack.AllowedChartTypes types -> Json.objectOf [ "rule", Json.String kind; "values", textArray (Set.toList types) ]
    | PolicyPack.ReportDetailDefault d -> Json.objectOf [ "rule", Json.String kind; "text", Json.String d ]
    | PolicyPack.SnapshotAtFinalization b
    | PolicyPack.AuditRequired b -> Json.objectOf [ "rule", Json.String kind; "flag", Json.Bool b ]
    | PolicyPack.RetentionDays(artifact, days) -> Json.objectOf [ "rule", Json.String "retention-days"; "artifact", Json.String artifact; "value", Json.Number(decimal days) ]

let private ruleOf (value: Json) : Decoded<PolicyPack.Rule> =
    let n () = integer "value" value

    text "rule" value
    |> Result.bind (fun kind ->
        match kind with
        | "minimum-group" -> closed [ "rule"; "value" ] value |> Result.bind n |> Result.map PolicyPack.MinimumGroup
        | "minimum-cell" -> closed [ "rule"; "value" ] value |> Result.bind n |> Result.map PolicyPack.MinimumCell
        | "minimum-distribution" -> closed [ "rule"; "value" ] value |> Result.bind n |> Result.map PolicyPack.MinimumDistribution
        | "minimum-comparison" -> closed [ "rule"; "value" ] value |> Result.bind n |> Result.map PolicyPack.MinimumComparison
        | "minimum-diversity" -> closed [ "rule"; "value" ] value |> Result.bind n |> Result.map PolicyPack.MinimumDiversity
        | "allowed-chart-types" -> closed [ "rule"; "values" ] value |> Result.bind (fun () -> texts "values" value) |> Result.map (Set.ofList >> PolicyPack.AllowedChartTypes)
        | "report-detail-default" -> closed [ "rule"; "text" ] value |> Result.bind (fun () -> text "text" value) |> Result.map PolicyPack.ReportDetailDefault
        | "snapshot-at-finalization" -> closed [ "flag"; "rule" ] value |> Result.bind (fun () -> flag "flag" value) |> Result.map PolicyPack.SnapshotAtFinalization
        | "audit-required" -> closed [ "flag"; "rule" ] value |> Result.bind (fun () -> flag "flag" value) |> Result.map PolicyPack.AuditRequired
        | "retention-days" -> closed [ "artifact"; "rule"; "value" ] value |> Result.bind (fun () -> both (text "artifact" value) (n ())) |> Result.map PolicyPack.RetentionDays
        | other -> Error $"'{other}' is not a policy rule")

let packJson (p: PolicyPack.Pack) =
    Json.objectOf
        [ "policyPackId", Json.String p.PolicyPackId
          "version", Json.Number(decimal p.Version)
          "parent",
          (match p.Parent with
           | Some(id, v) -> Json.objectOf [ "policyPackId", Json.String id; "version", Json.Number(decimal v) ]
           | None -> Json.Null)
          "rules", Json.Array(p.Rules |> List.map ruleJson)
          "minimumSchema", Json.Number(decimal p.MinimumSchema) ]

let packOf (value: Json) : Decoded<PolicyPack.Pack> =
    let parent =
        field "parent" value
        |> Result.bind (function
            | Json.Null -> Ok None
            | p -> closed [ "policyPackId"; "version" ] p |> Result.bind (fun () -> both (text "policyPackId" p) (integer "version" p)) |> Result.map Some)

    closed [ "minimumSchema"; "parent"; "policyPackId"; "rules"; "version" ] value
    |> Result.bind (fun () -> both (both (text "policyPackId" value) (integer "version" value)) (both parent (both (list "rules" ruleOf value) (integer "minimumSchema" value))))
    |> Result.map (fun ((id, version), (parent, (rules, minimum))) -> { PolicyPackId = id; Version = version; Parent = parent; Rules = rules; MinimumSchema = minimum })

// ---- Packages ----------------------------------------------------------------------------------

let private itemJson (item: Item) =
    match item with
    | ReportDefinitionItem entry -> Json.objectOf [ "kind", Json.String "report-definition"; "id", Json.String entry.Definition.Id; "definition", ReportRecord.entryJson { entry with Used = false } ]
    | PolicyPackItem pack -> Json.objectOf [ "kind", Json.String "policy-pack"; "pack", packJson pack ]

let private itemOf (value: Json) : Decoded<Item> =
    text "kind" value
    |> Result.bind (function
        | "report-definition" ->
            closed [ "definition"; "id"; "kind" ] value
            |> Result.bind (fun () -> text "id" value)
            |> Result.bind (fun id -> field "definition" value |> Result.bind (ReportRecord.entryOf id))
            |> Result.map (fun entry -> ReportDefinitionItem { entry with Used = false })
        | "policy-pack" -> closed [ "kind"; "pack" ] value |> Result.bind (fun () -> field "pack" value) |> Result.bind packOf |> Result.map PolicyPackItem
        | other -> Error $"'{other}' is not a configuration item")

/// The package's canonical text: what is hashed and moved.
let encode (p: Package) =
    Json.objectOf
        [ "form", Json.String "signal-config/1"
          "packageId", Json.String p.PackageId
          "schemaVersion", Json.Number(decimal p.SchemaVersion)
          "sourceVersion", (match p.SourceVersion with Some v -> Json.String v | None -> Json.Null)
          "items", Json.Array(p.Items |> List.map itemJson) ]
    |> Json.canonicalText

let hashOf (text: string) =
    "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

// ---- Quarantine (ADM-061) -------------------------------------------------------------------------

/// Text shapes a configuration must never carry.
let private forbidden =
    Regex(@"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_|-----BEGIN|bearer\s|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

/// A package read from outside: held aside until every item validates.
type Quarantined =
    { Package: Package
      Hash: string
      /// Why it cannot be activated: empty when it can.
      Problems: string list }

let private validate (existingPacks: PolicyPack.Pack list) (p: Package) =
    let packs = p.Items |> List.choose (function PolicyPackItem pack -> Some pack | _ -> None)
    let all = existingPacks @ packs

    [ if p.SchemaVersion <> PackageSchema then $"schema version {p.SchemaVersion} is not {PackageSchema}"
      if p.Items.IsEmpty then "the package holds nothing"
      for item in p.Items do
          match item with
          | ReportDefinitionItem entry ->
              match ReportLibrary.save ReportLibrary.empty entry.Definition entry.Pins with
              | Error problem -> $"report definition {entry.Definition.Id}: %A{problem}"
              | Ok _ -> ()
          | PolicyPackItem pack ->
              match PolicyPack.resolve all pack.PolicyPackId pack.Version with
              | Error problems -> $"policy pack {pack.PolicyPackId} {pack.Version}: %A{problems}"
              | Ok _ -> () ]

/// Reads a package from outside, verifying its form, its canonical spelling
/// (so its hash is its identity) and every item, and scanning it for
/// credentials and identity. Nothing becomes active here.
let quarantine (existingPacks: PolicyPack.Pack list) (source: string) : Result<Quarantined, string list> =
    match Json.parse source with
    | Error error -> Error [ "not JSON: " + JsonError.describe error ]
    | Ok value ->
        let package =
            closed [ "form"; "items"; "packageId"; "schemaVersion"; "sourceVersion" ] value
            |> Result.bind (fun () -> text "form" value)
            |> Result.bind (fun form -> if form = "signal-config/1" then Ok() else Error $"'{form}' is not a configuration package")
            |> Result.bind (fun () ->
                both (both (text "packageId" value) (integer "schemaVersion" value)) (both (optional "sourceVersion" (function Json.String s -> Ok s | _ -> Error "sourceVersion") value) (list "items" itemOf value)))
            |> Result.map (fun ((id, schema), (source, items)) -> { PackageId = id; SchemaVersion = schema; SourceVersion = source; Items = items })

        match package with
        | Error reason -> Error [ reason ]
        | Ok p when encode p <> source -> Error [ "not the package's canonical form (an unknown member or another spelling)" ]
        | Ok p when forbidden.IsMatch source -> Error [ "it carries a credential- or identity-shaped value; configuration packages never do" ]
        | Ok p -> Ok { Package = p; Hash = hashOf source; Problems = validate existingPacks p }

/// What activating a package would do to each definition and pack.
type Impact =
    | NewDefinition of id: string
    | NextDefinitionVersion of id: string * from: int
    | SameDefinition of id: string
    | NewPack of id: string * version: int
    | SamePack of id: string * version: int
    | DifferentPackSameVersion of id: string * version: int

/// The dry run (ADM-047): the impact of each item, before anything changes.
let impact (library: Library) (packs: PolicyPack.Pack list) (q: Quarantined) =
    q.Package.Items
    |> List.map (function
        | ReportDefinitionItem entry ->
            match latest library entry.Definition.Id with
            | None -> NewDefinition entry.Definition.Id
            | Some current when { current.Definition with Version = entry.Definition.Version } = entry.Definition && current.Pins = entry.Pins -> SameDefinition entry.Definition.Id
            | Some current -> NextDefinitionVersion(entry.Definition.Id, current.Definition.Version)
        | PolicyPackItem pack ->
            match packs |> List.tryFind (fun p -> p.PolicyPackId = pack.PolicyPackId && p.Version = pack.Version) with
            | None -> NewPack(pack.PolicyPackId, pack.Version)
            | Some same when same = pack -> SamePack(pack.PolicyPackId, pack.Version)
            | Some _ -> DifferentPackSameVersion(pack.PolicyPackId, pack.Version))

/// Why a package may not be activated, if it may not.
let activationProblems (library: Library) (packs: PolicyPack.Pack list) (q: Quarantined) =
    q.Problems
    @ (impact library packs q
       |> List.choose (function
           | DifferentPackSameVersion(id, v) -> Some $"policy pack {id} {v} already exists with other rules; a change needs a new version"
           | _ -> None))

/// A package built from what a dataset holds, for another dataset.
let export (packageId: string) (sourceVersion: string option) (library: Library) (packs: PolicyPack.Pack list) : Package =
    { PackageId = packageId
      SchemaVersion = PackageSchema
      SourceVersion = sourceVersion
      Items =
        (library |> Map.toList |> List.map (fun (_, versions) -> ReportDefinitionItem(List.last versions)))
        @ (packs |> List.map PolicyPackItem) }
