/// Versioned policy packs (WI-0075, ADM-048): reusable administrator
/// constraints as declared rules, never code. A pack may build on a parent;
/// resolution walks the chain deterministically, and a rule the chain
/// declares twice with different values is a conflict, never a silent
/// override. A resolved pack expands into explicit constraints: the
/// disclosure policy, allowed chart types, report detail, snapshot, audit
/// and retention requirements.
///
/// Pure.
module Echelon.Signal.Admin.PolicyPack

open System.Text.RegularExpressions

/// One declared rule.
type Rule =
    | MinimumGroup of int
    | MinimumCell of int
    | MinimumDistribution of int
    | MinimumComparison of int
    | MinimumDiversity of int
    | AllowedChartTypes of Set<string>
    | ReportDetailDefault of string
    | SnapshotAtFinalization of bool
    | AuditRequired of bool
    | RetentionDays of artifactClass: string * days: int

/// The kind a rule constrains: two rules of one kind are the same constraint.
let kindOf (rule: Rule) =
    match rule with
    | MinimumGroup _ -> "minimum-group"
    | MinimumCell _ -> "minimum-cell"
    | MinimumDistribution _ -> "minimum-distribution"
    | MinimumComparison _ -> "minimum-comparison"
    | MinimumDiversity _ -> "minimum-diversity"
    | AllowedChartTypes _ -> "allowed-chart-types"
    | ReportDetailDefault _ -> "report-detail-default"
    | SnapshotAtFinalization _ -> "snapshot-at-finalization"
    | AuditRequired _ -> "audit-required"
    | RetentionDays(artifact, _) -> "retention-days:" + artifact

type Pack =
    { PolicyPackId: string
      Version: int
      Parent: (string * int) option
      Rules: Rule list
      /// The oldest policy-pack schema a reader must understand.
      MinimumSchema: int }

[<Literal>]
let SchemaVersion = 1

type Problem =
    | UnknownPack of id: string * version: int
    | ParentCycle of chain: (string * int) list
    | ConflictingRules of kind: string * packs: (string * int) list
    | DuplicateRule of pack: string * kind: string
    | InvalidRule of pack: string * reason: string
    | UnsupportedSchema of pack: string * minimum: int
    | InvalidId of string

let private idPattern = Regex(@"^[A-Za-z][A-Za-z0-9-]{1,47}$", RegexOptions.CultureInvariant)

let private ruleProblems (pack: Pack) =
    let positive name n = if n < 1 || n > 10000 then [ InvalidRule(pack.PolicyPackId, $"{name} must be 1 to 10000") ] else []

    [ if not (idPattern.IsMatch pack.PolicyPackId) then InvalidId pack.PolicyPackId
      if pack.Version < 1 then InvalidRule(pack.PolicyPackId, "the version must be at least 1")
      if pack.MinimumSchema > SchemaVersion then UnsupportedSchema(pack.PolicyPackId, pack.MinimumSchema)
      for kind, n in pack.Rules |> List.countBy kindOf do
          if n > 1 then DuplicateRule(pack.PolicyPackId, kind)
      for rule in pack.Rules do
          match rule with
          | MinimumGroup n -> yield! positive "minimum-group" n
          | MinimumCell n -> yield! positive "minimum-cell" n
          | MinimumDistribution n -> yield! positive "minimum-distribution" n
          | MinimumComparison n -> yield! positive "minimum-comparison" n
          | MinimumDiversity n -> yield! positive "minimum-diversity" n
          | AllowedChartTypes types when types.IsEmpty -> InvalidRule(pack.PolicyPackId, "allowed-chart-types names no chart type")
          | ReportDetailDefault d when not (List.contains d [ "SummaryDetail"; "StandardDetail"; "DetailedDetail"; "AuditDetail" ]) ->
              InvalidRule(pack.PolicyPackId, $"'{d}' is not a report detail level")
          | RetentionDays(_, days) when days < 1 -> InvalidRule(pack.PolicyPackId, "retention must be at least one day")
          | _ -> () ]

/// The pack and its ancestors, nearest first.
let private chain (packs: Pack list) (id: string) (version: int) =
    let find i v = packs |> List.tryFind (fun p -> p.PolicyPackId = i && p.Version = v)

    let rec walk (seen: (string * int) list) (i, v) =
        if List.contains (i, v) seen then
            Error(ParentCycle(List.rev ((i, v) :: seen)))
        else
            match find i v with
            | None -> Error(UnknownPack(i, v))
            | Some pack ->
                match pack.Parent with
                | None -> Ok [ pack ]
                | Some parent -> walk ((i, v) :: seen) parent |> Result.map (fun rest -> pack :: rest)

    walk [] (id, version)

/// The explicit constraints a pack resolves to.
type Resolved =
    { Pack: string * int
      /// Every rule in force, by kind, with the pack that declared it.
      Rules: Map<string, Rule * (string * int)> }

/// Resolves a pack through its parents, or every reason it cannot be.
let resolve (packs: Pack list) (id: string) (version: int) : Result<Resolved, Problem list> =
    match chain packs id version with
    | Error problem -> Error [ problem ]
    | Ok packsInChain ->
        match packsInChain |> List.collect ruleProblems with
        | _ :: _ as problems -> Error problems
        | [] ->
            let declared =
                packsInChain
                |> List.collect (fun p -> p.Rules |> List.map (fun r -> kindOf r, (r, (p.PolicyPackId, p.Version))))
                |> List.groupBy fst

            let conflicts =
                declared
                |> List.choose (fun (kind, entries) ->
                    if entries |> List.map (snd >> fst) |> List.distinct |> List.length > 1 then
                        Some(ConflictingRules(kind, entries |> List.map (snd >> snd)))
                    else
                        None)

            if conflicts.IsEmpty then
                Ok
                    { Pack = (id, version)
                      Rules = declared |> List.map (fun (kind, entries) -> kind, snd (List.head entries)) |> Map.ofList }
            else
                Error conflicts

let private rule (resolved: Resolved) (kind: string) = resolved.Rules |> Map.tryFind kind |> Option.map fst

/// The disclosure policy a resolved pack sets, over the defaults it leaves alone.
let disclosure (resolved: Resolved) (defaults: Disclosure.Policy) : Disclosure.Policy =
    let number kind fallback =
        match rule resolved kind with
        | Some(MinimumGroup n | MinimumCell n | MinimumDistribution n | MinimumComparison n) -> n
        | _ -> fallback

    { MinimumGroup = number "minimum-group" defaults.MinimumGroup
      MinimumCell = number "minimum-cell" defaults.MinimumCell
      MinimumDistribution = number "minimum-distribution" defaults.MinimumDistribution
      MinimumComparison = number "minimum-comparison" defaults.MinimumComparison
      MinimumDiversity =
        match rule resolved "minimum-diversity" with
        | Some(MinimumDiversity n) -> Some n
        | _ -> defaults.MinimumDiversity }

/// Whether a chart type is allowed (every type when the pack names none).
let allowsChart (resolved: Resolved) (chart: string) =
    match rule resolved "allowed-chart-types" with
    | Some(AllowedChartTypes types) -> types.Contains chart
    | _ -> true

/// Whether finalization must leave a formal snapshot.
let requiresSnapshot (resolved: Resolved) =
    rule resolved "snapshot-at-finalization" = Some(SnapshotAtFinalization true)

/// Policy packs Signal ships (ADM-048's examples).
let builtIns =
    [ { PolicyPackId = "StrictAnonymous"
        Version = 1
        Parent = None
        Rules = [ MinimumGroup 7; MinimumCell 7; MinimumDistribution 14; MinimumComparison 7; MinimumDiversity 3; AuditRequired true ]
        MinimumSchema = 1 }
      { PolicyPackId = "ExecutiveReporting"
        Version = 1
        Parent = Some("StrictAnonymous", 1)
        Rules = [ ReportDetailDefault "SummaryDetail"; SnapshotAtFinalization true; AllowedChartTypes(set [ "Bar"; "DotPlot" ]) ]
        MinimumSchema = 1 } ]
