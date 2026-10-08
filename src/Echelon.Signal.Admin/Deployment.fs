/// A deployment's configuration (SIG-DATALOC-001, ADM-004, ADM-057): where
/// its administrator data lives, how administrators sign in, and who may set
/// each dataset up. Every value comes from the deployment's configuration
/// document; Signal hard-codes no repository, owner, branch, base path,
/// sign-in host or client id.
///
/// ```json
/// {"environment":"production","environmentName":"production",
///  "identity":{"exchange":"https://fides.acme.example","application":"signal-production",
///              "provider":"github","clientId":"Iv23li...","redirectUri":"https://signal.acme.example/admin/"},
///  "profiles":[
///    {"id":"primary","label":"Survey data","provider":"github",
///     "location":{"owner":"acme","repository":"signal-data","branch":"main","basePath":"deployments/prod"}},
///    {"id":"hr","label":"HR surveys","provider":"github",
///     "location":{"owner":"acme-hr","repository":"signal-hr","branch":"main","basePath":""}}],
///  "datasets":[
///    {"id":"ds_engagement","label":"Engagement","profile":"primary","administrators":["583231"]},
///    {"id":"ds_hr","label":"HR pulse","profile":"hr","administrators":["583231","9919"]}]}
/// ```
///
/// - A **storage profile** (ADM-057) binds a provider and its locator. The
///   first profile is the deployment's: Signal's own namespace lives there.
///   A profile's label is local presentation, never identity.
/// - A **dataset** names the profile its data lives in by the profile's
///   stable id, never by its label. A dataset whose data needs other
///   permissions than the rest points at a profile with its own repository,
///   because GitHub permissions apply per repository (SIG-DATALOC-004).
/// - **Administrators** are GitHub numeric account ids: the people who may
///   set the dataset up and become its first administrators. No one becomes
///   one by opening it.
///
/// `identity`, `profiles` and `datasets` are optional together: a local
/// deployment configures none of them and stores nothing. Nothing in the
/// document is secret: the client id is public and the client secret stays
/// with the exchange; a token has no place in it (ADM-004, ADM-071).
///
/// Pure.
module Echelon.Signal.Admin.Deployment

open System
open Arca
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

/// The storage providers this version of Signal stores through. An
/// installable service is a future provider (ADM-006); naming one now is
/// refused, never simulated.
let providers = [ "github" ]

/// The identity providers Signal signs administrators in with.
let identityProviders = [ "github" ]

/// One configured data location, as the configuration states it.
type LocationConfig =
    { Owner: string
      Repository: string
      Branch: string
      /// The folder application namespaces live under; empty for the repository root.
      BasePath: string }

/// A storage profile (ADM-057).
type ProfileConfig =
    { /// Stable id that datasets reference.
      Id: string
      /// Local display label; never identity.
      Label: string
      /// The provider type, by id: `github`.
      Provider: string
      Location: LocationConfig }

/// How administrators sign in: Fides' exchange and this deployment's
/// registration with it.
type IdentityConfig =
    { /// The exchange's origin, for example `https://fides.acme.example`.
      Exchange: string
      /// The application's id as registered with the exchange.
      Application: string
      /// The identity provider, by id: `github`.
      Provider: string
      /// The provider's public OAuth client id for the exchange.
      ClientId: string
      /// The exact redirect URI registered for this deployment.
      RedirectUri: string }

/// A dataset the deployment serves.
type DatasetConfig =
    { /// Immutable id; it names the dataset's folder.
      Id: string
      /// Local display label.
      Label: string
      /// The id of the profile its data lives in.
      Profile: string
      /// GitHub numeric account ids who may set it up and administer it first.
      Administrators: string list }

/// A deployment's configuration.
type DeploymentConfig =
    { Environment: EnvironmentKind
      /// A display name for the environment, for example "production".
      EnvironmentName: string
      Identity: IdentityConfig option
      /// The storage profiles, the deployment's own first.
      Profiles: ProfileConfig list
      Datasets: DatasetConfig list }

/// A location's Arca form, or why it is not a valid location.
let dataLocation (config: LocationConfig) =
    DataLocation.create config.Owner config.Repository config.Branch config.BasePath
    |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// A configured dataset, by id.
let dataset (config: DeploymentConfig) (datasetId: string) =
    config.Datasets |> List.tryFind (fun found -> found.Id = datasetId)

/// A configured profile, by id.
let profile (config: DeploymentConfig) (profileId: string) =
    config.Profiles |> List.tryFind (fun found -> found.Id = profileId)

/// The deployment's own profile: the first.
let primaryProfile (config: DeploymentConfig) = config.Profiles |> List.tryHead

/// Whether an actor (`github:<numeric id>`) is one of the dataset's
/// configured administrators. A login or a display name never matches.
let isBootstrapAdministrator (dataset: DatasetConfig) (actorId: string) =
    match actorId.Split(':', 2) with
    | [| "github"; id |] -> List.contains id dataset.Administrators
    | _ -> false

// ---- Parsing ------------------------------------------------------------------------

let private invalid detail = Error(InvalidConfiguration detail)

let private decoded (result: Decoded<'a>) = result |> Result.mapError InvalidConfiguration

let private loopback (uri: Uri) =
    uri.IsLoopback && (uri.Host = "localhost" || uri.Host = "127.0.0.1" || uri.Host = "[::1]")

/// An absolute https address (http only on this machine, for development),
/// with no user information; `originOnly` also refuses a path, query or fragment.
let private address (name: string) (originOnly: bool) (value: string) =
    let parsed =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, found -> Option.ofObj found
        | _ -> None

    match parsed with
    | Some uri ->
        let secure = uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && loopback uri)

        if not secure then
            invalid $"'{name}' must be an https address"
        elif uri.UserInfo <> "" then
            invalid $"'{name}' must not hold user information"
        elif originOnly && (uri.AbsolutePath <> "/" || uri.Query <> "" || uri.Fragment <> "" || value.EndsWith "/") then
            invalid $"'{name}' must be an origin, with no path"
        elif not originOnly && (uri.Query <> "" || uri.Fragment <> "") then
            invalid $"'{name}' must not have a query or fragment"
        else
            Ok value
    | None -> invalid $"'{name}' is not an absolute address"

/// A short identifier: no white space or control characters.
let private identifier (name: string) (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length > 200 || value |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then
        invalid $"'{name}' is not an identifier"
    else
        Ok value

/// A display label: one line of visible text.
let private label (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length > 120 || value |> Seq.exists Char.IsControl then
        invalid "'label' is not one short line of text"
    else
        Ok value

let private locationOf (value: Json) =
    decoded (closed [ "basePath"; "branch"; "owner"; "repository" ] value)
    |> Result.bind (fun () ->
        match text "owner" value, text "repository" value, text "branch" value, text "basePath" value with
        | Ok owner, Ok repository, Ok branch, Ok basePath ->
            let config =
                { Owner = owner
                  Repository = repository
                  Branch = branch
                  BasePath = basePath }

            dataLocation config |> Result.map (fun _ -> config)
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> invalid e)

let private environmentOf =
    function
    | "local" -> Ok EnvironmentKind.Local
    | "test" -> Ok EnvironmentKind.Test
    | "staging" -> Ok EnvironmentKind.Staging
    | "production" -> Ok EnvironmentKind.Production
    | other -> invalid $"'{other}' is not local, test, staging or production"

let private identityOf (value: Json) =
    decoded (closed [ "application"; "clientId"; "exchange"; "provider"; "redirectUri" ] value)
    |> Result.bind (fun () ->
        match
            decoded (text "exchange" value) |> Result.bind (address "exchange" true),
            decoded (text "application" value) |> Result.bind (identifier "application"),
            decoded (text "provider" value),
            decoded (text "clientId" value) |> Result.bind (identifier "clientId"),
            decoded (text "redirectUri" value) |> Result.bind (address "redirectUri" false)
        with
        | Ok exchange, Ok application, Ok provider, Ok clientId, Ok redirectUri ->
            if List.contains provider identityProviders then
                Ok
                    { Exchange = exchange
                      Application = application
                      Provider = provider
                      ClientId = clientId
                      RedirectUri = redirectUri }
            else
                invalid $"'{provider}' is not an identity provider Signal signs in with"
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)

let private profileOf (value: Json) =
    decoded (closed [ "id"; "label"; "location"; "provider" ] value)
    |> Result.bind (fun () ->
        match
            decoded (text "id" value) |> Result.bind (identifier "id"),
            decoded (text "label" value) |> Result.bind label,
            decoded (text "provider" value),
            decoded (field "location" value) |> Result.bind locationOf
        with
        | Ok id, Ok label, Ok provider, Ok location ->
            if List.contains provider providers then
                Ok
                    { Id = id
                      Label = label
                      Provider = provider
                      Location = location }
            else
                invalid $"'{provider}' is not a storage provider this version of Signal offers"
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

/// GitHub numeric account ids: digits only, each once.
let private administratorsOf (value: Json) =
    match Json.field "administrators" value with
    | None -> Ok []
    | Some(Json.Array items) ->
        items
        |> traverse (function
            | Json.String id when id <> "" && id.Length <= 20 && id |> Seq.forall Char.IsAsciiDigit -> Ok id
            | _ -> Error "'administrators' holds something other than a GitHub account number")
        |> decoded
        |> Result.bind (fun ids ->
            if (List.distinct ids).Length <> ids.Length then
                invalid "'administrators' names an account twice"
            else
                Ok ids)
    | Some _ -> invalid "'administrators' is not a list"

/// A dataset id: a valid Arca dataset id, so it can name a folder.
let private datasetIdOf (id: string) =
    match DatasetId.create id with
    | Ok _ -> Ok id
    | Error _ -> Error(InvalidDatasetId id)

let private datasetOf (value: Json) =
    decoded (closed [ "administrators"; "id"; "label"; "profile" ] value)
    |> Result.bind (fun () ->
        match
            decoded (text "id" value) |> Result.bind datasetIdOf,
            decoded (text "label" value) |> Result.bind label,
            decoded (text "profile" value),
            administratorsOf value
        with
        | Ok id, Ok label, Ok profile, Ok administrators ->
            Ok
                { Id = id
                  Label = label
                  Profile = profile
                  Administrators = administrators }
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

let private many (name: string) (decode: Json -> Result<'a, Problem>) (key: 'a -> string) (value: Json) =
    match Json.field name value with
    | None -> Ok []
    | Some(Json.Array items) ->
        items
        |> List.fold (fun state item -> state |> Result.bind (fun found -> decode item |> Result.map (fun next -> found @ [ next ]))) (Ok [])
        |> Result.bind (fun found ->
            match found |> List.countBy key |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(id, _) -> invalid $"'{id}' is configured twice in '{name}'"
            | None -> Ok found)
    | Some _ -> invalid $"'{name}' is not a list"

/// The rules that hold across sections.
let private coherent (config: DeploymentConfig) =
    let unknownProfile =
        config.Datasets |> List.tryFind (fun found -> profile config found.Profile |> Option.isNone)

    match unknownProfile with
    | Some found ->
        // A profile cannot go while a dataset still lives in it (ADM-057).
        invalid $"dataset '{found.Id}' lives in profile '{found.Profile}', which is not configured"
    | None ->
        if not config.Datasets.IsEmpty && config.Profiles.IsEmpty then
            invalid "'datasets' need 'profiles'"
        elif not config.Profiles.IsEmpty && (config.Identity.IsNone || config.Datasets.IsEmpty) then
            // Stored data needs someone signed in to write it, and a dataset to keep it in.
            invalid "'profiles' need 'identity' and 'datasets'"
        else
            Ok config

/// A deployment's configuration from its JSON text, every value validated.
let parse (document: string) : Result<DeploymentConfig, Problem> =
    match Json.parse document with
    | Error error -> invalid (JsonError.describe error)
    | Ok value ->
        decoded (closed [ "datasets"; "environment"; "environmentName"; "identity"; "profiles" ] value)
        |> Result.bind (fun () ->
            match decoded (text "environment" value) |> Result.bind environmentOf, decoded (text "environmentName" value) with
            | Ok _, Ok name when String.IsNullOrWhiteSpace name -> Error(MissingConfiguration "environmentName")
            | Ok environment, Ok environmentName ->
                let identity =
                    match Json.field "identity" value with
                    | None -> Ok None
                    | Some found -> identityOf found |> Result.map Some

                match identity, many "profiles" profileOf _.Id value, many "datasets" datasetOf _.Id value with
                | Ok identity, Ok profiles, Ok datasets ->
                    coherent
                        { Environment = environment
                          EnvironmentName = environmentName
                          Identity = identity
                          Profiles = profiles
                          Datasets = datasets }
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e
            | Error e, _
            | _, Error e -> Error e)
