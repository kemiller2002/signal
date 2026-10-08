/// Administrator sign-in through the real Fides client (WI-0040): ADM-056
/// (the identity is the provider's stable subject, never a typed name),
/// ADM-071 (memory-only by default; tokens never reach storage the person
/// did not choose, the URL or Signal) and ADM-072 (other tabs are told,
/// without tokens). The browser and the exchange are fakes that speak
/// Fides' wire protocol.
module Echelon.Signal.Tests.SignInTests

open System
open System.Collections.Generic
open Xunit
open Fides
open Fides.Client
open Echelon.Signal.Admin
open Echelon.Signal.Application

let private accessToken = "gho_SIGNALTESTACCESSTOKEN0123456789"
let private refreshToken = "ghr_SIGNALTESTREFRESHTOKEN0123456789"

let private identityConfig: Deployment.IdentityConfig =
    { Exchange = "https://fides.test"
      Application = "signal-test"
      Provider = "github"
      ClientId = "Iv23liTEST"
      RedirectUri = "https://signal.test/admin/" }

/// A browser tab and the exchange behind it, recording what it was asked.
type private Browser(now: DateTimeOffset) =
    member val Tab = Dictionary<string, string>()
    member val Device = Dictionary<string, string>()
    member val Posts = List<string * string>()
    member val Navigated = List<string>()
    member val Addresses = List<string>()
    member val Broadcasts = List<string>()

    member this.Exchange (path: string) (body: string) =
        this.Posts.Add(path, body)
        let at (offset: TimeSpan) = now.UtcDateTime.Add(offset).ToString("yyyy-MM-ddTHH:mm:ssZ")

        match path with
        | "/v1/token" when body.Contains "\"code\":\"good-code\"" ->
            200,
            String.concat
                ""
                [ "{\"accessToken\":\""
                  accessToken
                  "\",\"accessTokenExpiresAt\":\""
                  at (TimeSpan.FromHours 8.0)
                  "\",\"refreshToken\":\""
                  refreshToken
                  "\",\"refreshTokenExpiresAt\":\""
                  at (TimeSpan.FromDays 180.0)
                  "\",\"identity\":{\"provider\":\"github\",\"subject\":\"583231\",\"login\":\"octocat\",\"name\":\"The Octocat\"}}" ]
        | "/v1/token" -> 400, """{"error":"code_rejected"}"""
        | "/v1/revoke" -> 204, ""
        | _ -> 404, """{"error":"not_found"}"""

    member this.Ports: ClientPorts =
        let store (table: Dictionary<string, string>) : Storage =
            { Read = fun key -> async.Return(match table.TryGetValue key with | true, v -> Some v | _ -> None)
              Write = fun key value -> async { table[key] <- value }
              Remove = fun key -> async { table.Remove key |> ignore } }

        { PostToExchange =
            fun path body ->
                async {
                    let status, answer = this.Exchange path body
                    return HttpOutcome.Responded { Status = status; Body = answer }
                }
          TabStorage = store this.Tab
          DeviceStorage = store this.Device
          Navigate = fun url -> async { this.Navigated.Add url }
          ReplaceAddress = fun url -> async { this.Addresses.Add url }
          Broadcast = fun message -> this.Broadcasts.Add message
          Now = fun () -> now
          RandomBytes = fun count -> Array.init count byte }

    /// Everything the browser holds, as text.
    member this.Everything =
        String.concat "\n" (Seq.append (this.Tab.Values) (this.Device.Values) |> Seq.append this.Navigated |> Seq.append this.Addresses |> Seq.append this.Broadcasts)

let private at = DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero)

let private queryOf (url: string) =
    let query = Uri(url).Query.TrimStart('?')
    query.Split('&') |> Array.map (fun pair -> let parts = pair.Split('=', 2) in parts[0], Uri.UnescapeDataString parts[1]) |> List.ofArray

/// Signs in with the chosen retention and completes the provider's callback.
let private signIn (retention: Credential.Retention) =
    let browser = Browser(at)
    let client = Identity.create identityConfig browser.Ports

    match client.SignIn(Identity.retentionOf retention) |> Async.RunSynchronously with
    | Ok() -> ()
    | Error reason -> failwith reason

    let state = queryOf browser.Navigated[0] |> List.find (fun (k, _) -> k = "state") |> snd
    let result = Identity.start client [ "code", "good-code"; "state", state ] |> Async.RunSynchronously
    browser, client, result

[<Fact>]
let ``signing in goes to GitHub, and the administrator is the provider's subject`` () =
    let browser, _, (signedIn, principal) = signIn Credential.ThisPage

    Assert.StartsWith("https://github.com/login/oauth/authorize?", browser.Navigated[0])
    Assert.Contains(("client_id", "Iv23liTEST"), queryOf browser.Navigated[0])
    Assert.Equal(Credential.SignedIn "583231", signedIn)
    Assert.Equal(Some { Access.PrincipalId = "github:583231"; Access.Kind = Access.Human; Access.DisplayName = "octocat" }, principal)
    // The code and state left the address bar.
    Assert.Equal<string list>([ "https://signal.test/admin/" ], List.ofSeq browser.Addresses)

[<Fact>]
let ``memory-only is the default: no token is stored anywhere in the browser`` () =
    let browser, client, _ = signIn Credential.defaultRetention

    Assert.DoesNotContain(accessToken, browser.Everything)
    Assert.DoesNotContain(refreshToken, browser.Everything)
    // The token reaches Arca only through the token provider, never as a value Signal holds.
    match Identity.tokens client () |> Async.RunSynchronously with
    | Ok token -> Assert.DoesNotContain(accessToken, string token)
    | Error reason -> failwith $"%A{reason}"

[<Fact>]
let ``this-tab retention keeps the session in the tab only, never on the device`` () =
    let browser, _, _ = signIn Credential.ThisTab

    Assert.Empty(browser.Device)
    Assert.Contains(browser.Tab.Values, fun value -> value.Contains refreshToken)

[<Fact>]
let ``a callback that did not start in this tab is refused without calling the exchange`` () =
    let browser = Browser(at)
    let client = Identity.create identityConfig browser.Ports
    let signedIn, principal = Identity.start client [ "code", "good-code"; "state", "forged" ] |> Async.RunSynchronously

    Assert.Equal(Credential.SignedOut, signedIn)
    Assert.Equal(None, principal)
    Assert.Empty(browser.Posts)

[<Fact>]
let ``signing out tells the other tabs without a token, and they downgrade`` () =
    let browser, client, _ = signIn Credential.ThisTab
    client.SignOut() |> Async.RunSynchronously |> ignore

    let notices = browser.Broadcasts |> Seq.choose (Identity.noticeOf identityConfig) |> List.ofSeq
    Assert.Equal<Credential.TabNotice list>([ Credential.SessionRenewedElsewhere; Credential.SignedOutElsewhere ], notices)
    Assert.DoesNotContain(accessToken, browser.Everything)
    Assert.Empty(browser.Tab)
    Assert.Equal(Credential.SignedOut, Identity.signInOf (client.State()))

    // Another tab receiving the sign-out drops its own session.
    let other = Browser(at)
    let second = Identity.create identityConfig other.Ports
    second.Receive(Seq.last browser.Broadcasts) |> Async.RunSynchronously
    Assert.Equal(Credential.SignedOut, Identity.signInOf (second.State()))
