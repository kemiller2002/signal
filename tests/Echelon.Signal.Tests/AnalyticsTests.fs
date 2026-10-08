/// Analytics, comparisons, lineage, change impact and dependency
/// invalidation (WI-0048): ADM-011 (distribution nodes), ADM-012, ADM-013,
/// ADM-014, ADM-019, ADM-020, ADM-032, ADM-041, ADM-046 (expression limits),
/// ADM-049, ADM-051, ADM-052, ADM-054, ARX-012.
module Echelon.Signal.Tests.AnalyticsTests

open System
open Xunit
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Analysis

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private pilot = Pilot.assessment
let private opaque (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed * 3 + i)))).Value
let private groupId = opaque 90

let private config mode minimum : GroupRecord.GroupConfig =
    { Group = groupId
      Mode = mode
      ExpectedCount = 10
      SurveyIdentifier = pilot.Id
      TemplateVersion = pilot.Version
      TemplateHash = Canonical.templateHash pilot
      MinimumReportableCount = minimum
      Retention = GroupRecord.NoneAfterImport
      Revision = 1 }

let private answersOf (values: int list) =
    pilot.Items |> List.mapi (fun i item -> item.Id, Rated(match values[i % values.Length] with 0 -> Never | 1 -> Rarely | 2 -> Sometimes | 3 -> Often | _ -> AlmostAlways)) |> Map.ofList

/// A group with one respondent per answer pattern.
let private accumulatorOf mode (patterns: int list list) =
    let definition = { Group = groupId; Mode = mode; ExpectedCount = 10; Template = pilot }

    let state =
        patterns
        |> List.mapi (fun i values ->
            let binding = if mode = AnonymousGroup then Anonymous(opaque i, groupId) else Identified(opaque i, groupId)
            "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = binding; Answers = answersOf values })
        |> List.fold (fun s t -> importOne s t |> fst) (empty definition)

    Incremental.ofResults definition (state.Results |> Map.toList |> List.map snd) |> ok

let private section = pilot.Dimensions.Head.Id
let private patterns = [ [ 0 ]; [ 1 ]; [ 2 ]; [ 3 ]; [ 4 ]; [ 2; 3 ] ]

// ---- ADM-012 / ADM-013: measures with prerequisites ---------------------------------------------

[<Fact>]
let ``descriptive measures come from the evidence and say how they were computed`` () =
    let src = source (config IdentifiedGroup 1) (accumulatorOf IdentifiedGroup patterns)
    let value m = match measure src (Some section) m with Value v -> v | other -> failwith $"%A{other}"

    Assert.Equal(6.0, (match measure src None AcceptedCount with Value v -> v | _ -> nan))
    Assert.Equal(0.6, (match measure src None CompletionRate with Value v -> v | _ -> nan))
    Assert.Equal(0.0, value Minimum)
    Assert.Equal(100.0, value Maximum)
    Assert.True(value StandardDeviation > 0.0)
    // Nearest-rank percentiles bracket the median.
    Assert.True(value (Percentile 25) <= value Median && value Median <= value (Percentile 75))
    Assert.Equal(Descriptive, (prerequisites Mean).Kind)
    Assert.Contains("Student", (prerequisites ConfidenceLower95).Method)
    Assert.Equal(Inferential, (prerequisites ConfidenceUpper95).Kind)
    Assert.True(value ConfidenceLower95 < value Mean && value Mean < value ConfidenceUpper95)

[<Fact>]
let ``a measure whose prerequisites fail is typed unavailable, never a number`` () =
    let small = source (config IdentifiedGroup 1) (accumulatorOf IdentifiedGroup [ [ 3 ] ])
    Assert.Equal(Unavailable(InsufficientSample(2, 1)), measure small (Some section) StandardDeviation)
    Assert.Equal(Unavailable(InsufficientSample(5, 1)), measure small (Some section) ConfidenceLower95)

    // A small anonymous group: counts yes, scores suppressed.
    let anonymous = source (config AnonymousGroup 5) (accumulatorOf AnonymousGroup [ [ 3 ]; [ 1 ] ])
    Assert.Equal(Unavailable(Suppressed(5, 2)), measure anonymous (Some section) Mean)
    Assert.Equal(Value 2.0, measure anonymous None AcceptedCount)

    // What results do not retain is never estimated.
    Assert.True(match measure small None DontKnowRate with Unavailable(NotRetained _) -> true | _ -> false)
    Assert.True(match measure small None CronbachAlpha with Unavailable(NotRetained _) -> true | _ -> false)
    Assert.Equal(Unavailable(UnknownSection "nope"), measure small (Some "nope") Mean)

[<Fact>]
let ``analysis never changes the canonical result`` () =
    let accumulator = accumulatorOf IdentifiedGroup patterns
    let before = sprintf "%A" (Incremental.result (GroupRecord.policy (config IdentifiedGroup 1)) accumulator)
    let src = source (config IdentifiedGroup 1) accumulator
    [ Mean; Median; StandardDeviation; Percentile 90 ] |> List.iter (measure src (Some section) >> ignore)
    Assert.Equal(before, sprintf "%A" (Incremental.result (GroupRecord.policy (config IdentifiedGroup 1)) accumulator))

[<Fact>]
let ``the distribution bins every scored respondent once, ceiling and floor are detected`` () =
    let src = source (config IdentifiedGroup 1) (accumulatorOf IdentifiedGroup patterns)
    let bins = distribution src section 4 |> ok
    Assert.Equal(4, bins.Length)
    Assert.Equal(src.Scores[section].Length, bins |> List.sumBy (fun (_, _, n) -> n))
    Assert.Equal(Some "ceiling", ceilingOrFloor src section)

// ---- AnalysisExpression -------------------------------------------------------------------------------

[<Fact>]
let ``calculated measures are bounded, deterministic and fail explicitly`` () =
    let src = source (config IdentifiedGroup 1) (accumulatorOf IdentifiedGroup patterns)
    let spread = Round(Sub(Ref(Maximum, Some section), Ref(Minimum, Some section)), 1)
    Assert.Equal(Value 100.0, evaluate src spread)
    Assert.Equal(Unavailable DivisionByZero, evaluate src (Div(Const 1.0, Const 0.0)))

    let rec deep n = if n = 0 then Const 1.0 else Add(deep (n - 1), Const 1.0)
    Assert.True(validate (deep 10) |> Result.isOk)
    Assert.True(validate (deep 40) |> Result.isError)

    // From JSON (a URL or a record), an expression is untrusted and held to the limits.
    let state = { AnalysisState.empty with Expressions = [ "spread", spread ] }
    let back = AnalysisState.ofJson (AnalysisState.toJson state) |> ok
    Assert.Equal(state, back)
    let tooDeep = AnalysisState.toJson { AnalysisState.empty with Expressions = [ "deep", deep 40 ] }
    Assert.True(AnalysisState.ofJson tooDeep |> Result.isError)

// ---- ADM-014: comparison ------------------------------------------------------------------------------

let private side (key: string) mode minimum hash (patterns: int list list) : Comparison.Side =
    { Group = key
      SurveyIdentifier = pilot.Id
      TemplateHash = hash
      Sections = pilot.Dimensions |> List.map _.Id
      Source = source (config mode minimum) (accumulatorOf mode patterns) }

[<Fact>]
let ``comparison rests on semantics, and refuses with reasons when they differ`` () =
    let hash = Canonical.templateHash pilot
    let a = side "a" IdentifiedGroup 1 hash patterns
    let b = side "b" IdentifiedGroup 1 hash [ [ 4 ]; [ 4 ]; [ 3 ] ]

    match Comparison.comparability [] a b with
    | Comparison.Comparable mapping -> Assert.Equal(pilot.Dimensions.Length, mapping.Count)
    | other -> failwith $"%A{other}"

    Assert.True(match Comparison.change [] a b section |> ok with Value delta -> delta > 0.0 | _ -> false)
    Assert.True(Comparison.effectSize [] a b section |> Result.isOk)

    let reasons x y = match Comparison.comparability [] x y with Comparison.NotComparable r -> r | _ -> []
    Assert.Equal<string list>([ "SURVEY_DIFFERS" ], reasons a { b with SurveyIdentifier = "OTHER" })
    Assert.Equal<string list>([ "IDENTITY_MODE_DIFFERS" ], reasons a (side "c" AnonymousGroup 1 hash patterns))
    Assert.Equal<string list>([ "PRIVACY_POLICY_DIFFERS" ], reasons a (side "d" IdentifiedGroup 3 hash patterns))
    Assert.Equal<string list>([ "TEMPLATE_DIFFERS_WITHOUT_DECLARATION" ], reasons a { b with TemplateHash = "sha256:next" })
    Assert.True(Comparison.change [] a { b with SurveyIdentifier = "OTHER" } section |> Result.isError)

[<Fact>]
let ``a versioned continuity declaration lets a later template compare, if it is valid`` () =
    let hash = Canonical.templateHash pilot
    let a = side "a" IdentifiedGroup 1 hash patterns
    let b = { side "b" IdentifiedGroup 1 hash patterns with TemplateHash = "sha256:next" }
    let declaration: Comparison.ContinuityDeclaration = { Version = 1; FromTemplate = hash; ToTemplate = "sha256:next"; Metrics = [ section, section ] }

    match Comparison.comparability [ declaration ] a b with
    | Comparison.Comparable mapping -> Assert.Equal<string list>([ section ], mapping |> Map.keys |> List.ofSeq)
    | other -> failwith $"%A{other}"

    let invalid = { declaration with Metrics = [ "MISSING", section ] }
    Assert.Equal<string list>([ "DECLARATION_UNKNOWN_SOURCE_METRIC:MISSING" ], match Comparison.comparability [ invalid ] a b with Comparison.NotComparable r -> r | _ -> [])

// ---- ADM-020 / ARX-012: lineage ---------------------------------------------------------------------

[<Fact>]
let ``a value traces back through the path that computed it, without identities`` () =
    let accumulator = accumulatorOf IdentifiedGroup patterns
    let trace = Comparison.trace (config IdentifiedGroup 1) accumulator section Mean None
    let result = Incremental.result (GroupRecord.policy (config IdentifiedGroup 1)) accumulator

    Assert.Equal(result.Lineage.DerivationHash, trace.DerivationHash)
    Assert.Equal<string list>(result.Lineage.SubmissionHashes, trace.Contributions)
    Assert.Equal(Template.EngineVersion, trace.ScoringEngineVersion)
    Assert.All(trace.Contributions, fun c -> Assert.StartsWith("sha256:", c))
    Assert.DoesNotContain("instance:", sprintf "%A" trace)

    let small = Comparison.trace (config AnonymousGroup 5) (accumulatorOf AnonymousGroup [ [ 1 ] ]) section Mean None
    Assert.Contains("SUPPRESSED_BELOW_MINIMUM", small.ReasonCodes)

// ---- ADM-054 / ADM-041: dependencies and impact -------------------------------------------------------

let private graph =
    Dependencies.groupGraph "g" [ "s1"; "s2" ] [ "Mean"; "Median" ] [ "summary", [ "g/measure/s1/Mean" ]; "privacy", [ "g/privacy" ] ] "sha256:x"

[<Fact>]
let ``a change invalidates exactly its downstream closure`` () =
    let closure = Dependencies.closure graph [ "g/aggregate/s1" ] |> Set.ofList
    Assert.Equal<Set<string>>(set [ "g/measure/s1/Mean"; "g/measure/s1/Median"; "report/summary" ], closure)

    // A presentation change touches only its block.
    let presentation = Dependencies.preview graph (Dependencies.ReportPresentationChanged "summary")
    Assert.Equal<string list>([ "report/summary" ], presentation |> List.map _.Node)

    // A new contribution rebuilds aggregates and the index and reprojects
    // what reads them; the accepted contributions themselves are never invalidated.
    let added = Dependencies.preview graph (Dependencies.ContributionAdded "g")
    Assert.DoesNotContain(added, fun p -> p.Node = "g/contributions")
    Assert.Contains(added, fun p -> p.Node = "g/aggregate/s2" && p.Impact = Dependencies.RequiresAggregateRebuild)
    Assert.Contains(added, fun p -> p.Node = "g/index" && p.Impact = Dependencies.RequiresIndexRebuild)
    Assert.Contains(added, fun p -> p.Node = "report/summary" && p.Impact = Dependencies.RequiresReprojection)
    Assert.Equal<AdminState.Obligation list>([ AdminState.RebuildDerivedProjection ], Dependencies.obligations added)

    // A privacy change reprojects without re-scoring.
    let privacy = Dependencies.preview graph (Dependencies.PrivacyThresholdChanged "g")
    Assert.DoesNotContain(privacy, fun p -> p.Node.Contains "/aggregate/")
    Assert.Contains(privacy, fun p -> p.Node = "g/privacy" && p.Impact = Dependencies.BecomesSuppressed)

    // A successor's new template leaves history valid, only not comparable.
    let adopted = Dependencies.preview graph (Dependencies.TemplateAdoptedForSuccessor "g")
    Assert.All(adopted, fun p -> Assert.Equal(Dependencies.BecomesNotComparable, p.Impact))

[<Fact>]
let ``recomputing only the invalidated closure equals a full rebuild`` () =
    let before = accumulatorOf IdentifiedGroup patterns
    let definition = before.Definition

    let extra =
        "https://signal.example" + LiveUrl.urlFor pilot "/web/" "" { Binding = Identified(opaque 77, groupId); Answers = answersOf [ 4; 0 ] }

    let result = match evaluateAgainst definition (Incremental.acceptedFor before) extra with Accepted r -> r | other -> failwith $"%A{other}"
    let after = Incremental.add result before |> ok
    let sections = pilot.Dimensions |> List.map _.Id
    let measures = [ Mean; Median; StandardDeviation ]
    let all (acc: Incremental.Accumulator) = [ for s in sections do for m in measures -> (s, measureName m), measure (source (config IdentifiedGroup 1) acc) (Some s) m ] |> Map.ofList

    let full = all after
    let g = Dependencies.groupGraph "g" sections (measures |> List.map measureName) [] "x"
    let invalidated = Dependencies.closure g [ "g/contributions" ] |> List.filter (fun id -> id.StartsWith "g/measure/") |> Set.ofList

    let incremental =
        all before
        |> Map.map (fun (s, m) value -> if invalidated.Contains $"g/measure/{s}/{m}" then measure (source (config IdentifiedGroup 1) after) (Some s) (measures |> List.find (fun x -> measureName x = m)) else value)

    Assert.Equal<Map<string * string, Metric>>(full, incremental)

// ---- ADM-051 / ADM-049: diffs and upgrades --------------------------------------------------------------

[<Fact>]
let ``diffs are classified by meaning`` () =
    let c = config IdentifiedGroup 5
    let classified = Dependencies.classifyConfiguration c { c with MinimumReportableCount = 3; ExpectedCount = 12 } |> Map.ofList
    Assert.Contains(Dependencies.PrivacyRelevant, classified["minimumReportableCount"])
    Assert.Contains(Dependencies.Breaking, classified["minimumReportableCount"])
    Assert.Contains(Dependencies.NonBreaking, classified["expectedCount"])

    let manifest = Dependencies.classifyManifest DatasetManifest.current { DatasetManifest.current with StorageLayout = 2 } |> Map.ofList
    Assert.Contains(Dependencies.Breaking, manifest["storageLayout"])

[<Fact>]
let ``a template upgrade explains what continues and what a saved analysis loses`` () =
    let removed = pilot.Dimensions.Head.Id
    let next = { pilot with Version = "0.2.0"; Dimensions = pilot.Dimensions.Tail; Items = pilot.Items |> List.filter (fun i -> i.DimensionId <> removed) }
    let upgrade = Dependencies.upgrade pilot next [ "first-section", [ removed ]; "others", [ next.Dimensions.Head.Id ] ]

    Assert.Equal<string list>([ removed ], upgrade.Removed)
    Assert.Empty(upgrade.Changed)
    Assert.Equal<string list>(next.Dimensions |> List.map _.Id, upgrade.Continuous)
    Assert.Equal<string list>([ "first-section" ], upgrade.InvalidatedAnalyses)

// ---- ADM-019 / ADM-032 / ADM-052 --------------------------------------------------------------------------

[<Fact>]
let ``analysis state travels in the URL with integrity, and saved analyses are records`` () =
    let state = { AnalysisState.empty with Groups = [ "g1" ]; Sections = [ section ]; Measures = [ Mean; Percentile 90 ]; Display = AnalysisState.Percentages }
    let fragment = AnalysisState.fragment state
    Assert.Equal(state, AnalysisState.ofFragment fragment |> ok)
    Assert.DoesNotContain("token", fragment)
    Assert.True(AnalysisState.ofFragment (fragment.Replace(".", "X.")) |> Result.isError)

    let saved: AnalysisState.Saved = { Id = "an-1"; Name = "Weakest sections"; State = state; Revision = 1 }
    let _, text = AnalysisState.encodeSaved "ds_engagement" saved |> ok
    let record = Record.decode Record.DefaultMaxBytes text |> ok
    Assert.Equal(("ds_engagement", saved), AnalysisState.savedOfBody record.Body |> ok)

[<Fact>]
let ``every roster change can be undone by a new change`` () =
    let me = { Access.PrincipalId = "github:1"; Access.Kind = Access.Human; Access.DisplayName = "one" }
    let them = { Access.PrincipalId = "github:2"; Access.Kind = Access.Human; Access.DisplayName = "two" }
    let roster = Access.founded "ds" me |> Access.execute me.PrincipalId (Access.Admit(them, Access.Grants.analyst)) |> ok

    for command in [ Access.Grant(them.PrincipalId, Access.ImportSubmissions); Access.Revoke(them.PrincipalId, Access.ViewResults); Access.Remove them.PrincipalId ] do
        let changed = Access.execute me.PrincipalId command roster |> ok
        let undo = AnalysisState.inverse roster command |> Option.get
        let restored = Access.execute me.PrincipalId undo changed |> ok
        Assert.Equal<Set<Access.Capability>>(Access.capabilitiesOf roster them.PrincipalId, Access.capabilitiesOf restored them.PrincipalId)
