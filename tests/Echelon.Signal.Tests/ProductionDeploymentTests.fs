/// The production Pages site's real deployment configuration (WI-0148,
/// DF-SIGNAL-2026-0027): it parses with Signal's own rules, signs in through
/// the Echelon exchange with a redirect on the administrator page, keeps its
/// data in one repository, names the owner as the first administrator, and is
/// published where the administrator page reads it, with a policy that lets
/// the page reach the exchange and GitHub's API and nothing else new.
module Echelon.Signal.Tests.ProductionDeploymentTests

open System.Text.Json
open Xunit
open Arca
open Echelon.Signal.Admin
open Echelon.Signal.Tests.Support

let private site () = JsonDocument.Parse(readRepoFile "deploy/pages/site.json").RootElement

let private configuration () =
    let from = site().GetProperty("deployment").GetProperty("from").GetString()

    match Deployment.parse (readRepoFile from) with
    | Ok config -> config
    | Error problem -> failwith $"the production configuration does not parse: %A{problem}"

[<Fact>]
let ``the production configuration parses and is the production environment`` () =
    let config = configuration ()
    Assert.Equal(EnvironmentKind.Production, config.Environment)

[<Fact>]
let ``sign-in goes through the Echelon exchange and returns to the administrator page`` () =
    match configuration().Identity with
    | Some identity ->
        Assert.Equal("https://auth.echelonfoundry.com", identity.Exchange)
        Assert.Equal("echelon-signal-production", identity.Application)
        Assert.Equal("github", identity.Provider)
        Assert.StartsWith("Iv", identity.ClientId)
        Assert.Equal("https://signal.echelonfoundry.com/web/admin/", identity.RedirectUri)
    | None -> failwith "production must configure sign-in"

[<Fact>]
let ``the data lives in one GitHub repository and the owner is the first administrator`` () =
    let config = configuration ()
    let profile = Assert.Single config.Profiles
    Assert.Equal("kemiller2002", profile.Location.Owner)
    Assert.Equal("echelon-organization-administration", profile.Location.Repository)
    let dataset = Assert.Single config.Datasets
    Assert.Equal(profile.Id, dataset.Profile)
    Assert.Equal<string list>([ "737074" ], dataset.Administrators)

[<Fact>]
let ``the configuration is published beside the administrator page, where the page reads it`` () =
    Assert.Equal("admin/signal.deployment.json", site().GetProperty("deployment").GetProperty("as").GetString())

[<Fact>]
let ``the policy lets pages reach only the exchange and GitHub's API beyond their own origin`` () =
    let policy = site().GetProperty("contentSecurityPolicy").GetString()
    let connect =
        policy.Split(';')
        |> Array.map _.Trim()
        |> Array.find _.StartsWith("connect-src")

    Assert.Equal("connect-src 'self' https://auth.echelonfoundry.com https://api.github.com", connect)
    Assert.DoesNotContain("'unsafe-inline'", policy)
