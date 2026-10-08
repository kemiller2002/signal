/// The browser services Fides' client and Arca's GitHub adapter need, as
/// kernel requests through the `Bridge` (ADM-035: the browser performs the
/// mechanics, F# decides).
///
/// - Fides' `ClientPorts`: the exchange is Limen's Http effect, device
///   storage its Storage effect, and this tab's session storage, leaving for
///   the provider, tidying the address bar and telling other tabs are
///   `signal.host` requests.
/// - Arca's `Host`: each GitHub request is a Limen Http request with the
///   token Fides' provider gives for that request; a 401 is reported to
///   Fides, which then treats the session as revoked (ADM-056); waits are
///   `limen.schedule` timeouts.
module Echelon.Signal.Application.AdminPorts

open System
open Fides
open Fides.Client
open Arca.GitHub
open Echelon.Signal.Application.Bridge

/// How long the exchange may take to answer.
[<Literal>]
let ExchangeTimeoutMs = 15000

let private read (bridge: Bridge<'msg>) request =
    async {
        match! bridge.Call request with
        | Read value -> return value
        | _ -> return None
    }

/// Fides' client ports over the bridge, for an exchange origin.
let fidesPorts (bridge: Bridge<'msg>) (now: unit -> DateTimeOffset) (randomBytes: int -> byte array) (exchange: string) : ClientPorts =
    { PostToExchange =
        fun path body ->
            async {
                match! bridge.Call(Http("POST", exchange + path, [ "Content-Type", "application/json" ], Some body, ExchangeTimeoutMs, [])) with
                | Answered(HttpSucceeded(status, _, body)) -> return Fides.HttpOutcome.Responded { Status = status; Body = body }
                | Answered(HttpUnknown _) -> return Fides.HttpOutcome.Failed TransportFailure.TimedOut
                | _ -> return Fides.HttpOutcome.Failed TransportFailure.Unreachable
            }
      TabStorage =
        { Read = fun key -> read bridge (TabGet key)
          Write = fun key value -> bridge.Call(TabSet(key, value)) |> Async.Ignore
          Remove = fun key -> bridge.Call(TabRemove key) |> Async.Ignore }
      DeviceStorage =
        { Read = fun key -> read bridge (DeviceGet key)
          Write = fun key value -> bridge.Call(DeviceSet(key, value)) |> Async.Ignore
          Remove = fun key -> bridge.Call(DeviceRemove key) |> Async.Ignore }
      Navigate = fun url -> bridge.Call(Leave url) |> Async.Ignore
      ReplaceAddress = fun url -> bridge.Call(ReplaceAddress url) |> Async.Ignore
      Broadcast =
        fun message ->
            bridge.Start(
                async {
                    let! _ = bridge.Call(Announce message)
                    return []
                }
            )
      Now = now
      RandomBytes = randomBytes }

let private methodName =
    function
    | HttpMethod.Get -> "GET"
    | HttpMethod.Post -> "POST"
    | HttpMethod.Patch -> "PATCH"
    | HttpMethod.Put -> "PUT"
    | HttpMethod.Delete -> "DELETE"

/// Arca's GitHub host over the bridge, its tokens from Fides. A 401 to a
/// credentialed request is reported to Fides.
let arcaHost (bridge: Bridge<'msg>) (tokens: unit -> Arca.TokenProvider option) (unauthorized: unit -> Async<unit>) : Host =
    { Send =
        fun authorized ->
            async {
                let request = authorized.Request
                let credential = authorized.Credential |> Option.map Arca.AccessToken.authorization |> Option.toList

                match! bridge.Call(Http(methodName request.Method, request.Url, request.Headers @ credential, request.Body, request.TimeoutMs, request.ResponseHeaders)) with
                | Answered(HttpSucceeded(status, headers, body)) ->
                    if status = 401 && authorized.Credential.IsSome then
                        do! unauthorized ()

                    return HttpOutcome.Response(status, Http.normalizeHeaders headers, body)
                | Answered(HttpFailed reason) ->
                    return
                        HttpOutcome.Failed(
                            match reason with
                            | "aborted" -> HttpFailure.Aborted
                            | "invalid-response" -> HttpFailure.InvalidResponse
                            | "too-large" -> HttpFailure.TooLarge
                            | _ -> HttpFailure.Network
                        )
                | Answered HttpCancelled -> return HttpOutcome.Cancelled
                | Answered(HttpUnknown reason) ->
                    return HttpOutcome.OutcomeUnknown(if reason = "connection-lost" then UnknownReason.ConnectionLost else UnknownReason.TimeoutAfterDispatch)
                | Read _
                | Done
                | Refused _ -> return HttpOutcome.Failed HttpFailure.InvalidResponse
            }
      Wait = fun delay -> bridge.Call(Sleep(int delay.TotalMilliseconds)) |> Async.Ignore
      Tokens =
        fun () ->
            match tokens () with
            | Some provider -> provider ()
            | None -> async.Return(Error Arca.TokenUnavailable.NoToken) }
