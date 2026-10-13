/// The shared shape of Signal's state systems (ARX-002, WI-0092): legal
/// states, commands with explicit illegal-transition results, capabilities
/// derived from state, obligations that name the evidence discharging them,
/// and knowledge that keeps "unknown" and "searched, not found" apart from
/// "absent".
///
/// Each state system keeps its own types; it exposes itself as a `System`
/// so one set of properties (`StateModelTests`) holds every system to the
/// same rules.
///
/// Pure.
module Echelon.Signal.Engine.StateModel

/// What is known about something looked for. "Searched, not found" names
/// where and at what revision, so it is never generalized into "does not
/// exist"; "unknown" is not "failed".
type Knowledge<'a> =
    | Known of 'a
    /// The outcome is not known (an external write that may have landed).
    | Unknown of reason: string
    /// Looked for in `scope` at `revision`, and not there.
    | SearchedNotFound of scope: string * revision: string

/// Unresolved work a state carries, and the evidence that discharges it.
type Obligation =
    { Code: string
      Detail: string
      DischargedBy: string }

/// An explicit illegal-transition result: the system, the state it was in,
/// the command, and why.
type Illegal =
    { System: string
      From: string
      Command: string
      Reason: string }

    member this.Code = $"SIGNAL.{this.System.ToUpperInvariant()}.ILLEGAL_TRANSITION"

/// The capability vocabulary (ARX-002), shared by every system.
module Capability =
    let CanAnswer = "CanAnswer"
    let CanNavigateBack = "CanNavigateBack"
    let CanFinalize = "CanFinalize"
    let CanCopySubmission = "CanCopySubmission"
    let CanImportSubmission = "CanImportSubmission"
    let CanPublishTemplate = "CanPublishTemplate"
    let CanPersistReportState = "CanPersistReportState"
    let CanRetryEffect = "CanRetryEffect"
    let CanReconcileEffect = "CanReconcileEffect"
    let CanRenderFinalReport = "CanRenderFinalReport"
    let CanCloseGroup = "CanCloseGroup"
    let CanReopenGroup = "CanReopenGroup"
    let CanSealGroup = "CanSealGroup"
    let CanSupersede = "CanSupersede"
    let CanEditDraft = "CanEditDraft"

    let all =
        set
            [ CanAnswer; CanNavigateBack; CanFinalize; CanCopySubmission; CanImportSubmission; CanPublishTemplate; CanPersistReportState
              CanRetryEffect; CanReconcileEffect; CanRenderFinalReport; CanCloseGroup; CanReopenGroup; CanSealGroup; CanSupersede; CanEditDraft ]

/// One state system, as the shared properties see it.
[<NoEquality; NoComparison>]
type System<'state, 'command> =
    { Name: string
      StateName: 'state -> string
      CommandName: 'command -> string
      /// The next state, or an explicit illegal-transition result.
      Attempt: 'command -> 'state -> Result<'state, Illegal>
      /// Derived from the state alone, never granted separately.
      Capabilities: 'state -> Set<string>
      /// The capability a command needs, if it needs one: without it the
      /// command is refused.
      Requires: 'command -> string option
      Obligations: 'state -> Obligation list
      /// A stable state: no command moves it.
      Terminal: 'state -> bool }

let illegal (system: string) (from: string) (command: string) (reason: string) =
    Error
        { System = system
          From = from
          Command = command
          Reason = reason }
