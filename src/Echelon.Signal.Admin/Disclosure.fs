/// Disclosure controls for anonymous groups (WI-0051, ADM-024, ARX-009):
/// what may be released about small groups, small slices, distributions and
/// comparisons, decided before any ReportData reaches a visual component, so
/// the page never receives a hidden value to hide.
///
/// - **Cells** below the minimum are suppressed, and a breakdown whose total
///   is shown suppresses a second cell (or the total) when subtraction would
///   reveal a single hidden one.
/// - **Filters** that leave an anonymous group below its threshold suppress
///   the view.
/// - **Differencing across views** (ADM-024, ARX-009): a release ledger
///   records every count released for a group. A new release whose
///   population is nested in, or contains, an earlier one, and differs from
///   it by fewer than the minimum, is withheld: the two would reveal the
///   small difference. Repeated snapshots as submissions arrive are the same
///   view at a later time, so the view freezes until enough new responses
///   arrive.
///
/// Identified groups are not anonymous; the controls apply to anonymous
/// groups only (ID-003). Pure.
module Echelon.Signal.Admin.Disclosure

open Echelon.Signal.Engine.Import

/// The configured thresholds (ADM-024 §"At minimum support").
type Policy =
    { MinimumGroup: int
      MinimumCell: int
      MinimumDistribution: int
      MinimumComparison: int
      /// Distinct values a distribution needs before it is shown, if set.
      MinimumDiversity: int option }

let defaultPolicy =
    { MinimumGroup = 5
      MinimumCell = 5
      MinimumDistribution = 10
      MinimumComparison = 5
      MinimumDiversity = Some 3 }

/// The policy a group's own minimum implies: cells, comparisons and the
/// differencing rule use it; distributions need twice as many values and,
/// above a minimum of one, a few distinct ones.
let forGroup (minimum: int) =
    { MinimumGroup = minimum
      MinimumCell = minimum
      MinimumDistribution = 2 * minimum
      MinimumComparison = minimum
      MinimumDiversity = if minimum > 1 then Some(min 3 minimum) else None }

/// Why a value is withheld.
type Reason =
    | BelowGroupMinimum of have: int * need: int
    | BelowCellMinimum of have: int * need: int
    /// Shown, it would let subtraction reveal a hidden cell.
    | ComplementaryCell
    | BelowDistributionMinimum of have: int * need: int
    | TooFewDistinctValues of have: int * need: int
    | BelowComparisonMinimum of have: int * need: int
    /// It and an earlier release differ by fewer than the minimum.
    | Differencing of earlier: string * difference: int

type Decision<'a> =
    | Release of 'a
    | Withhold of Reason

let private anonymous mode = mode = AnonymousGroup

/// The group as a whole.
let group (policy: Policy) (mode: IdentityMode) (accepted: int) =
    if anonymous mode && accepted < policy.MinimumGroup then Withhold(BelowGroupMinimum(accepted, policy.MinimumGroup))
    else Release accepted

/// A filtered view of an anonymous group (ADM-024: a filter that reduces it
/// below the threshold becomes suppressed).
let filtered (policy: Policy) (mode: IdentityMode) (count: int) =
    if anonymous mode && count < max policy.MinimumGroup policy.MinimumCell then Withhold(BelowCellMinimum(count, max policy.MinimumGroup policy.MinimumCell))
    else Release count

/// A breakdown into cells with its total: small cells are suppressed, and
/// when one hidden cell could be recovered by subtraction from the total, the
/// next smallest cell is suppressed too, or the total when there is none.
let breakdown (policy: Policy) (mode: IdentityMode) (cells: (string * int) list) : (string * Decision<int>) list * Decision<int> =
    let total = cells |> List.sumBy snd

    if not (anonymous mode) then
        cells |> List.map (fun (k, n) -> k, Release n), Release total
    else
        let small = cells |> List.filter (fun (_, n) -> n < policy.MinimumCell) |> List.map fst |> Set.ofList

        let complement =
            if small.Count = 1 then
                cells |> List.filter (fun (k, _) -> not (small.Contains k)) |> List.sortBy (fun (k, n) -> n, k) |> List.tryHead |> Option.map fst
            else
                None

        let decide (k, n) =
            if small.Contains k then k, Withhold(BelowCellMinimum(n, policy.MinimumCell))
            elif complement = Some k then k, Withhold ComplementaryCell
            else k, Release n

        let totalDecision =
            if total < policy.MinimumGroup then Withhold(BelowGroupMinimum(total, policy.MinimumGroup))
            elif small.Count = 1 && complement.IsNone then Withhold ComplementaryCell
            else Release total

        cells |> List.map decide, totalDecision

/// A distribution: enough values, and (if configured) enough distinct ones.
let distribution (policy: Policy) (mode: IdentityMode) (values: float list) =
    let distinct = values |> List.distinct |> List.length

    if anonymous mode && values.Length < policy.MinimumDistribution then
        Withhold(BelowDistributionMinimum(values.Length, policy.MinimumDistribution))
    else
        match policy.MinimumDiversity with
        | Some need when anonymous mode && distinct < need -> Withhold(TooFewDistinctValues(distinct, need))
        | _ -> Release values

/// A comparison between two populations: both sides must be large enough.
let comparison (policy: Policy) (mode: IdentityMode) (current: int) (baseline: int) =
    match [ current; baseline ] |> List.tryFind (fun n -> n < policy.MinimumComparison) with
    | Some small when anonymous mode -> Withhold(BelowComparisonMinimum(small, policy.MinimumComparison))
    | _ -> Release(current, baseline)

// ---- Differencing across releases ---------------------------------------------------------------

/// A released view: the filters that define its population (empty: the
/// whole group) and how many responses it covered.
type View =
    { Id: string
      Filters: Set<string * string>
      Count: int }

/// The views released for one group, in release order.
type Ledger = View list

let emptyLedger: Ledger = []

/// Whether one view's population contains the other's: more filters, a
/// smaller population; the same filters at a later time, a population that
/// grew (submissions only arrive).
let private nested (a: View) (b: View) =
    Set.isSubset a.Filters b.Filters || Set.isSubset b.Filters a.Filters

/// Whether a view may be released given the ledger; a released view joins it.
let release (policy: Policy) (mode: IdentityMode) (ledger: Ledger) (view: View) : Decision<View> * Ledger =
    match filtered policy mode view.Count with
    | Withhold reason -> Withhold reason, ledger
    | Release _ when not (anonymous mode) -> Release view, ledger @ [ view ]
    | Release _ ->
        let minimum = max policy.MinimumGroup policy.MinimumCell

        let risky =
            ledger
            |> List.tryFind (fun earlier ->
                let difference = abs (earlier.Count - view.Count)
                nested earlier view && difference > 0 && difference < minimum)

        match risky with
        | Some earlier -> Withhold(Differencing(earlier.Id, abs (earlier.Count - view.Count))), ledger
        | None -> Release view, ledger @ [ view ]

/// Whether a group's live state may be released, given the count last
/// released (ARX-009: repeated views as submissions arrive). A minimum of one
/// or an identified group never needs the ledger.
let releasable (policy: Policy) (mode: IdentityMode) (lastReleased: int option) (live: int) =
    let earlier = lastReleased |> Option.map (fun n -> { Id = "released"; Filters = Set.empty; Count = n }) |> Option.toList
    fst (release policy mode earlier { Id = "live"; Filters = Set.empty; Count = live })
