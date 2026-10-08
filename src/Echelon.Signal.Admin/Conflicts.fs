/// Group configuration changes and the conflict workspace (ADM-007, ADM-062).
///
/// A group's configuration changes only by a versioned change: a new
/// revision conditioned on the one last read. Changes that would alter how
/// already accepted results are interpreted (the template, the identity
/// mode, the privacy threshold) are refused once results exist: they need a
/// successor group, which never mutates the prior one.
///
/// When a change meets a configuration that moved underneath it, the
/// workspace shows the base, current and proposed versions, a field-level
/// semantic diff, which changes are independent (and so merge
/// deterministically) and which conflict, and the resolutions with the
/// obligations each leaves. A resolution produces a new version on top of
/// the current one; history is never rewritten.
///
/// Pure.
module Echelon.Signal.Admin.Conflicts

open Echelon.Signal.Engine.Import
open Echelon.Signal.Admin.GroupRecord

/// One configuration field, as the diff names it.
type Field =
    | ExpectedCount
    | MinimumReportableCount
    | Retention
    | Template
    | Mode

let fieldName =
    function
    | ExpectedCount -> "expectedCount"
    | MinimumReportableCount -> "minimumReportableCount"
    | Retention -> "retention"
    | Template -> "template"
    | Mode -> "mode"

/// Fields that change how accepted results are interpreted.
let interpreting = set [ Template; Mode; MinimumReportableCount ]

let private valueOf (config: GroupConfig) =
    function
    | ExpectedCount -> string config.ExpectedCount
    | MinimumReportableCount -> string config.MinimumReportableCount
    | Retention -> $"%A{config.Retention}"
    | Template -> config.TemplateHash
    | Mode -> $"%A{config.Mode}"

let private fields = [ ExpectedCount; MinimumReportableCount; Retention; Template; Mode ]

/// The fields that differ between two configurations.
let changed (before: GroupConfig) (after: GroupConfig) =
    fields |> List.filter (fun field -> valueOf before field <> valueOf after field)

/// Why a configuration change is refused.
type ChangeRefusal =
    /// The group no longer accepts configuration changes.
    | NotChangeable of status: string
    /// It would reinterpret accepted results: create a successor group.
    | RequiresSuccessorGroup of fields: string list
    /// The value is not valid.
    | InvalidValue of field: string

/// The next configuration version, or why not.
let change (status: GroupLifecycle.Status) (accepted: int) (current: GroupConfig) (proposed: GroupConfig) : Result<GroupConfig, ChangeRefusal> =
    let touched = changed current proposed

    if not (GroupLifecycle.acceptsConfiguration status) then
        Error(NotChangeable(GroupLifecycle.statusName status))
    elif proposed.ExpectedCount < 1 then
        Error(InvalidValue "expectedCount")
    elif proposed.MinimumReportableCount < 1 then
        Error(InvalidValue "minimumReportableCount")
    elif proposed.Group <> current.Group then
        Error(InvalidValue "group")
    else
        match touched |> List.filter interpreting.Contains with
        | reinterpreting when accepted > 0 && not reinterpreting.IsEmpty -> Error(RequiresSuccessorGroup(reinterpreting |> List.map fieldName))
        | _ -> Ok { proposed with Revision = current.Revision + 1 }

// ---- The conflict workspace -----------------------------------------------------------------

/// One field's three versions.
type FieldDifference =
    { Field: string
      Base: string
      Current: string
      Proposed: string
      /// Changed on both sides to different values.
      Conflicting: bool }

/// How a conflict may be resolved, with what it leaves to do.
type Resolution =
    /// Keep the current version; the proposal is dropped.
    | KeepCurrent
    /// Apply the proposal's independent changes on top of the current version.
    | MergeIndependent
    /// Apply the whole proposal on top of the current version, as a new version.
    | ApplyProposedOnCurrent
    /// Leave this group as it is and continue in a successor group.
    | CreateSuccessorGroup

/// A conflict between a proposed configuration and the current one.
[<NoComparison>]
type Conflict =
    { Group: string
      BaseRevision: int
      CurrentRevision: int
      Differences: FieldDifference list
      /// True when no field changed on both sides: the merge is deterministic.
      Mergeable: bool
      /// The resolutions open here, each with the obligations it leaves.
      Options: (Resolution * string list) list }

/// The workspace for a proposal made on `base'` that met `current`.
let analyze (accepted: int) (base': GroupConfig) (current: GroupConfig) (proposed: GroupConfig) : Conflict =
    let theirs = changed base' current |> Set.ofList
    let mine = changed base' proposed |> Set.ofList

    let differences =
        Set.union theirs mine
        |> Set.toList
        |> List.map (fun field ->
            { Field = fieldName field
              Base = valueOf base' field
              Current = valueOf current field
              Proposed = valueOf proposed field
              Conflicting = theirs.Contains field && mine.Contains field && valueOf current field <> valueOf proposed field })

    let mergeable = differences |> List.forall (fun d -> not d.Conflicting)
    let reinterpreting = Set.intersect mine interpreting |> Set.toList |> List.map fieldName

    { Group = groupKey current.Group
      BaseRevision = base'.Revision
      CurrentRevision = current.Revision
      Differences = differences
      Mergeable = mergeable
      Options =
        [ KeepCurrent, []
          if mergeable then MergeIndependent, []
          if accepted = 0 || reinterpreting.IsEmpty then ApplyProposedOnCurrent, [ "Review downstream reports for the changed fields" ]
          CreateSuccessorGroup,
          [ "Import into the successor group from now on"
            if not reinterpreting.IsEmpty then $"""The successor carries the new {String.concat ", " reinterpreting}""" ] ] }

/// The new version a resolution produces on top of the current one, or None
/// when the resolution keeps the current version or moves to a successor.
let resolve (resolution: Resolution) (base': GroupConfig) (current: GroupConfig) (proposed: GroupConfig) : GroupConfig option =
    match resolution with
    | KeepCurrent
    | CreateSuccessorGroup -> None
    | ApplyProposedOnCurrent -> Some { proposed with Revision = current.Revision + 1 }
    | MergeIndependent ->
        let mine = changed base' proposed |> Set.ofList

        let pick field (fromProposed: GroupConfig -> 'a) (fromCurrent: GroupConfig -> 'a) =
            if mine.Contains field then fromProposed proposed else fromCurrent current

        Some
            { current with
                ExpectedCount = pick ExpectedCount _.ExpectedCount _.ExpectedCount
                MinimumReportableCount = pick MinimumReportableCount _.MinimumReportableCount _.MinimumReportableCount
                Retention = pick Retention _.Retention _.Retention
                TemplateHash = pick Template _.TemplateHash _.TemplateHash
                TemplateVersion = pick Template _.TemplateVersion _.TemplateVersion
                SurveyIdentifier = pick Template _.SurveyIdentifier _.SurveyIdentifier
                Mode = pick Mode _.Mode _.Mode
                Revision = current.Revision + 1 }
