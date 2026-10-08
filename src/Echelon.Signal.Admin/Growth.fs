/// Repository growth (ADM-058): warnings before a dataset's size makes
/// ordinary GitHub-backed operations unreliable, from a measurement Arca
/// takes (`GitHubStorage.measure`, a `GrowthMeasure`).
///
/// Thresholds are policy, not constants buried in code: the defaults cite
/// GitHub's published guidance and limits, and a deployment may set its own
/// from its own evidence. A measurement the provider cut short is said to
/// be incomplete; nothing is judged from it as if it were whole.
///
/// Pure.
module Echelon.Signal.Admin.Growth

open Arca

/// What is measured.
type Metric =
    | StoredBytes
    | ObjectCount
    | FolderCount

let metricName =
    function
    | StoredBytes -> "stored bytes"
    | ObjectCount -> "objects"
    | FolderCount -> "folders"

/// When to warn, and when operations are expected to become unreliable.
type Threshold = { Warn: int64; Critical: int64 }

/// A deployment's growth policy.
type GrowthPolicy =
    { Thresholds: Map<Metric, Threshold>
      /// Where each default comes from, for the person reading the warning.
      Evidence: string }

/// Defaults from GitHub's guidance: repositories should stay under 1 GB and
/// well under 5 GB; the Git tree API truncates beyond 100,000 entries; the
/// contents API lists at most 1,000 entries per directory, which Arca's
/// record partitioning keeps folders under.
let defaultPolicy =
    { Thresholds =
        Map.ofList
            [ StoredBytes, { Warn = 500_000_000L; Critical = 1_000_000_000L }
              ObjectCount, { Warn = 50_000L; Critical = 100_000L }
              FolderCount, { Warn = 5_000L; Critical = 20_000L } ]
      Evidence = "GitHub repository size guidance (under 1 GB) and tree API truncation at 100,000 entries" }

/// One finding.
type GrowthWarning =
    /// The metric passed the warning threshold.
    | Approaching of metric: Metric * value: int64 * threshold: int64
    /// The metric passed the critical threshold: plan a rollover or compaction (a migration).
    | Exceeded of metric: Metric * value: int64 * threshold: int64
    /// The measurement is incomplete; the values are lower bounds.
    | Unmeasured of reason: string

/// The warnings a measurement gives under a policy, in metric order.
let assess (policy: GrowthPolicy) (measure: GrowthMeasure) : GrowthWarning list =
    let value =
        function
        | StoredBytes -> measure.Bytes
        | ObjectCount -> int64 measure.Objects
        | FolderCount -> int64 measure.Folders

    let judged =
        [ StoredBytes; ObjectCount; FolderCount ]
        |> List.choose (fun metric ->
            policy.Thresholds.TryFind metric
            |> Option.bind (fun threshold ->
                let found = value metric

                if found >= threshold.Critical then Some(Exceeded(metric, found, threshold.Critical))
                elif found >= threshold.Warn then Some(Approaching(metric, found, threshold.Warn))
                else None))

    if measure.Complete then
        judged
    else
        Unmeasured "the provider listed only part of the dataset, or the walk reached its budget; the values are lower bounds"
        :: judged

/// The warning as one sentence.
let describe =
    function
    | Approaching(metric, value, threshold) -> $"The dataset holds {value} {metricName metric}, past the {threshold} warning level."
    | Exceeded(metric, value, threshold) ->
        $"The dataset holds {value} {metricName metric}, past the {threshold} level where GitHub-backed operations become unreliable; plan a rollover."
    | Unmeasured reason -> $"The dataset's size is not fully known: {reason}."
