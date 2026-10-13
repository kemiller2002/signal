/// Exact template resolution (ADM-055, WI-0081): every outcome the
/// requirement names, negative knowledge scoped to the registries and
/// revisions searched, a hash mismatch and a conflict that fail resolution,
/// a verified copy used only when a registry cannot be read, and never
/// "latest" in place of the exact version asked for.
module Echelon.Signal.Tests.TemplateResolutionTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Publication
open Echelon.Signal.Admin
open Echelon.Signal.Admin.TemplateResolution

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private draft = SurveyPageTests.mixedEditor.Draft
let private acknowledged (d: Drafts.Draft) = (Validation.validate Validation.defaultPolicy d).Warnings |> List.map _.Code |> Set.ofList

let private v1, catalog1 = publish Validation.defaultPolicy emptyCatalog (acknowledged draft) DateTimeOffset.UnixEpoch "author" draft |> ok

let private v2 =
    let child = { deriveDraft v1 with Content = { v1.Content with Metadata = { v1.Content.Metadata with Title = "Mixed kinds, revised" } } }
    publish Validation.defaultPolicy catalog1 (acknowledged child) DateTimeOffset.UnixEpoch "author" child |> ok |> fst

let private registry name state templates : Registry =
    { Name = name; Revision = Some "rev-7"; State = state; Templates = templates; Verify = Publication.verify }

let private stored templates = registry "the dataset's template catalog" Available templates
let private builtIn = { registry "the built-in catalog" Available [] with Revision = None; Verify = fun _ -> true }
let private none _ = None
let private resolveIn registries = resolve registries [] none

[<Fact>]
let ``an exact template is resolved by hash or by survey and version, from the registry that holds it`` () =
    match resolveIn [ builtIn; stored [ v1; v2 ] ] (ByHash v1.Hash) with
    | ResolvedExact(t, registry) ->
        Assert.Equal(v1, t)
        Assert.Equal("the dataset's template catalog", registry)
    | other -> failwith $"%A{other}"

    Assert.Equal(Some v2, usable (resolveIn [ builtIn; stored [ v1; v2 ] ] (ByVersion(v2.SurveyId, v2.Version))))

[<Fact>]
let ``not found says which registries and revisions were searched, and is never answered with another version`` () =
    // Only version 2 is held: asking for version 1 is not answered with 2.
    let outcome = resolveIn [ builtIn; stored [ v2 ] ] (ByVersion(v1.SurveyId, v1.Version))
    Assert.Equal(NotFoundInRegistry [ "the built-in catalog", None; "the dataset's template catalog", Some "rev-7" ], outcome)
    Assert.Equal(None, usable outcome)

    Assert.Equal(
        "MIXED 1 was searched for in the built-in catalog and the dataset's template catalog at rev-7 and is not there.",
        describe (ByVersion(v1.SurveyId, v1.Version)) outcome
    )

[<Fact>]
let ``a registry that cannot be read or refuses access is said so, unless a verified copy is held`` () =
    let unavailable = registry "the dataset's template catalog" (Unavailable "SIGNAL.NETWORK.OFFLINE") []
    let refused = registry "the dataset's template catalog" (Unauthorized "SIGNAL.ACCESS.NOT_A_MEMBER") []
    Assert.Equal(RegistryUnavailable("the dataset's template catalog", "SIGNAL.NETWORK.OFFLINE"), resolveIn [ builtIn; unavailable ] (ByHash v1.Hash))
    Assert.Equal(RegistryUnauthorized("the dataset's template catalog", "SIGNAL.ACCESS.NOT_A_MEMBER"), resolveIn [ builtIn; refused ] (ByHash v1.Hash))

    // A copy verified at the last good read is used, and says why.
    match resolve [ builtIn; unavailable ] [ v1 ] none (ByHash v1.Hash) with
    | CachedVerifiedCopyAvailable(t, reason) as outcome ->
        Assert.Equal(v1, t)
        Assert.Contains("could not be read", reason)
        Assert.Equal(Some v1, usable outcome)
    | other -> failwith $"%A{other}"

    // A cached copy that no longer hashes to its identity is not used.
    let tampered = { v1 with Content = { v1.Content with Metadata = { v1.Content.Metadata with Title = "Tampered" } } }
    Assert.Equal(RegistryUnavailable("the dataset's template catalog", "SIGNAL.NETWORK.OFFLINE"), resolve [ builtIn; unavailable ] [ tampered ] none (ByHash v1.Hash))

[<Fact>]
let ``a template whose content does not hash to its identity fails resolution`` () =
    let tampered = { v1 with Content = { v1.Content with Metadata = { v1.Content.Metadata with Title = "Tampered" } } }
    let outcome = resolveIn [ builtIn; stored [ tampered ] ] (ByHash v1.Hash)
    Assert.Equal(HashMismatch("the dataset's template catalog", v1.Hash), outcome)
    Assert.Equal(None, usable outcome)

[<Fact>]
let ``two artifacts claiming the same survey and version conflict, and neither is used`` () =
    let impostor = { v2 with Version = v1.Version }
    let other = { registry "a mirror" Available [ impostor ] with Verify = fun _ -> true }

    for request in [ ByVersion(v1.SurveyId, v1.Version); ByHash v1.Hash ] do
        match resolveIn [ builtIn; stored [ v1 ]; other ] request with
        | ConflictingArtifacts claims ->
            Assert.Contains(("the dataset's template catalog", v1.Hash), claims)
            Assert.Contains(("a mirror", impostor.Hash), claims)
        | outcome -> failwith $"%A{request}: %A{outcome}"

[<Fact>]
let ``a template this engine cannot run is found and refused, and a partial read proves nothing`` () =
    let refuse (t: Published) = if t.Hash = v1.Hash then Some "it declares UsesGroupScoring" else None

    match resolve [ builtIn; stored [ v1 ] ] [] refuse (ByHash v1.Hash) with
    | UnsupportedTemplateVersion(t, reason) ->
        Assert.Equal(v1, t)
        Assert.Contains("UsesGroupScoring", reason)
    | other -> failwith $"%A{other}"

    let partial = registry "the dataset's template catalog" (Incomplete "the listing was cut short") []

    match resolveIn [ builtIn; partial ] (ByHash v1.Hash) with
    | ResolutionUnknown reason -> Assert.Contains("read only in part", reason)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the console's catalog resolves through the registries, and keeps verified copies when the store cannot be read`` () =
    let stored: Echelon.Signal.Application.TemplateStore.StoredCatalog =
        { Catalog = catalog1; CatalogRevision = None; Drafts = Map.empty; Reviews = Map.empty; Problems = [] }

    let loaded = Echelon.Signal.Application.TemplateCatalog.combine [] stored

    match Echelon.Signal.Application.TemplateCatalog.resolution loaded v1.Hash with
    | ResolvedExact(t, registry) ->
        Assert.Equal(v1.Hash, t.Hash)
        Assert.Equal(Echelon.Signal.Application.TemplateCatalog.StoredName, registry)
    | other -> failwith $"%A{other}"

    // Offline: the stored registry is unavailable, and the copy verified at
    // the last good read resolves the group, saying so.
    let offline = Echelon.Signal.Application.GroupStore.failed DateTimeOffset.UnixEpoch (Arca.StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "offline"))
    let unreadable = Echelon.Signal.Application.TemplateCatalog.unreadable [] loaded offline

    match Echelon.Signal.Application.TemplateCatalog.resolution unreadable v1.Hash with
    | CachedVerifiedCopyAvailable(t, reason) ->
        Assert.Equal(v1.Hash, t.Hash)
        Assert.Contains("the dataset's template catalog could not be read", reason)
    | other -> failwith $"%A{other}"

    Assert.True(Echelon.Signal.Application.TemplateCatalog.resolver unreadable v1.Hash |> Result.isOk)

    // Without a copy it is said plainly, never guessed.
    let cold = Echelon.Signal.Application.TemplateCatalog.unreadable [] (Echelon.Signal.Application.TemplateCatalog.builtIn []) offline

    match Echelon.Signal.Application.TemplateCatalog.resolver cold v1.Hash with
    | Error reason -> Assert.Contains("could not be read", reason)
    | Ok _ -> failwith "resolved without a verified copy"
