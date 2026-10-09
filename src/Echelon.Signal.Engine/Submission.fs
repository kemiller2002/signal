/// Finalizing a live response into a portable submission (LURL-002, ID-002,
/// ID-004, URLC-002).
///
/// The live envelope is what the respondent edits; a submission is what the
/// administrator imports. Finalization is the one transition between them:
///
/// - identified group: the instance and group stay, because the
///   administrator intentionally maps the instance to a person outside Signal;
/// - anonymous group: a fresh AnonymousSubmissionId is drawn from
///   cryptographically secure entropy supplied by the caller, the instance is
///   removed, and the result is checked to carry no invitation-linking bytes.
///
/// Pure: the entropy arrives as bytes, so this module is deterministic and
/// testable, and the application edge decides where randomness comes from.
module Echelon.Signal.Engine.Submission

open System
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState

/// Why a response cannot be finalized. These are expected outcomes, shown to
/// the respondent; none is an operational fault.
type Refusal =
    /// Items still without an answer (don't know etc. are answers).
    | Incomplete of unanswered: int
    /// The response belongs to no invitation, so no group can receive it.
    | NoInvitation
    /// The envelope is already a finalized submission.
    | AlreadyFinal
    /// The entropy is not 16 bytes, or reproduces an invitation id. Supplying
    /// it is the caller's job; refusing it here keeps a weak source from
    /// producing a linkable id.
    | UnusableEntropy

/// Whether an envelope is a finalized submission (not editable).
let isFinal (envelope: Envelope) =
    match production envelope.Binding with
    | Identified _
    | Anonymous _ -> true
    | _ -> false

let private contains (haystack: byte[]) (needle: byte[]) =
    needle.Length > 0
    && Seq.windowed needle.Length haystack |> Seq.exists (fun window -> window = needle)

/// The anonymous invariant (LURL-002 §17): no byte sequence of the original
/// instance id survives anywhere in the encoded submission.
let isUnlinkable (assessment: Assessment) (instance: OpaqueId) (submission: Envelope) =
    let encoded = System.Buffers.Text.Base64Url.DecodeFromChars((encode assessment submission).AsSpan())
    not (contains encoded (OpaqueId.toBytes instance))

/// Finalizes a live envelope. `entropy` is consumed only for anonymous groups.
let finalize (assessment: Assessment) (entropy: byte[]) (live: Envelope) : Result<Envelope, Refusal> =
    let missing = unanswered assessment live.Answers

    if isFinal live then
        Error AlreadyFinal
    elif not missing.IsEmpty then
        Error(Incomplete missing.Length)
    else
        match live.Binding with
        | Unbound -> Error NoInvitation
        | Identified _
        | Anonymous _ -> Error AlreadyFinal
        // The pilot codec carries no test artifacts.
        | Test _ -> Error NoInvitation
        | IdentifiedInvitation(instance, group) ->
            Ok
                { live with
                    Binding = Identified(instance, group) }
        | AnonymousInvitation(instance, group) ->
            match OpaqueId.ofBytes entropy with
            | Some fresh when fresh <> instance && fresh <> group ->
                let submission =
                    { live with
                        Binding = Anonymous(fresh, group) }

                if isUnlinkable assessment instance submission then
                    Ok submission
                else
                    Error UnusableEntropy
            | _ -> Error UnusableEntropy

let describe =
    function
    | Incomplete 1 -> "1 question still needs an answer before you can submit."
    | Incomplete n -> $"{n} questions still need an answer before you can submit."
    | NoInvitation -> "This page was not opened from an invitation, so there is no group to submit to. Your results are still shown and can be printed."
    | AlreadyFinal -> "These answers have already been submitted."
    | UnusableEntropy -> "Signal could not create a private submission identifier. Nothing was submitted; please try again."
