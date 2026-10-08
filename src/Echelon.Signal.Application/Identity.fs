/// Administrator sign-in through Fides (ADM-056, ADM-071, ADM-072).
///
/// The real Fides client does the work: PKCE, the exchange, token refresh,
/// revocation and cross-tab messages. It asks its host for browser services
/// (`ClientPorts`: the exchange, tab and device storage, navigation, the
/// address bar, broadcasting, the clock and random bytes); the page supplies
/// them through Limen. Signal sees only what Fides reports: the sign-in
/// state and the identity the provider resolved (its stable subject, never a
/// typed name). Tokens stay inside the client; Arca gets the client's token
/// provider, never a token.
///
/// Retention is memory-only unless the administrator chooses this tab
/// (`Credential.offeredRetentions`); nothing is kept across browser restarts.
module Echelon.Signal.Application.Identity

open Fides
open Fides.Client
open Echelon.Signal.Admin

/// The client configuration for a deployment's identity settings.
let configuration (config: Deployment.IdentityConfig) : ClientConfiguration =
    { Application = config.Application
      Provider = ProviderId config.Provider
      ClientId = config.ClientId
      RedirectUri = config.RedirectUri }

/// The identity providers Signal signs in with.
let catalog = ProviderCatalog.ofList [ GitHub.provider GitHub.githubDotCom ]

/// The administrator an identity is: `<provider>:<subject>`, a person. The
/// login is only a display name.
let principalOf (identity: Identity) : Access.Principal =
    let (ProviderId provider) = identity.Provider

    { PrincipalId = $"{provider}:{identity.Subject}"
      Kind = Access.Human
      DisplayName = identity.Login }

/// The sign-in state Signal reasons about.
let signInOf (state: SessionState) : Credential.SignIn =
    match state with
    | SessionState.SignedOut -> Credential.SignedOut
    | SessionState.SigningIn -> Credential.SigningIn
    | SessionState.SignedIn identity -> Credential.SignedIn identity.Subject
    | SessionState.Expired -> Credential.Expired
    | SessionState.Revoked -> Credential.Revoked
    | SessionState.ProviderUnavailable -> Credential.ProviderUnavailable

/// Fides' retention for the one the administrator chose.
let retentionOf (retention: Credential.Retention) =
    match retention with
    | Credential.ThisPage -> MemoryOnly
    | Credential.ThisTab -> SessionScoped

/// Whether a page was opened by the provider's callback.
let isCallback (query: (string * string) list) =
    query |> List.exists (fun (name, _) -> name = "state" || name = "code" || name = "error")

/// What another tab's Fides message means for this tab, if it is one.
let noticeOf (config: Deployment.IdentityConfig) (message: string) : Credential.TabNotice option =
    let prefix = FidesClient.message (configuration config) ""

    if message.StartsWith prefix then
        match message.Substring prefix.Length with
        | "signed_out"
        | "revoked" -> Some Credential.SignedOutElsewhere
        | "signed_in" -> Some Credential.SessionRenewedElsewhere
        | _ -> None
    else
        None

/// The Fides client for a deployment, over the page's ports.
let create (config: Deployment.IdentityConfig) (ports: ClientPorts) : FidesClient =
    FidesClient.create (configuration config) catalog ports

/// The page has loaded: complete the provider's callback when the address
/// carries one, otherwise restore a kept session. The result is the sign-in
/// state and, when signed in, the administrator.
let start (client: FidesClient) (query: (string * string) list) : Async<Credential.SignIn * Access.Principal option> =
    async {
        if isCallback query then
            match! client.CompleteCallback query with
            | CompletedSignIn identity -> return Credential.SignedIn identity.Subject, Some(principalOf identity)
            | _ -> return Credential.SignedOut, None
        else
            match! client.Restore() with
            | SessionState.SignedIn identity -> return Credential.SignedIn identity.Subject, Some(principalOf identity)
            | other -> return signInOf other, None
    }

/// Arca's token provider for the client: Arca asks Fides for a token on each
/// request and never holds one.
let tokens (client: FidesClient) : Arca.TokenProvider = Fides.Arca.TokenBridge.ofClient client
