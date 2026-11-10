/// What the page binds for its operations (SIG-VIS-PENDING-001): for each
/// control that starts one, whether it is the one running, whether another
/// operation makes it unavailable, and its label; and the page's activity
/// and persistence regions. Every value comes from `Operations`; the page
/// decides nothing (ADM-035).
///
/// A running control keeps its focus and its place: it is marked
/// `aria-busy="true"` and `aria-disabled="true"` (Forma 1.0.1 draws its ring
/// for `.forma-button[aria-busy="true"]`) rather than disabled, and the
/// engine ignores another activation (`Operations.start`). The other
/// controls are disabled while it runs (the guard in `Operations`).
///
/// Pure.
module Echelon.Signal.Admin.ActivityView

open Echelon.Signal.Engine.View
open Echelon.Signal.Admin.AdminApp
open Echelon.Signal.Admin.Operations

let private ariaFlag (value: bool) = Text(if value then "true" else "false")

/// A control's bound values, named `{prefix}Busy`, `{prefix}Disabled` and
/// `{prefix}Label`: whether its operation runs (an ARIA "true"/"false",
/// for aria-busy and aria-disabled), whether another operation runs, and
/// its label, `pending` while it runs.
let fieldsWith (activity: Activity) (prefix: string) (operation: Operation) (label: string) (pending: string) : (string * Scalar) list =
    let mine = isRunningOp operation activity

    [ $"{prefix}Busy", ariaFlag mine
      // Another operation runs, or a change waits on a reconciliation.
      $"{prefix}Disabled", Flag(not mine && not (canStart operation activity))
      $"{prefix}Label", Text(if mine then pending else label) ]

/// The same, with the present participle of the control's own words while it runs.
let fields (activity: Activity) (prefix: string) (operation: Operation) (label: string) : (string * Scalar) list =
    fieldsWith activity prefix operation label (pendingLabel operation)

/// The same, as page-level values.
let control (activity: Activity) (prefix: string) (operation: Operation) (label: string) : (string * ViewValue) list =
    fields activity prefix operation label |> List.map (fun (name, value) -> name, Value value)

/// The words that say what is running, for the activity region.
let private readingText (model: Model) (operation: Operation) =
    match operation with
    | Operation.OpenDataset id ->
        let label =
            model.Deployment
            |> Option.bind (fun c -> c.Datasets |> List.tryFind (fun d -> d.Id = id))
            |> Option.map _.Label
            |> Option.defaultValue id

        $"Opening {label}…"
    | other -> $"{pendingLabel other}…"

/// The page's activity region (`#activity`, beside the notice, where the
/// person acts): what runs, with a spinner; the last change's outcome; an
/// owed reload. It is the page's one live region for operations: always in
/// the page, it announces each start and each settle once, the notice's
/// sentence included (the notice itself is not a second live region).
let project (model: Model) : View =
    let activity = model.Activity
    let signingIn = model.SignIn = Echelon.Signal.Admin.Credential.SigningIn

    let runningText =
        match activity.Running with
        | Some operation when kind operation = Write -> persistenceText activity
        | Some operation -> readingText model operation
        | None when signingIn -> "Signing in with GitHub…"
        | None -> ""

    let settledText = if isRunning activity then "" else persistenceText activity
    let reload = reloadText activity
    let notice = model.Notice |> Option.map _.Message |> Option.defaultValue ""

    [ // Some operation runs: the controls that start none but were unavailable
      // while the page was busy stay so (startDraft, deriveDraft, the site file…).
      "busy", Value(Flag(isRunning activity))
      "operationRunning", Value(Flag(isRunning activity || signingIn))
      "operationText", Value(Text runningText)
      // The persistence indicator: pending, saved, partial, failed or unknown.
      "persistenceState", Value(Text(persistenceState activity))
      "hasPersistence", Value(Flag(settledText <> ""))
      "persistenceText", Value(Text settledText)
      "reloadOwed", Value(Flag(reload <> ""))
      "reloadText", Value(Text reload)
      // The notice's sentence for assistive technology, inside the live
      // region; visually the notice shows it.
      "noticeAnnouncement", Value(Text(if isRunning activity then "" else notice)) ]
