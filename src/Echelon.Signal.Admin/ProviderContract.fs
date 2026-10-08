/// Signal's storage-provider contract (ADM-003, ADM-006, ADM-026, ADM-073):
/// what a provider can do, stated as explicit knowledge per capability; what
/// the signed-in credential may do at the configured branch; and what each
/// storage failure means for the person and for retrying.
///
/// Signal's provider *is* Arca's provider-neutral `StorageProvider`; this
/// module only states Signal's vocabulary over it. A capability Arca's
/// contract does not offer is `Unavailable` with the reason, never simulated;
/// one the contract offers but cannot prove from the provider's declaration
/// is `Unverified` with its evidence (ADM-044). Choosing a provider never
/// changes scoring, aggregation, duplicate rules, privacy rules or hashes:
/// nothing in the survey domain refers to storage at all.
///
/// Pure.
module Echelon.Signal.Admin.ProviderContract

open System
open Arca

/// The capabilities ADM-003 names.
type StorageCapability =
    | ReadObject
    | WriteObject
    | ConditionalWrite
    | AtomicReplace
    | ListPrefix
    | BatchRead
    | BatchWrite
    | ServerSideQuery
    | ServerSideAggregate
    | Transactions
    | ImmutableObject
    | DeleteObject
    | StrongDelete
    | VersionHistory
    | Compression
    | EncryptionAtRestClaim
    | MaxObjectSize
    | MaxBatchSize
    | RateLimitObservation
    | ChangeToken
    | StreamingRead
    | StreamingWrite

let allCapabilities =
    [ ReadObject
      WriteObject
      ConditionalWrite
      AtomicReplace
      ListPrefix
      BatchRead
      BatchWrite
      ServerSideQuery
      ServerSideAggregate
      Transactions
      ImmutableObject
      DeleteObject
      StrongDelete
      VersionHistory
      Compression
      EncryptionAtRestClaim
      MaxObjectSize
      MaxBatchSize
      RateLimitObservation
      ChangeToken
      StreamingRead
      StreamingWrite ]

/// What Signal knows about one capability of one provider.
type Knowledge =
    /// Offered, at this version, as the provider declared and Arca's
    /// conformance suite proves.
    | Available of version: int
    /// Not offered, and why: explicit negative knowledge.
    | Unavailable of reason: string
    /// Offered by the contract, but not something the provider's declaration
    /// can prove; the evidence says what is and is not known.
    | Unverified of evidence: string

/// A capability Signal needs that the provider does not offer.
type CapabilityRefusal =
    { Provider: string
      Capability: StorageCapability
      Reason: string }

let private notInContract = "Arca storage contract 1 has no such capability"

/// The capability's knowledge for a provider's declared capabilities. Each
/// Signal capability is either one of Arca's declared capabilities, a part
/// of Arca's contract every conforming provider implements, or outside the
/// contract.
let knowledge (capabilities: ProviderCapabilities) (capability: StorageCapability) : Knowledge =
    let declared (arca: Capability) =
        match ProviderCapabilities.state arca capabilities with
        | CapabilityState.Available version -> Available version
        | CapabilityState.Unavailable reason -> Unavailable reason

    let contract (needs: Capability) =
        // Part of every operation of the contract, on the declared capability it rests on.
        match declared needs with
        | Available _ -> Available capabilities.ContractVersion
        | other -> other

    match capability with
    | ReadObject -> declared Capability.ReadObject
    | ConditionalWrite -> declared Capability.ConditionalWrite
    | ListPrefix -> declared Capability.ListPrefix
    | BatchWrite -> declared Capability.BatchWrite
    | ChangeToken -> declared Capability.ChangeToken
    | MaxObjectSize -> declared Capability.MaxObjectSize
    | EncryptionAtRestClaim -> declared Capability.AtRestEncryption
    // Every write is conditioned on expected state; a write is a one-change operation.
    | WriteObject -> contract Capability.ConditionalWrite
    // A delete is a conditioned change like any other.
    | DeleteObject -> contract Capability.ConditionalWrite
    // An operation's changes land as one commit or not at all.
    | AtomicReplace -> contract Capability.BatchWrite
    // Records declared immutable are refused if changed (ARCA-INT-003).
    | ImmutableObject -> contract Capability.ConditionalWrite
    | VersionHistory ->
        Unverified "the contract's History reads the commits that touched an object; Git history is evidence, never domain state"
    | RateLimitObservation ->
        Unverified "an exhausted limit is reported with the provider's retry-after and reset evidence; the remaining budget is not exposed"
    | BatchRead
    | ServerSideQuery
    | ServerSideAggregate
    | Transactions
    | StrongDelete
    | Compression
    | MaxBatchSize
    | StreamingRead
    | StreamingWrite -> Unavailable notInContract

/// Every capability's knowledge, in ADM-003's order.
let describe (capabilities: ProviderCapabilities) =
    allCapabilities |> List.map (fun capability -> capability, knowledge capabilities capability)

/// What changing administrator data needs from a provider.
let writeNeeds = [ ReadObject; ConditionalWrite; BatchWrite; ListPrefix; ChangeToken ]

/// What reading administrator data needs.
let readNeeds = [ ReadObject; ListPrefix ]

/// Every needed capability the provider does not offer; empty when all are.
let missing (needed: StorageCapability list) (capabilities: ProviderCapabilities) : CapabilityRefusal list =
    needed
    |> List.choose (fun capability ->
        match knowledge capabilities capability with
        | Available _ -> None
        | Unverified evidence ->
            Some
                { Provider = capabilities.Provider
                  Capability = capability
                  Reason = evidence }
        | Unavailable reason ->
            Some
                { Provider = capabilities.Provider
                  Capability = capability
                  Reason = reason })

// ---- ADM-073: may the credential write directly? ------------------------------------

/// Whether direct writes to the configured branch are possible, from the
/// credential's capability snapshot (ADM-073).
type WriteMode =
    | DirectWriteAvailable
    | ReadOnlyByPermission
    /// Rules on the branch require a review or checks a direct write cannot give.
    | ProtectedBranchRequiresReview of reasons: string list
    | BranchMissing
    | RepositoryArchived
    /// The repository cannot be read, so nothing about its policy is known.
    | ProviderPolicyUnknown

/// The write mode a capability snapshot shows. Phase 1 writes directly only:
/// anything else is refused explicitly, never routed around.
let writeMode (snapshot: CapabilitySnapshot) =
    if not snapshot.CanRead then ProviderPolicyUnknown
    elif snapshot.Archived then RepositoryArchived
    elif not snapshot.CanWrite then ReadOnlyByPermission
    else
        match snapshot.Branch with
        | BranchAccess.Writable -> DirectWriteAvailable
        | BranchAccess.NotWritable reasons -> ProtectedBranchRequiresReview reasons
        | BranchAccess.Missing -> BranchMissing

let writeModeCode =
    function
    | DirectWriteAvailable -> "DirectWriteAvailable"
    | ReadOnlyByPermission -> "ReadOnlyByPermission"
    | ProtectedBranchRequiresReview _ -> "ProtectedBranchRequiresReview"
    | BranchMissing -> "BranchMissing"
    | RepositoryArchived -> "RepositoryArchived"
    | ProviderPolicyUnknown -> "ProviderPolicyUnknown"

// ---- ADM-057: a storage profile's verification state ---------------------------------

/// What is known about a storage profile right now. It is evidence from the
/// provider, never configuration, and it is recomputed after provider errors
/// and credential changes (ADM-056).
type ProfileVerification =
    /// Not checked with the current credential yet.
    | NotVerified
    /// Checked: the repository the credential sees, how it may write, and
    /// what writing would still need from the provider.
    | Verified of repositoryId: string * mode: WriteMode * missing: CapabilityRefusal list
    /// The check failed; the reason is for the person.
    | VerificationFailed of reason: string

/// A profile's verification from the provider's declared capabilities and
/// the credential's capability snapshot (or why it could not be taken).
let verify (capabilities: ProviderCapabilities) (snapshot: Result<CapabilitySnapshot, string>) =
    match snapshot with
    | Error reason -> VerificationFailed reason
    | Ok found -> Verified(found.RepositoryId, writeMode found, missing writeNeeds capabilities)

/// Whether a verification allows changes.
let permitsWrites =
    function
    | Verified(_, DirectWriteAvailable, []) -> true
    | Verified _
    | NotVerified
    | VerificationFailed _ -> false

// ---- ADM-026: what a storage failure means ----------------------------------------------

/// When the same request may be made again.
type Retry =
    /// Not before this time, on the provider's evidence; never automatically.
    | NotBefore of DateTimeOffset
    /// Only once the provider says when; until then the person waits.
    | WhenProviderAllows
    /// Only after the unknown outcome is reconciled; never resent blindly.
    | AfterReconciling
    /// Only after reloading and deciding again with Signal's rules.
    | AfterReloading
    /// When the person chooses (the network is back, sign-in is renewed).
    | WhenThePersonChooses
    /// The same request will fail the same way.
    | Never

/// What one storage failure means.
type FailureMeaning =
    { /// Stable code.
      Code: string
      /// One sentence for the person; no token, URL or repository content.
      Message: string
      Retry: Retry
      /// True when nothing was written; false only for an unknown outcome.
      NothingWritten: bool }

let private writeRefusal (refusal: WriteRefusal) =
    match refusal with
    | WriteRefusal.BranchProtected -> "SIGNAL.STORAGE.BRANCH_PROTECTED", "The data branch does not accept direct changes.", Never
    | WriteRefusal.RepositoryArchived -> "SIGNAL.STORAGE.REPOSITORY_ARCHIVED", "The data repository is archived.", Never
    | WriteRefusal.ReadOnlyAccess -> "SIGNAL.STORAGE.READ_ONLY", "Your account can read the data repository but not change it.", Never
    | WriteRefusal.CredentialUnavailable _ -> "SIGNAL.STORAGE.CREDENTIAL_UNAVAILABLE", "You are not signed in any more. Sign in again.", WhenThePersonChooses
    | WriteRefusal.RepositoryIdentityChanged _ ->
        "SIGNAL.STORAGE.REPOSITORY_CHANGED", "The configured repository is now a different repository; an administrator must confirm it.", Never
    | WriteRefusal.CapabilityUnavailable refusal ->
        "SIGNAL.STORAGE.CAPABILITY_UNAVAILABLE", $"The store cannot do this: {refusal.Reason}.", Never

/// The meaning of a storage failure at `now`. A rate limit is never retried
/// automatically: the earliest retry is the provider's own evidence
/// (retry-after or the reset time), or unknown (ADM-026).
let meaning (now: DateTimeOffset) (failure: StorageFailure) : FailureMeaning =
    let make code message retry =
        { Code = code
          Message = message
          Retry = retry
          NothingWritten = true }

    match failure with
    | StorageFailure.Refused refusal ->
        let code, message, retry = writeRefusal refusal
        make code message retry
    | StorageFailure.Conflicted conflicts ->
        make "SIGNAL.STORAGE.CONFLICT" $"Something changed elsewhere ({conflicts.Length} record(s)); it is reloaded and decided again." AfterReloading
    | StorageFailure.OutcomeUnknown _ ->
        { make "SIGNAL.STORAGE.OUTCOME_UNKNOWN" "The provider did not say whether the change was saved; it is checked before anything is sent again." AfterReconciling with
            NothingWritten = false }
    | StorageFailure.ObjectTooLarge(path, bytes, limit) ->
        make "SIGNAL.STORAGE.OBJECT_TOO_LARGE" $"'{path}' is {bytes} bytes, over the provider's {limit}-byte limit." Never
    | StorageFailure.StaleChangeToken _ ->
        make "SIGNAL.STORAGE.STALE_CHANGE_TOKEN" "The data changed since it was read; it is reloaded and decided again." AfterReloading
    | StorageFailure.RateLimited(retryAfter, resetAt) ->
        let retry =
            match retryAfter, resetAt with
            | Some after, _ -> NotBefore(now + after)
            | None, Some reset -> NotBefore(DateTimeOffset.FromUnixTimeSeconds reset)
            | None, None -> WhenProviderAllows

        make "SIGNAL.STORAGE.RATE_LIMITED" "The provider is limiting requests; nothing is retried until it allows." retry
    | StorageFailure.WrongLocation _ ->
        make "SIGNAL.STORAGE.WRONG_LOCATION" "The provider serves a different location than the one configured." Never
    | StorageFailure.IntegrityRefused(path, _) ->
        make "SIGNAL.STORAGE.INTEGRITY_REFUSED" $"'{path}' is not in a state that can be changed safely; it must be repaired first." Never
    | StorageFailure.ProviderFailed(code, transient, _) ->
        make code (if transient then "The provider could not be reached." else "The provider failed.") (if transient then WhenThePersonChooses else Never)
