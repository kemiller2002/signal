/// The administrator page through the Limen boundary (WI-0047): a fake
/// browser answers every request the engine makes (the deployment's
/// configuration, Fides' exchange, tab and device storage, navigation,
/// timers), the real Fides client signs in, and Arca's in-memory provider
/// stands in for GitHub. ADM-001, ADM-002, ADM-007, ADM-008, ADM-031..033,
/// ADM-035, ADM-052, ADM-056, ADM-065, ADM-066, ADM-070, ADM-072, ADM-077.
module Echelon.Signal.Tests.AdminPageTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Aegis
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Admin
open Echelon.Signal.Application

let accessToken = "gho_SIGNALADMINACCESSTOKEN0123456789"
let refreshToken = "ghr_SIGNALADMINREFRESHTOKEN0123456789"
let start = DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero)

let configured =
    """{"environment":"production","environmentName":"production",
"identity":{"exchange":"https://fides.test","application":"signal-admin","provider":"github","clientId":"Iv23liTEST","redirectUri":"http://127.0.0.1:4321/web/admin/index.html"},
"profiles":[{"id":"primary","label":"Survey data","provider":"github","location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"prod"}}],
"datasets":[{"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]}]}"""

let local = """{"environment":"local","environmentName":"local"}"""

let offer =
    $"""{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[{{"id":"{AdminProtocol.schedule.Id}","version":1,"fingerprint":"{AdminProtocol.schedule.Fingerprint}"}},{{"id":"{AdminProtocol.host.Id}","version":1,"fingerprint":"{AdminProtocol.host.Fingerprint}"}}]}}"""

let initialize (query: string) (hash: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Http","Storage","Clipboard","Navigation"],"location":{{"origin":"http://127.0.0.1:4321","path":"/web/admin/index.html","query":"{query}","hash":"{hash}"}},"handshake":{offer}}}"""

let json (value: string) = JsonSerializer.Serialize value

/// The browser around the page: storage, the deployment's configuration, the
/// exchange and what it was asked.
type Browser(configuration: string option) =
    member val Tab = Dictionary<string, string>()
    member val Device = Dictionary<string, string>()
    member val Left = List<string>()
    member val Broadcasts = List<string>()
    member val Requests = List<string>()
    member val Hash = "" with get, set
    member val Copied = List<string>()
    member val Navigations = List<string>()

    member this.Exchange (url: string) (body: string) =
        let at (offset: TimeSpan) = start.UtcDateTime.Add(offset).ToString("yyyy-MM-ddTHH:mm:ssZ")

        match url with
        | "https://fides.test/v1/token" when body.Contains "\"code\":\"good-code\"" ->
            200,
            $$$"""{"accessToken":"{{{accessToken}}}","accessTokenExpiresAt":"{{{at (TimeSpan.FromHours 8.0)}}}","refreshToken":"{{{refreshToken}}}","refreshTokenExpiresAt":"{{{at (TimeSpan.FromDays 180.0)}}}","identity":{"provider":"github","subject":"583231","login":"octocat","name":"The Octocat"}}"""
        | "https://fides.test/v1/revoke" -> 204, ""
        | _ -> 400, """{"error":"code_rejected"}"""

    /// The kernel's answer to one effect, if it answers.
    member this.Answer(effect: JsonNode) : string option =
        let field (name: string) = effect[name].GetValue<string>()
        let id = field "correlationId"
        this.Requests.Add(effect.ToJsonString())

        match field "kind" with
        | "Http" ->
            let url = field "url"

            let status, body =
                if url.EndsWith "signal.deployment.json" then
                    match configuration with
                    | Some text -> 200, text
                    | None -> 404, ""
                else
                    this.Exchange url (effect["body"].GetValue<string>())

            Some $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{id}","outcome":{{"kind":"Success","status":{status},"body":{json body}}}}}}}"""
        | "Storage" ->
            let key = field "key"

            let value =
                match field "operation" with
                | "get" -> match this.Device.TryGetValue key with | true, v -> $",\"value\":{json v}" | _ -> ""
                | "set" -> this.Device[key] <- field "value"; ""
                | _ -> this.Device.Remove key |> ignore; ""

            Some $"""{{"kind":"EffectResult","result":{{"kind":"StorageResult","correlationId":"{id}","outcome":{{"kind":"Success"{value}}}}}}}"""
        | "Capability" ->
            let request = effect["request"]
            let arg (name: string) = request[name].GetValue<string>()

            let result =
                match field "capability", arg "operation" with
                | "limen.schedule", _ -> """{"kind":"Fired"}"""
                | _, "tabGet" -> match this.Tab.TryGetValue(arg "key") with | true, v -> $"""{{"kind":"Value","value":{json v}}}""" | _ -> """{"kind":"Value"}"""
                | _, "tabSet" -> this.Tab[arg "key"] <- arg "value"; """{"kind":"Done"}"""
                | _, "tabRemove" -> this.Tab.Remove(arg "key") |> ignore; """{"kind":"Done"}"""
                | _, "leave" -> this.Left.Add(arg "url"); """{"kind":"Done"}"""
                | _, "broadcast" -> this.Broadcasts.Add(arg "message"); """{"kind":"Done"}"""
                | _, _ -> """{"kind":"Done"}"""

            Some $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"{field "capability"}","version":1,"outcome":{{"kind":"Completed","result":{result}}}}}}}"""
        | "Clipboard" ->
            this.Copied.Add(field "text")
            Some $"""{{"kind":"EffectResult","result":{{"kind":"ClipboardResult","correlationId":"{id}","outcome":{{"kind":"Success"}}}}}}"""
        | "Navigation" ->
            let url = field "url"
            let operation = field "operation"
            this.Navigations.Add $"{operation} {url}"
            this.Hash <- (match url.IndexOf '#' with -1 -> "" | i -> url.Substring i)
            Some $"""{{"kind":"EffectResult","result":{{"kind":"NavigationResult","correlationId":"{id}","outcome":{{"kind":"Success","location":{{"path":"/web/admin/index.html","hash":"{this.Hash}"}}}}}}}}"""
        | _ -> None

/// The page: the wire, its environment and the latest view.
type Page(browser: Browser, github: InMemoryStore, reachable: unit -> bool) =
    let keys = ref 0
    let bridge = Bridge.Bridge<AdminWork.Outcome>()

    let backend: Store.Backend =
        let offline () = async.Return(Error(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "offline")))

        { Provider =
            fun _ ->
                let real = github.Provider

                { real with
                    Read = fun ns p -> if reachable () then real.Read ns p else offline ()
                    List = fun ns p -> if reachable () then real.List ns p else offline ()
                    Commit = fun o -> if reachable () then real.Commit o else offline ()
                    ChangeToken = fun ns -> if reachable () then real.ChangeToken ns else offline () }
          Resolve =
            fun location ->
                if reachable () then
                    async.Return(
                        Ok
                            { Identity = { Provider = "github"; Subject = "583231"; Login = Some "octocat"; Kind = IdentityKind.User }
                              RepositoryId = "R_1"
                              Repository = location.Repository
                              Visibility = RepositoryVisibility.Private
                              CanRead = true
                              CanWrite = true
                              Archived = false
                              Branch = BranchAccess.Writable }
                    )
                else
                    async.Return(Error(Credential.Unreachable "offline")) }

    let env: AdminWork.Env =
        { Now = fun () -> start
          NewKey = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment keys:D6}"
          RandomBytes = fun count -> Array.init count (fun i -> byte (i + keys.Value))
          Bridge = bridge
          Backend = fun _ _ -> backend
          Identity = Identity.create
          Resolve = fun hash -> if hash = Canonical.templateHash Pilot.assessment then Some Pilot.assessment else None
          Catalog =
            [ { Hash = Canonical.templateHash Pilot.assessment
                SurveyIdentifier = Pilot.assessment.Id
                Version = Pilot.assessment.Version
                Title = Pilot.assessment.Title
                Content = Pilot.assessment } ]
          ApplicationVersion = "signal-admin/test" }

    let aegis = Boundary.configure [ (Sinks.Collector()).Sink() ]
    let mutable state = AdminWire.initial
    let mutable view: JsonNode = null

    /// Sends a message and answers every request until none is left.
    member this.Send(message: string) =
        let rec pump (message: string) =
            let next, reply = AdminWire.handle aegis env state message
            state <- next
            let node = JsonNode.Parse reply
            view <- node["view"]

            for effect in node["effects"].AsArray() do
                match browser.Answer effect with
                | Some answer -> pump answer
                | None -> ()

        pump message

    member this.Event(name: string, ?key: string, ?value: string) =
        let key = key |> Option.map (fun k -> $",\"key\":{json k}") |> Option.defaultValue ""
        let value = value |> Option.map (fun v -> $",\"value\":{json v}") |> Option.defaultValue ""
        this.Send $"""{{"kind":"Event","event":{{"name":"{name}"{key}{value}}}}}"""

    member _.Flag(name: string) = view[name].GetValue<bool>()
    member _.Text(name: string) = view[name].GetValue<string>()
    member _.Items(name: string) = view[name].AsArray() |> Seq.map (fun item -> item.AsObject()) |> List.ofSeq
    member _.ViewText = view.ToJsonString()

let openPage (configuration: string option) =
    let browser = Browser(configuration)
    let github = InMemoryStore()
    let page = Page(browser, github, fun () -> true)
    page.Send(initialize "" "")
    browser, github, page

/// Signs in through the provider's callback, in a fresh page load.
let signedIn () =
    let browser, github, page = openPage (Some configured)
    page.Event("signIn")
    let state = Uri(browser.Left[0]).Query.TrimStart('?').Split('&') |> Array.find (fun p -> p.StartsWith "state=") |> fun p -> p.Substring 6
    let reloaded = Page(browser, github, fun () -> true)
    reloaded.Send(initialize $"?code=good-code&state={state}" "")
    browser, github, reloaded

// ---- Configuration, sign-in and the dataset --------------------------------------------------

[<Fact>]
let ``a deployment without storage says so and offers nothing to administer`` () =
    let _, _, page = openPage (Some local)
    Assert.True(page.Flag "screenUnconfigured")

    let _, _, missing = openPage None
    Assert.True(missing.Flag "screenMisconfigured")

[<Fact>]
let ``a configured deployment asks for sign-in and goes to GitHub with memory-only retention`` () =
    let browser, _, page = openPage (Some configured)
    Assert.True(page.Flag "screenSignIn")
    Assert.True(page.Flag "retentionPage")

    page.Event("signIn")
    Assert.StartsWith("https://github.com/login/oauth/authorize?", browser.Left[0])
    Assert.True(page.Flag "signInBusy")

[<Fact>]
let ``the configured administrator signs in, and the dataset is set up and opened`` () =
    let browser, github, page = signedIn ()

    Assert.True(page.Flag "screenDataset", page.ViewText)
    Assert.Equal("github:583231", page.Text "principal")
    Assert.Equal("CredentialValidReadWrite", page.Text "credentialState")
    Assert.False(page.Flag "readOnly")
    // Nothing the browser holds carries the token, and memory-only retention keeps none.
    let held = String.concat "\n" (Seq.append browser.Tab.Values browser.Device.Values)
    Assert.DoesNotContain(accessToken, held)
    Assert.DoesNotContain(accessToken, page.ViewText)
    Assert.True(github.State.History.Length > InMemory.empty.History.Length)

// ---- Groups, imports and the lifecycle -----------------------------------------------------------

let link (seed: byte) (group: OpaqueId) =
    let answers = Pilot.assessment.Items |> List.map (fun item -> item.Id, Assessment.Rated Assessment.Often) |> Map.ofList
    "https://signal.example" + LiveUrl.urlFor Pilot.assessment "/web/" "" { Binding = Anonymous((OpaqueId.ofBytes (Array.init 16 (fun i -> seed + byte i))).Value, group); Answers = answers }

[<Fact>]
let ``an administrator creates a group, imports into it, and finalization waits for its obligations`` () =
    let browser, _, page = signedIn ()
    page.Event("navigate", key = "groups")
    Assert.True(page.Flag "canCreateGroup")
    page.Event("newGroupExpected", value = "2")
    page.Event("newGroupMinimum", value = "1")
    page.Event("createGroup")

    Assert.True(page.Flag "hasGroup", page.ViewText)
    let key = page.Text "groupKey"
    Assert.Equal("GroupReady", page.Text "groupPhase")
    let group = (OpaqueId.ofBytes (Convert.FromHexString key)).Value

    page.Event("importText", value = String.concat "\n" [ link 1uy group; link 2uy group; "not a link" ])
    page.Event("import")

    Assert.Equal("CompleteWithRejections", page.Text "batchStatus")
    Assert.Equal("2 of 2 accepted", page.Text "groupProgress")
    Assert.Equal("GroupComplete", page.Text "groupPhase")
    Assert.Equal(3, page.Items("importItems").Length)
    // ADM-008: separate views by outcome.
    Assert.Equal((2, 1, 0), (page.Items("importAccepted").Length, page.Items("importRejected").Length, page.Items("importDuplicate").Length))
    // Calculated analysis and lineage sit beside the canonical result (ADM-012, ADM-020).
    Assert.NotEmpty(page.Items "analysis")
    Assert.StartsWith("sha256:", page.Text "lineage")
    // Exploration (ADM-019): a chart of section means, drill into a section's
    // distribution, show percentages; the address holds it, so back restores it.
    Assert.Equal(Pilot.assessment.Dimensions.Length, page.Items("chart").Length)
    Assert.Contains("Section means", page.Text "chartDescription")
    let first = Pilot.assessment.Dimensions.Head.Id
    page.Event("navigate", key = $"/groups/{key}/results")
    Assert.True(page.Flag "viewResults")
    page.Event("exploreSection", key = first)
    Assert.True(page.Flag "hasDistribution")
    Assert.Equal(5, page.Items("distribution").Length)
    page.Event("exploreDisplay", value = "percent")
    Assert.True(page.Flag "explorePercent")
    let explored = browser.Hash
    Assert.Equal($"#/groups/{key}/results?section={first}&display=percent", explored)
    // Refinements replace the history entry; moving to the results pushed one (SIG-LINK-004).
    Assert.Equal($"replace #/groups/{key}/results?section={first}&display=percent", Seq.last browser.Navigations)
    page.Send $"""{{"kind":"LocationChanged","location":{{"path":"/web/admin/index.html","hash":"#/groups/{key}"}}}}"""
    Assert.False(page.Flag "hasDistribution")
    page.Send $"""{{"kind":"LocationChanged","location":{{"path":"/web/admin/index.html","hash":"{explored}"}}}}"""
    Assert.True(page.Flag "hasDistribution")
    Assert.True(page.Flag "explorePercent")
    // Limen sends a checkbox's value either way, with whether it is checked.
    page.Send $"""{{"kind":"Event","event":{{"name":"exploreDisplay","value":"percent","checked":false}}}}"""
    Assert.False(page.Flag "explorePercent")
    Assert.Equal($"#/groups/{key}/results?section={first}", browser.Hash)
    // The report state travels in the URL while it is small (ARP-004).
    Assert.StartsWith("a=1.", page.Text "reportFragment")

    // Finalizing needs a current contribution index (ADM-066).
    page.Event("transition", value = "finalize")
    Assert.Equal("SIGNAL.GROUP.BLOCKED_BY_OBLIGATIONS", page.Text "noticeCode")

    page.Event("navigate", key = "storage")
    page.Event("rebuildIndex")
    page.Event("openGroup", key = key)
    page.Event("transition", value = "finalize")
    Assert.Equal("Finalized", page.Text "groupPhase")
    Assert.False(page.Flag "canImport")

    page.Event("transition", value = "seal")
    Assert.Equal("Sealed", page.Text "groupPhase")
    Assert.False(page.Flag "canSeal")

[<Fact>]
let ``administrators are admitted by account number only, and capabilities follow`` () =
    let _, _, page = signedIn ()
    page.Event("navigate", key = "administrators")
    Assert.True(page.Flag "canManageAdministrators")

    page.Event("admitAccount", value = "octocat")
    page.Event("admit")
    Assert.Equal("SIGNAL.ACCESS.NOT_AN_ACCOUNT_NUMBER", page.Text "noticeCode")

    page.Event("admitAccount", value = "9919")
    page.Event("admitGrant", value = "analyst")
    page.Event("admit")
    let ids = page.Items "administrators" |> List.map (fun item -> item["id"].GetValue<string>())
    Assert.Equal<string list>([ "github:583231"; "github:9919" ], ids)

    // The last administrator cannot be removed.
    page.Event("removeAdministrator", key = "github:9919")
    page.Event("removeAdministrator", key = "github:583231")
    Assert.Equal("SIGNAL.ACCESS.LAST_ADMINISTRATOR", page.Text "noticeCode")

[<Fact>]
let ``offline the page stays readable, says it may be stale, and withholds every change`` () =
    let browser = Browser(Some configured)
    let github = InMemoryStore()
    let mutable reachable = true
    let page = Page(browser, github, fun () -> reachable)
    page.Send(initialize "" "")
    page.Event("signIn")
    let state = Uri(browser.Left[0]).Query.TrimStart('?').Split('&') |> Array.find (fun p -> p.StartsWith "state=") |> fun p -> p.Substring 6
    let loaded = Page(browser, github, fun () -> reachable)
    loaded.Send(initialize $"?code=good-code&state={state}" "")
    Assert.True(loaded.Flag "screenDataset")

    reachable <- false
    loaded.Event("refresh")

    Assert.True(loaded.Flag "offline")
    Assert.True(loaded.Flag "readOnly")
    Assert.Equal("SIGNAL.STORAGE.OFFLINE", loaded.Text "noticeCode")
    Assert.DoesNotContain("CanWriteStore", loaded.Text "capabilityList")
    Assert.Contains("CanReadStore", loaded.Text "capabilityList")

[<Fact>]
let ``another tab's sign-out ends this tab's session`` () =
    let _, _, page = signedIn ()
    page.Send """{"kind":"CapabilityFact","capability":"signal.host","fact":{"kind":"Broadcast","message":"fides:signal-admin:signed_out"}}"""
    Assert.True(page.Flag "screenSignIn", page.ViewText)

[<Fact>]
let ``a malformed message is an Aegis fault, and an unknown event fails loud`` () =
    let _, _, page = signedIn ()
    page.Send """{"kind":"Event"}"""
    Assert.True(page.Flag "hasOperationalFault")
    Assert.Throws<InvalidOperationException>(fun () -> page.Event("launchMissiles")) |> ignore

[<Fact>]
let ``another tab's change to the dataset reloads it from the provider`` () =
    let browser, github, page = signedIn ()
    // A second tab signs in (memory-only: its own sign-in) and admits an administrator.
    let first = Page(browser, github, fun () -> true)
    first.Send(initialize "" "")
    first.Event("signIn")
    let state = Uri(Seq.last browser.Left).Query.TrimStart('?').Split('&') |> Array.find (fun p -> p.StartsWith "state=") |> fun p -> p.Substring 6
    let other = Page(browser, github, fun () -> true)
    other.Send(initialize $"?code=good-code&state={state}" "")
    Assert.True(other.Flag "screenDataset", other.ViewText)
    other.Event("navigate", key = "administrators")
    other.Event("admitAccount", value = "4242")
    other.Event("admit")
    Assert.Contains("signal:ds_engagement:roster", browser.Broadcasts)

    // This tab hears it and reloads; it never trusts the notice for content.
    page.Send """{"kind":"CapabilityFact","capability":"signal.host","fact":{"kind":"Broadcast","message":"signal:ds_engagement:roster"}}"""
    page.Event("navigate", key = "administrators")
    let ids = page.Items "administrators" |> List.map (fun item -> item["id"].GetValue<string>())
    Assert.Contains("github:4242", ids)
