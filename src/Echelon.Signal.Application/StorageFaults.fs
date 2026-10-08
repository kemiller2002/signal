/// GitHub failures at Signal's storage boundary (AER-002, AER-032, ADM-026).
///
/// Signal calls GitHub only through Arca's GitHub adapter, which classifies
/// every operational failure with Aegis's GitHub integration
/// (`Aegis.Integration.GitHub.GitHubFailure`, through `Arca.GitHub.Faults`).
/// Signal reuses that model rather than reclassifying raw responses: an HTTP
/// outcome becomes Aegis's typed failure, its stable fault code, its safe
/// message and an Aegis fault for the boundary's sinks. What a typed storage
/// result means (a conflict, an unknown outcome, a refusal) is the domain's
/// (`ProviderContract.meaning`): those are not faults.
///
/// Signal's policy on top of Aegis's recovery hint: nothing is retried
/// automatically. Aegis suggests a bounded retry for a rate limit or an
/// unreachable network; Signal offers the retry to the person instead, and
/// never before the provider's own evidence allows (ADM-026). A conflict is
/// never retried blindly, and an unknown write outcome is reconciled first.
module Echelon.Signal.Application.StorageFaults

open System
open Aegis
open Aegis.Integration.GitHub
open Arca
open Arca.GitHub
open Echelon.Signal.Admin

/// A GitHub failure as Signal presents and handles it.
type Presented =
    { /// Aegis's stable code, for example `AEGIS.GITHUB.RATE_LIMITED`.
      Code: string
      /// Aegis's safe message: no token, URL or internal detail.
      Message: string
      /// Aegis's recovery hint, as Aegis states it.
      Recovery: RecoveryPolicy
      /// Signal never retries on its own (ADM-026).
      AutomaticRetry: bool }

/// The typed failure an HTTP outcome from GitHub represents, or None for a
/// success, a cancellation or an unknown outcome (which is not a fault).
/// `repository` is `owner/name`, never a URL.
let classify (repository: string) (outcome: HttpOutcome) : GitHubFailure.T option = Faults.ofOutcome repository outcome

/// The failure as Signal presents it.
let present (failure: GitHubFailure.T) : Presented =
    let (FaultCode code) = GitHubFailure.code failure

    { Code = code
      Message = GitHubFailure.userMessage failure
      Recovery = GitHubFailure.recovery failure
      AutomaticRetry = false }

/// The Aegis fault for the boundary's sinks. Identity (fault id, correlation
/// id, time) is an input, so this is deterministic; the context holds only
/// the operation's name and the repository as `owner/name`.
let fault (identity: FaultId * Aegis.CorrelationId * DateTimeOffset) (version: string option) (operation: string) (repository: string) (failure: GitHubFailure.T) =
    Faults.toFault identity ("Signal", version) operation (Map.ofList [ "repository", ContextValue.Internal repository ]) failure

/// What a storage call's typed failure means for the person and for
/// retrying, at `now`.
let meaning (now: DateTimeOffset) (failure: StorageFailure) = ProviderContract.meaning now failure
