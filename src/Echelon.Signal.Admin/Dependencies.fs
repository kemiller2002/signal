/// The derived-state dependency graph, change impact, semantic diffs and
/// template upgrades (ADM-054, ADM-041, ADM-051, ADM-049).
///
/// Every derived artifact is a node with its inputs, version, source hash
/// and whether it can be rebuilt; a change invalidates exactly the
/// downstream closure of the nodes it touches, never more and never less.
/// An impact preview classifies every affected artifact with a stable
/// reason code and the obligations applying the change creates; historical
/// immutable artifacts (accepted results, snapshots) are never invalidated,
/// only marked not comparable or stale where that is the truth.
///
/// Pure.
module Echelon.Signal.Admin.Dependencies

open Echelon.Signal.Engine
open Echelon.Signal.Admin.GroupRecord

/// What a node is.
type NodeKind =
    | Contributions
    | Aggregate
    | AnalysisMeasure
    | Privacy
    | ReportBlock
    | Visualization
    | Snapshot
    | Index

/// One derived (or authoritative) node.
type Node =
    { Id: string
      Kind: NodeKind
      Version: int
      Inputs: string list
      /// The hash of the inputs it was built from.
      SourceHash: string
      Rebuildable: bool
      /// Immutable history: never invalidated, only judged.
      Immutable: bool }

/// The graph: nodes by id.
type Graph = Map<string, Node>

let ofNodes (nodes: Node list) : Graph = nodes |> List.map (fun n -> n.Id, n) |> Map.ofList

/// Nodes that read `id` directly.
let dependents (graph: Graph) (id: string) =
    graph |> Map.toList |> List.filter (fun (_, n) -> List.contains id n.Inputs) |> List.map fst

/// The downstream closure of the touched nodes, each once, in breadth order.
let closure (graph: Graph) (touched: string list) =
    let rec walk (queue: string list) (seen: Set<string>) (order: string list) =
        match queue with
        | [] -> List.rev order
        | id :: rest ->
            let next = dependents graph id |> List.filter (seen.Contains >> not)
            walk (rest @ next) (Set.union seen (Set.ofList next)) (List.rev next @ order)

    walk touched (Set.ofList touched) []

/// The nodes for one group and the report blocks that read it: contributions
/// feed one aggregate per section, which feeds the section's measures; the
/// privacy decision reads the contribution count; report blocks read
/// measures and the privacy decision; the contribution index reads the
/// contributions.
let groupGraph (group: string) (sections: string list) (measures: string list) (blocks: (string * string list) list) (sourceHash: string) : Graph =
    let node id kind inputs rebuildable =
        { Id = id
          Kind = kind
          Version = 1
          Inputs = inputs
          SourceHash = sourceHash
          Rebuildable = rebuildable
          Immutable = false }

    ofNodes (
        [ { node $"{group}/contributions" Contributions [] false with Immutable = true }
          node $"{group}/privacy" Privacy [ $"{group}/contributions" ] true
          node $"{group}/index" Index [ $"{group}/contributions" ] true ]
        @ [ for s in sections -> node $"{group}/aggregate/{s}" Aggregate [ $"{group}/contributions" ] true ]
        @ [ for s in sections do
                for m in measures -> node $"{group}/measure/{s}/{m}" AnalysisMeasure [ $"{group}/aggregate/{s}"; $"{group}/privacy" ] true ]
        @ [ for block, reads in blocks -> node $"report/{block}" ReportBlock reads true ]
    )

// ---- Change impact (ADM-041) -----------------------------------------------------------------

/// A change an administrator proposes.
type Change =
    | ContributionAdded of group: string
    | PrivacyThresholdChanged of group: string
    | ReportPresentationChanged of block: string
    | AnalysisExpressionChanged of measureNode: string
    | IndexSchemaChanged of group: string
    | StorageProviderChanged
    | TemplateAdoptedForSuccessor of group: string

/// How a change affects one artifact.
type Impact =
    | Unaffected
    | RequiresReprojection
    | RequiresAggregateRebuild
    | RequiresIndexRebuild
    | RequiresRevalidation
    | BecomesNotComparable
    | BecomesSuppressed
    | BecomesStale
    | RequiresMigration
    | Blocked
    | UnknownImpact

let impactName (impact: Impact) = $"%A{impact}"

/// The nodes a change touches first, and the impact on its closure.
let private touched (graph: Graph) (change: Change) =
    match change with
    | ContributionAdded group -> [ $"{group}/contributions" ], RequiresAggregateRebuild, "CONTRIBUTION_ADDED"
    | PrivacyThresholdChanged group -> [ $"{group}/privacy" ], RequiresReprojection, "PRIVACY_THRESHOLD_CHANGED"
    | ReportPresentationChanged block -> [ $"report/{block}" ], RequiresReprojection, "PRESENTATION_CHANGED"
    | AnalysisExpressionChanged node -> [ node ], RequiresRevalidation, "ANALYSIS_EXPRESSION_CHANGED"
    | IndexSchemaChanged group -> [ $"{group}/index" ], RequiresIndexRebuild, "INDEX_SCHEMA_CHANGED"
    | StorageProviderChanged -> graph |> Map.toList |> List.map fst, RequiresMigration, "STORAGE_PROVIDER_CHANGED"
    | TemplateAdoptedForSuccessor _ -> [], BecomesNotComparable, "TEMPLATE_ADOPTED"

/// One artifact's predicted impact, with its reason and evidence.
type Prediction =
    { Node: string
      Impact: Impact
      Reason: string
      /// The path from the change to this node.
      Evidence: string list }

/// The impact preview: deterministic from the graph, the change and these
/// rules. Immutable history is never invalidated; a storage change is a
/// migration; an index is rebuilt, a projection reprojected.
let preview (graph: Graph) (change: Change) : Prediction list =
    let first, impact, reason = touched graph change
    let affected = (first @ closure graph first) |> List.distinct

    let predictionFor (id: string) =
        match graph.TryFind id with
        | None -> { Node = id; Impact = UnknownImpact; Reason = reason; Evidence = [ id ] }
        | Some node ->
            let specific =
                match node.Kind, impact with
                | _, RequiresMigration -> RequiresMigration
                | Contributions, _
                | Snapshot, _ when node.Immutable -> Unaffected
                | Index, _ -> RequiresIndexRebuild
                | Aggregate, RequiresAggregateRebuild -> RequiresAggregateRebuild
                | Privacy, _ when (match change with PrivacyThresholdChanged _ -> true | _ -> false) -> BecomesSuppressed
                | (AnalysisMeasure | ReportBlock | Visualization), RequiresAggregateRebuild -> RequiresReprojection
                | _, other -> other

            { Node = id
              Impact = specific
              Reason = reason
              Evidence = node.Inputs @ [ id ] }

    match change with
    | TemplateAdoptedForSuccessor group ->
        // History stays valid; only comparison eligibility changes.
        graph
        |> Map.toList
        |> List.filter (fun (id, n) -> id.StartsWith(group + "/") && n.Kind = AnalysisMeasure)
        |> List.map (fun (id, _) -> { Node = id; Impact = BecomesNotComparable; Reason = reason; Evidence = [ id ] })
    | _ -> affected |> List.map predictionFor |> List.filter (fun p -> p.Impact <> Unaffected)

/// The obligations applying the change creates.
let obligations (predictions: Prediction list) =
    predictions
    |> List.choose (fun p ->
        match p.Impact with
        | RequiresIndexRebuild
        | RequiresAggregateRebuild -> Some AdminState.RebuildDerivedProjection
        | RequiresMigration -> Some(AdminState.ResolveUnsupportedStoreCapability "migration to the new provider")
        | _ -> None)
    |> List.distinct

// ---- Semantic diffs (ADM-051) ------------------------------------------------------------------

/// How a change matters.
type Classification =
    | PresentationOnly
    | PrivacyRelevant
    | ComparisonRelevant
    | AggregateRelevant
    | StorageRelevant
    | Breaking
    | NonBreaking
    | RequiresRebuild
    | RequiresRevalidation

/// A group configuration diff, classified by meaning.
let classifyConfiguration (before: GroupConfig) (after: GroupConfig) : (string * Classification list) list =
    Conflicts.changed before after
    |> List.map (fun field ->
        Conflicts.fieldName field,
        match field with
        | Conflicts.ExpectedCount -> [ AggregateRelevant; NonBreaking; RequiresRevalidation ]
        | Conflicts.MinimumReportableCount -> [ PrivacyRelevant; ComparisonRelevant; Breaking; RequiresRebuild ]
        | Conflicts.Retention -> [ StorageRelevant; PrivacyRelevant; NonBreaking ]
        | Conflicts.Template -> [ ComparisonRelevant; AggregateRelevant; Breaking; RequiresRebuild ]
        | Conflicts.Mode -> [ ComparisonRelevant; PrivacyRelevant; Breaking ])

/// A storage manifest diff, classified: a newer format is breaking for older readers.
let classifyManifest (before: DatasetManifest.FormatVersions) (after: DatasetManifest.FormatVersions) =
    [ if before.StorageLayout <> after.StorageLayout then "storageLayout", [ StorageRelevant; Breaking; RequiresRebuild ]
      if before.ResultSchema <> after.ResultSchema || before.GroupResultSchema <> after.GroupResultSchema then
          "resultSchema", [ AggregateRelevant; Breaking; RequiresRebuild ]
      if before.SupportedEncodings <> after.SupportedEncodings then "supportedEncodings", [ StorageRelevant; NonBreaking; RequiresRevalidation ]
      if before.ReportDefinitionSchema <> after.ReportDefinitionSchema || before.VisualizationSchema <> after.VisualizationSchema then
          "reportSchema", [ PresentationOnly; RequiresRevalidation ] ]

// ---- Template upgrades (ADM-049) --------------------------------------------------------------

/// What adopting a newer template for a successor group means.
type Upgrade =
    { Added: string list
      Removed: string list
      /// Sections whose items or scoring changed: no semantic continuity.
      Changed: string list
      /// Sections identical in items and scoring: comparable when declared.
      Continuous: string list
      ScoringChanged: bool
      /// Saved analyses (by name) that read a removed or changed section.
      InvalidatedAnalyses: string list }

/// The upgrade from one template to another; neither old groups nor their
/// results change (the successor gets the new template).
let upgrade (before: Assessment.Assessment) (after: Assessment.Assessment) (analyses: (string * string list) list) : Upgrade =
    let ids (a: Assessment.Assessment) = a.Dimensions |> List.map _.Id
    let itemsOf (a: Assessment.Assessment) id = a.Items |> List.filter (fun i -> i.DimensionId = id) |> List.map (fun i -> i.Id, i.Prompt)
    let shared = ids before |> List.filter (fun id -> List.contains id (ids after))
    let scoringChanged = before.MinimumNumericAnswers <> after.MinimumNumericAnswers
    let changed = shared |> List.filter (fun id -> scoringChanged || itemsOf before id <> itemsOf after id)
    let removed = ids before |> List.filter (fun id -> not (List.contains id (ids after)))

    { Added = ids after |> List.filter (fun id -> not (List.contains id (ids before)))
      Removed = removed
      Changed = changed
      Continuous = shared |> List.filter (fun id -> not (List.contains id changed))
      ScoringChanged = scoringChanged
      InvalidatedAnalyses =
        analyses
        |> List.filter (fun (_, sections) -> sections |> List.exists (fun s -> List.contains s removed || List.contains s changed))
        |> List.map fst }
