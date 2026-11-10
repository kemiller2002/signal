/// The tone of a state chip (SIG-VIS-PENDING-001, the Echelon application
/// layer's `.echelon-chip[data-echelon-tone]`): a group's phase and a
/// template version's lifecycle, mapped from the authoritative model and
/// never re-derived. The tone is presentation only; the chip always carries
/// its text label, and the theme gives every tone its own marker shape, so
/// the meaning survives the loss of hue (Visual Engineering, semantic channel
/// survivability).
///
/// Pure.
module Echelon.Signal.Admin.StateTones

/// The tones the Echelon theme 1.1.0 contract defines.
[<RequireQualifiedAccess>]
type Tone =
    | Draft
    | Review
    | Published
    | Ready
    | Progress
    | Complete
    | Sealed
    | Closed

/// The value of `data-echelon-tone`.
let toneId (tone: Tone) =
    match tone with
    | Tone.Draft -> "draft"
    | Tone.Review -> "review"
    | Tone.Published -> "published"
    | Tone.Ready -> "ready"
    | Tone.Progress -> "progress"
    | Tone.Complete -> "complete"
    | Tone.Sealed -> "sealed"
    | Tone.Closed -> "closed"

/// A group phase's tone, as the contract maps Signal's `GroupPhase`:
/// ready (hollow); progress = Importing, Partial (half); review = Reconciling
/// (diamond); complete (filled); sealed = Finalized, FinalizedIncomplete,
/// Sealed (square); closed = ClosedIncomplete, Superseded (muted).
let ofPhase (phase: AdminState.GroupPhase) =
    match phase with
    | AdminState.GroupReady -> Tone.Ready
    | AdminState.Importing
    | AdminState.GroupPartial -> Tone.Progress
    | AdminState.Reconciling -> Tone.Review
    | AdminState.GroupComplete -> Tone.Complete
    | AdminState.GroupFinalized
    | AdminState.GroupFinalizedIncomplete
    | AdminState.GroupSealed -> Tone.Sealed
    | AdminState.GroupClosedIncomplete
    | AdminState.GroupSuperseded -> Tone.Closed

/// A template version's tone: draft (dashed, hollow), review (diamond) for a
/// draft in review or reviewed and not yet published, published (settled
/// fill). The contract names no tone for a version hidden from new groups;
/// it takes `closed` (muted), the tone the contract gives a group that no
/// longer takes new work, and its label says "Hidden".
let ofLifecycle (lifecycle: TemplateListing.Lifecycle) =
    match lifecycle with
    | TemplateListing.Lifecycle.Draft -> Tone.Draft
    | TemplateListing.Lifecycle.InReview
    | TemplateListing.Lifecycle.ReviewedDraft -> Tone.Review
    | TemplateListing.Lifecycle.Published -> Tone.Published
    | TemplateListing.Lifecycle.Hidden -> Tone.Closed

/// A template version's visible label.
let lifecycleLabel (lifecycle: TemplateListing.Lifecycle) =
    match lifecycle with
    | TemplateListing.Lifecycle.Draft -> "Draft"
    | TemplateListing.Lifecycle.InReview -> "In review"
    | TemplateListing.Lifecycle.ReviewedDraft -> "Reviewed"
    | TemplateListing.Lifecycle.Published -> "Published"
    | TemplateListing.Lifecycle.Hidden -> "Hidden"
