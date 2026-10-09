/// The administrator import pipeline (ARP-001, ARP-002, ARP-005, LURL-003,
/// CAN-004 §27, ARX-008 idempotency) as pure functions.
///
/// One completed respondent URL goes in; the group's state and an explicit
/// outcome come out. The pipeline order is ARP-001 §1: parse, resolve the
/// exact template, verify fingerprint and integrity, decode, validate, score,
/// produce a SurveyResult, add it to the group. Rejected submissions never
/// change counts or scores. Importing the exact same artifact again is an
/// idempotent no-op, not an error and not a second survey (ARP §5-7).
///
/// Duplicate rule (ARP §6-7, §24): an identified group accepts at most one
/// submission per SurveyInstanceId and rejects a different artifact for an
/// accepted instance rather than silently replacing it. An anonymous group
/// accepts at most one result per AnonymousSubmissionId. Strong anonymity
/// means one person who finalizes twice produces two unrelated ids; that
/// limitation is inherent and stated, not hidden (ARP §7, LURL-003 §28).
module Echelon.Signal.Engine.Import

open System
open System.Buffers.Text
open System.Security.Cryptography
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState

/// Version of the derived SurveyResult/SurveyGroupResult format (ARP §31).
[<Literal>]
let ResultVersion = 1

type IdentityMode =
    | IdentifiedGroup
    | AnonymousGroup

/// What the administrator declared when creating the group (ARP §17, §20).
/// A generic template a group runs (WI-0078): its content, the compact
/// reference its submissions carry, and its published hash.
[<NoComparison>]
type GenericForm =
    { Content: Template.Content
      Reference: byte[]
      Hash: string }

[<NoComparison>]
type GroupDefinition =
    { Group: OpaqueId
      Mode: IdentityMode
      ExpectedCount: int
      /// The group's sections and questions as the result pipeline reads
      /// them (for a generic template, its shape: one dimension per section).
      Template: Assessment
      /// Present when the group runs a generic template: submissions are its
      /// generic envelopes, scored by its own scorers (`GenericImport`).
      Generic: GenericForm option }

/// The identity one accepted result contributes under: never both an
/// instance and an anonymous id (ARP §4).
[<NoComparison>]
type SubmissionIdentity =
    | Instance of OpaqueId
    | AnonymousSubmission of OpaqueId

    /// The stable key a group indexes results by.
    member this.Key =
        match this with
        | Instance id -> $"instance:{id}"
        | AnonymousSubmission id -> $"anonymous:{id}"

/// The interpreted result of one accepted submission (ARP §3).
[<NoComparison>]
type SurveyResult =
    { Identity: SubmissionIdentity
      SurveyIdentifier: string
      TemplateVersion: string
      TemplateHash: string
      /// SHA-256 over the canonical submission bytes (ARP §5).
      SubmissionHash: string
      Dimensions: (Dimension * DimensionResult) list
      /// Answer counts only; decoded answers are not retained (ARP §11).
      AnsweredCount: int
      NonNumericCount: int }

/// Why a submission was not accepted. Stable codes, never guesses (ARP §58).
type ImportError =
    /// The text holds no envelope (no `#r=` and not a bare payload).
    | NoSubmissionFound
    | Unreadable of DecodeError
    /// A live response, not a finalized submission.
    | NotFinalized
    | WrongGroup
    /// An identified submission in an anonymous group, or the reverse
    /// (mixing is prohibited by default, ARP §20).
    | IdentityModeMismatch
    | IncompleteSubmission of unanswered: int
    /// An identified instance already contributed a different submission.
    | DuplicateInstance of existingHash: string

[<NoComparison>]
type ImportOutcome =
    | Accepted of SurveyResult
    /// The exact same artifact was already accepted: nothing changes.
    | AlreadyImported of SubmissionIdentity
    | Rejected of ImportError

/// The group's accepted results, keyed by identity, and an audit log of
/// every attempt's outcome code in order (no answers, no PII; ADM-030).
[<NoComparison>]
type GroupState =
    { Definition: GroupDefinition
      /// Accepted results by `SubmissionIdentity.Key`.
      Results: Map<string, SurveyResult>
      Log: string list }

let empty (definition: GroupDefinition) =
    { Definition = definition
      Results = Map.empty
      Log = [] }

/// The envelope text in a pasted URL (`...#r=<payload>`), or the text itself
/// when it is a bare payload.
let payloadOf (text: string) =
    let trimmed = text.Trim()

    match trimmed.IndexOf '#' with
    | -1 when trimmed.Length > 0 && not (trimmed.Contains '=') && not (trimmed.Contains '/') -> Some trimmed
    | -1 -> None
    | i ->
        trimmed.Substring(i + 1).Split('&')
        |> Array.tryPick (fun part ->
            match part.Split('=', 2) with
            | [| key; value |] when key = LiveUrl.FragmentKey -> Some value
            | _ -> None)

let private sha256Hex (bytes: byte[]) =
    "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

/// Interprets one decoded, validated envelope.
let private interpret (assessment: Assessment) (identity: SubmissionIdentity) (envelope: Envelope) =
    let canonical = Base64Url.DecodeFromChars((encode assessment envelope).AsSpan())

    { Identity = identity
      SurveyIdentifier = assessment.Id
      TemplateVersion = assessment.Version
      TemplateHash = Canonical.templateHash assessment
      SubmissionHash = sha256Hex canonical
      Dimensions = score assessment envelope.Answers
      AnsweredCount = envelope.Answers.Count
      NonNumericCount = envelope.Answers |> Map.filter (fun _ a -> (numericValue a).IsNone) |> Map.count }

/// The identity a finalized submission contributes under, or why it cannot
/// contribute to this group.
let identify (definition: GroupDefinition) (binding: Binding) : Result<SubmissionIdentity, ImportError> =
    let identity, group =
        match binding with
        | Identified(instance, group) -> Some(Instance instance, IdentifiedGroup), Some group
        | Anonymous(submission, group) -> Some(AnonymousSubmission submission, AnonymousGroup), Some group
        | Unbound
        | IdentifiedInvitation _
        | AnonymousInvitation _ -> None, None

    match identity, group with
    | None, _
    | _, None -> Error NotFinalized
    | Some _, Some group when group <> definition.Group -> Error WrongGroup
    | Some(_, mode), _ when mode <> definition.Mode -> Error IdentityModeMismatch
    | Some(identity, _), _ -> Ok identity

let sha256Of (bytes: byte[]) = sha256Hex bytes

/// Reads and validates one submission against a group definition and the
/// group's accepted identities (`accepted` gives the SubmissionHash accepted
/// for an identity key, if any), without changing anything. The accepted
/// set may come from the results in memory or from a store's index.
let evaluateAgainst (definition: GroupDefinition) (accepted: string -> string option) (text: string) : ImportOutcome =
    let assessment = definition.Template

    match payloadOf text with
    | None -> Rejected NoSubmissionFound
    | Some payload ->
        match decode assessment payload with
        | Error error -> Rejected(Unreadable error)
        | Ok envelope ->
            match identify definition envelope.Binding with
            | Error error -> Rejected error
            | Ok identity ->
                match unanswered assessment envelope.Answers with
                | missing when not missing.IsEmpty -> Rejected(IncompleteSubmission missing.Length)
                | _ ->
                    let result = interpret assessment identity envelope

                    match accepted identity.Key with
                    | Some existing when existing = result.SubmissionHash -> AlreadyImported identity
                    | Some existing -> Rejected(DuplicateInstance existing)
                    | None -> Accepted result

/// Reads and validates one submission against a group, without changing it.
let evaluate (state: GroupState) (text: string) : ImportOutcome =
    evaluateAgainst state.Definition (fun key -> state.Results.TryFind key |> Option.map _.SubmissionHash) text

/// The outcome's stable code, as the audit log records it.
let outcomeCode =
    function
    | Accepted _ -> "accepted"
    | AlreadyImported _ -> "already-imported"
    | Rejected NoSubmissionFound -> "rejected:no-submission"
    | Rejected(Unreadable e) -> $"rejected:unreadable:{e}"
    | Rejected NotFinalized -> "rejected:not-finalized"
    | Rejected WrongGroup -> "rejected:wrong-group"
    | Rejected IdentityModeMismatch -> "rejected:identity-mode"
    | Rejected(IncompleteSubmission n) -> $"rejected:incomplete:{n}"
    | Rejected(DuplicateInstance _) -> "rejected:duplicate-instance"

/// Imports one submission: the new group state and what happened.
let importOne (state: GroupState) (text: string) : GroupState * ImportOutcome =
    let outcome = evaluate state text

    let results =
        match outcome with
        | Accepted result -> state.Results.Add(result.Identity.Key, result)
        | AlreadyImported _
        | Rejected _ -> state.Results

    { state with
        Results = results
        Log = state.Log @ [ outcomeCode outcome ] },
    outcome

/// Imports a batch in order; each item's outcome is reported (ADM-060).
let importAll (state: GroupState) (texts: string list) =
    texts
    |> List.fold
        (fun (state, outcomes) text ->
            let next, outcome = importOne state text
            next, outcomes @ [ outcome ])
        (state, [])
