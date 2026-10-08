/// The administrator's credential and what it allows, separate from the
/// domain data (ADM-056, ADM-070, ADM-071, ADM-072).
///
/// The token itself never reaches this module: Fides keeps it, and Arca asks
/// Fides for it. What Signal holds is non-secret evidence: the sign-in state
/// Fides reports and the capability snapshot Arca resolves for the
/// configured repository. From these it derives a typed credential state,
/// and from that and the roster the capabilities the person may use now.
/// Every provider error, sign-in change and message from another tab
/// recomputes them; a stale tab still relies on the provider's concurrency
/// checks, never on the notification.
///
/// Pure.
module Echelon.Signal.Admin.Credential

open Arca
open Echelon.Signal.Admin.Access

/// The sign-in state as Fides reports it (no token).
type SignIn =
    | SignedOut
    | SigningIn
    /// Signed in as this provider subject (GitHub's numeric account id).
    | SignedIn of subject: string
    | Expired
    | Revoked
    | ProviderUnavailable

/// Why the credential's capabilities could not be resolved.
type ResolveProblem =
    /// No usable token: none, expired, revoked, or the exchange failed.
    | NoUsableToken of TokenUnavailable
    /// The provider refused the token.
    | Rejected
    /// The account cannot see the configured repository.
    | RepositoryNotVisible of repository: string
    /// The provider could not be reached.
    | Unreachable of reason: string

/// The credential's state (ADM-056).
type CredentialState =
    | CredentialMissing
    | CredentialExpired
    | CredentialRejected
    /// Valid, but it cannot read the configured repository.
    | PermissionInsufficient of reason: string
    /// The configured location now resolves to a different repository than
    /// the dataset was pinned to: an administrator must resolve it.
    | ProviderIdentityMismatch of expected: string * actual: string
    | CredentialValidReadOnly of reason: string
    | CredentialValidReadWrite
    /// The provider cannot be reached; nothing is known about the credential now.
    | CredentialUnverifiable of reason: string

let stateCode =
    function
    | CredentialMissing -> "CredentialMissing"
    | CredentialExpired -> "CredentialExpired"
    | CredentialRejected -> "CredentialRejected"
    | PermissionInsufficient _ -> "PermissionInsufficient"
    | ProviderIdentityMismatch _ -> "ProviderIdentityMismatch"
    | CredentialValidReadOnly _ -> "CredentialValidReadOnly"
    | CredentialValidReadWrite -> "CredentialValidReadWrite"
    | CredentialUnverifiable _ -> "CredentialUnverifiable"

/// The credential state from the sign-in state, the repository the dataset
/// is pinned to (None before it was first opened), and the capability
/// snapshot (or why it could not be taken).
let assess (signIn: SignIn) (pinned: string option) (snapshot: Result<CapabilitySnapshot, ResolveProblem> option) : CredentialState =
    match signIn, snapshot with
    | (SignedOut | SigningIn), _ -> CredentialMissing
    | Expired, _ -> CredentialExpired
    | Revoked, _ -> CredentialRejected
    | ProviderUnavailable, _ -> CredentialUnverifiable "the sign-in service cannot be reached"
    | SignedIn _, None -> CredentialUnverifiable "not checked with the provider yet"
    | SignedIn _, Some(Error problem) ->
        match problem with
        | NoUsableToken TokenUnavailable.NoToken -> CredentialMissing
        | NoUsableToken TokenUnavailable.Expired -> CredentialExpired
        | NoUsableToken TokenUnavailable.Revoked
        | Rejected -> CredentialRejected
        | NoUsableToken(TokenUnavailable.ProviderFailed reason)
        | Unreachable reason -> CredentialUnverifiable reason
        | RepositoryNotVisible repository -> PermissionInsufficient $"the account cannot see {repository}"
    | SignedIn _, Some(Ok found) ->
        match CapabilitySnapshot.checkRepository pinned found with
        | Error(WriteRefusal.RepositoryIdentityChanged(expected, actual)) -> ProviderIdentityMismatch(expected, actual)
        | Error _ -> CredentialUnverifiable "the repository check failed"
        | Ok() ->
            if not found.CanRead then
                PermissionInsufficient "the account cannot read the repository"
            else
                match CapabilitySnapshot.permitsWrite found with
                | Ok() -> CredentialValidReadWrite
                | Error WriteRefusal.RepositoryArchived -> CredentialValidReadOnly "the repository is archived"
                | Error WriteRefusal.BranchProtected -> CredentialValidReadOnly "the branch does not accept direct writes"
                | Error _ -> CredentialValidReadOnly "the account may read but not write"

/// Whether the credential state lets the person read what is stored.
let permitsReading =
    function
    | CredentialValidReadOnly _
    | CredentialValidReadWrite -> true
    | _ -> false

/// Whether data loaded earlier may still be shown, read-only, in this state:
/// verified data stays viewable while offline or unverifiable (ADM-070), but
/// not after the credential was rejected, expired or the repository changed.
let keepsLoadedData =
    function
    | CredentialValidReadOnly _
    | CredentialValidReadWrite
    | CredentialUnverifiable _ -> true
    | CredentialMissing
    | CredentialExpired
    | CredentialRejected
    | PermissionInsufficient _
    | ProviderIdentityMismatch _ -> false

// ---- ADM-071: where the credential may be kept ------------------------------------------

/// Where Fides may keep the session's tokens.
type Retention =
    /// In memory only; gone on reload. The default.
    | ThisPage
    /// In this tab's session storage; gone when the tab closes.
    | ThisTab

/// The retentions Signal offers, safest first. Keeping a credential across
/// browser restarts is not offered: an administrator signs in again.
let offeredRetentions = [ ThisPage; ThisTab ]

/// The default retention.
let defaultRetention = ThisPage

/// What the person is told about a retention before choosing it.
let disclosure =
    function
    | ThisPage -> "Your sign-in is kept only on this page and ends when you reload or close it."
    | ThisTab -> "Your sign-in is kept in this tab until you close it or sign out. Others using this browser cannot see it."

// ---- ADM-070: connectivity --------------------------------------------------------------

/// Whether the provider is reachable.
type Connectivity =
    | Online
    /// Offline or interrupted, since the last verified refresh at this time.
    | Degraded of reason: string * lastVerified: System.DateTimeOffset option

// ---- What the person may do now -----------------------------------------------------------

/// One capability the person holds in the roster but cannot use now, and why.
type Withheld = { Capability: Capability; Reason: string }

/// The capabilities usable now, and those withheld with the reason. A
/// mutation is usable only with a read-write credential, online, with a
/// verified write grant; there is no offline journal (ADM-070), so offline
/// every mutation is withheld rather than queued.
let usable (roster: Roster) (principalId: string) (state: CredentialState) (connectivity: Connectivity) (writeGranted: bool) =
    let held = capabilitiesOf roster principalId

    let reasonFor (capability: Capability) =
        match state, connectivity with
        | s, _ when not (keepsLoadedData s) -> Some $"your sign-in is not usable ({stateCode s})"
        | _, _ when not (mutating.Contains capability) -> None
        | _, Degraded(reason, _) -> Some $"changes are unavailable while offline ({reason})"
        | CredentialValidReadOnly reason, _ -> Some $"read-only: {reason}"
        | CredentialUnverifiable reason, _ -> Some $"the provider cannot confirm your access ({reason})"
        | CredentialValidReadWrite, Online when not writeGranted -> Some "the dataset is read-only until its stored data is verified"
        | _ -> None

    let reasons = held |> Set.toList |> List.map (fun capability -> capability, reasonFor capability)

    reasons |> List.choose (fun (c, reason) -> if reason.IsNone then Some c else None) |> Set.ofList,
    reasons |> List.choose (fun (c, reason) -> reason |> Option.map (fun r -> { Capability = c; Reason = r }))

// ---- ADM-072: other tabs -----------------------------------------------------------------

/// What another tab announced. Notifications only downgrade: they never
/// grant anything, and the provider's evidence still decides.
type TabNotice =
    | SignedOutElsewhere
    | SessionRenewedElsewhere
    | RosterChangedElsewhere
    | StorageChangedElsewhere

/// The credential state after another tab's notice: a sign-out elsewhere
/// ends this tab's session; a renewal or change elsewhere means this tab's
/// evidence is stale until it is checked again.
let afterNotice (notice: TabNotice) (state: CredentialState) =
    match notice with
    | SignedOutElsewhere -> CredentialMissing
    | SessionRenewedElsewhere
    | RosterChangedElsewhere
    | StorageChangedElsewhere ->
        match state with
        | CredentialValidReadWrite
        | CredentialValidReadOnly _ -> CredentialUnverifiable "another tab changed something; checking again"
        | other -> other
