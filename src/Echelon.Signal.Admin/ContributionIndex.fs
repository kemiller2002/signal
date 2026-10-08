/// The accepted-contribution index (ADM-027): a derived, disposable view of
/// every accepted contribution in a dataset, keyed by group and identity,
/// built with Arca's derived-index support. It names the exact source set it
/// was built from, so a stale or corrupt index is detected and rebuilt; it
/// is never read as a source of results, so it cannot corrupt them.
/// Rebuilding from the same records gives the same index.
///
/// - ValidateIndex: `Derived.check` against the current source set.
/// - RebuildIndex and ActivateRebuiltIndex: `Derived.rebuild`, one commit
///   conditioned on the records being exactly as read.
/// - CompareIndexToReference: `Derived.compare` with a fresh build.
///
/// Pure.
module Echelon.Signal.Admin.ContributionIndex

open Arca
open Echelon.Signal.Admin.Codec

/// The index's definition, version 1.
let definition: IndexDefinition =
    { Name =
        match Segment.create "accepted-contributions" with
        | Ok found -> found
        | Error error -> invalidOp ("internal: " + LocationError.describe error)
      Version = 1
      Sources = [ ResultRecord.schema ]
      Project =
        fun record ->
            match text "group" record.Body, text "identity" record.Body, text "submissionHash" record.Body with
            | Ok group, Ok identity, Ok submission ->
                [ $"{group}/{identity}", Json.objectOf [ "submissionHash", Json.String submission ] ]
            | _ -> [] }
