/// The templates the console can start groups from and resolve groups by
/// (WI-0073, WI-0078): the built-in pilot and every stored, published
/// template, which runs through the generic pipeline (`GenericImport`).
/// Hidden versions still resolve the groups that use them (AUT-006 §58) but
/// are not offered for new groups. Groups resolve their exact template
/// through `TemplateResolution` (ADM-055): the built-in catalog, then the
/// dataset's stored catalog, with the copies verified at the last good read
/// kept for when the store cannot be read.
module Echelon.Signal.Application.TemplateCatalog

open System
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Application.Flow

/// The catalog as the console uses it.
[<NoComparison; NoEquality>]
type Loaded =
    { /// Offered for new groups.
      Offered: AdminApp.CatalogEntry list
      /// Every template a group may name.
      Resolvable: AdminApp.CatalogEntry list
      Listing: TemplateListing.Listing
      /// Each stored draft's revision, to save it at.
      Revisions: Map<string, Arca.Revision>
      /// The registries resolution searches, in priority order (ADM-055).
      Registries: TemplateResolution.Registry list
      /// Stored templates verified at the last good read of the catalog.
      Cache: Publication.Published list }

/// The built-in catalog's name, as resolution reports it.
[<Literal>]
let BuiltInName = "the built-in catalog"

/// The stored catalog's name, as resolution reports it.
[<Literal>]
let StoredName = "the dataset's template catalog"

let private entryOf (t: Publication.Published) : AdminApp.CatalogEntry =
    let form = GenericImport.formOf t.SurveyId t.Version t.Content

    { Hash = form.Hash
      SurveyIdentifier = t.SurveyId
      Version = t.Version
      Title = t.Content.Metadata.Title
      Content = GenericImport.shapeOf t.SurveyId t.Version t.Content
      Generic = Some form }

/// A catalog entry as a published template, keyed by the hash its groups record.
let private publishedOf (e: AdminApp.CatalogEntry) : Publication.Published =
    { SurveyId = e.SurveyIdentifier
      Version = e.Version
      Hash = e.Hash
      Content = e.Generic |> Option.map _.Content |> Option.defaultValue (Pilot.contentOf e.Content)
      Parent = None
      PublishedAt = DateTimeOffset.UnixEpoch
      PublishedBy = "catalog"
      Manifest = []
      Fixtures = [] }

/// Templates compiled into the application: verified by construction.
let private builtInRegistry (entries: AdminApp.CatalogEntry list) : TemplateResolution.Registry =
    { Name = BuiltInName
      Revision = None
      State = TemplateResolution.Available
      Templates = entries |> List.map publishedOf
      Verify = fun _ -> true }

/// The dataset's stored catalog, as read now: every template re-hashed.
let private storedRegistry (state: TemplateResolution.RegistryState) (revision: string option) (templates: Publication.Published list) : TemplateResolution.Registry =
    { Name = StoredName
      Revision = revision
      State = state
      Templates = templates
      Verify = Publication.verify }

/// The built-in templates alone (no dataset open).
let builtIn (entries: AdminApp.CatalogEntry list) =
    { Offered = entries
      Resolvable = entries
      Listing = TemplateListing.empty
      Revisions = Map.empty
      Registries = [ builtInRegistry entries ]
      Cache = [] }

/// The built-in templates and the stored catalog.
let combine (builtIns: AdminApp.CatalogEntry list) (stored: TemplateStore.StoredCatalog) : Loaded =
    let usable =
        stored.Catalog.Templates
        |> List.map (fun t -> t, entryOf t)
        |> List.filter (fun (_, e) -> not (builtIns |> List.exists (fun b -> b.Hash = e.Hash)))

    { Offered = builtIns @ (usable |> List.filter (fun (t, _) -> Publication.visibility stored.Catalog t = Publication.Listed) |> List.map snd)
      Resolvable = builtIns @ (usable |> List.map snd)
      Listing = TemplateListing.ofCatalogReviewed stored.Catalog (stored.Drafts |> Map.toList |> List.map (snd >> fst)) (stored.Reviews |> Map.map (fun _ (r, _) -> r))
      Revisions = stored.Drafts |> Map.map (fun _ (_, revision) -> revision)
      Registries = [ builtInRegistry builtIns; storedRegistry TemplateResolution.Available (stored.CatalogRevision |> Option.map string) stored.Catalog.Templates ]
      Cache = stored.Catalog.Templates |> List.filter Publication.verify }

/// The catalog when the dataset's store could not be read: the built-in
/// templates, the stored registry marked as it failed, and the copies
/// verified at the last good read (ADM-055). New groups are offered only
/// what is known now.
let unreadable (builtIns: AdminApp.CatalogEntry list) (previous: Loaded) (failure: GroupStore.GroupFailure) : Loaded =
    let state =
        match failure with
        | GroupStore.NotPermitted refusal -> TemplateResolution.Unauthorized(Access.refusalCode refusal)
        | GroupStore.Offline meaning
        | GroupStore.Storage meaning -> TemplateResolution.Unavailable meaning.Code
        | other -> TemplateResolution.Unavailable $"%A{other}"

    { builtIn builtIns with
        Resolvable = builtIns @ (previous.Cache |> List.map entryOf |> List.filter (fun e -> not (builtIns |> List.exists (fun b -> b.Hash = e.Hash))))
        Registries = [ builtInRegistry builtIns; storedRegistry state None [] ]
        Cache = previous.Cache }

/// Reads the dataset's catalog beside the built-in templates.
let load (builtIns: AdminApp.CatalogEntry list) (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<Loaded, GroupStore.GroupFailure> =
    asyncResult {
        let! stored = TemplateStore.load now opened
        return combine builtIns stored
    }

/// The templates groups pin, as a publication catalog keyed by the hash each
/// group's configuration records: what a report snapshot resolves its
/// template pin against (WI-0075).
let pinned (loaded: Loaded) : Publication.Catalog =
    { Templates = loaded.Resolvable |> List.map publishedOf
      Hidden = Set.empty }

/// Why a found template cannot be used here, if it cannot: it declares a
/// capability this engine does not support.
let private unsupported (t: Publication.Published) =
    match t.Content.Compatibility.Capabilities |> List.filter (Template.supportedCapabilities.Contains >> not) with
    | [] -> None
    | caps -> Some $"""it declares {caps |> List.map TemplateCanonical.capabilityName |> String.concat ", "}, which this engine does not support"""

/// Exact resolution of a template by its canonical hash (ADM-055).
let resolution (loaded: Loaded) (hash: string) =
    TemplateResolution.resolve loaded.Registries loaded.Cache unsupported (TemplateResolution.ByHash hash)

/// A group's template by the hash its configuration records, only when
/// resolution established it exactly; otherwise why not.
let resolver (loaded: Loaded) : GroupRecord.TemplateResolver =
    fun hash ->
        let outcome = resolution loaded hash

        match TemplateResolution.usable outcome with
        | Some t ->
            let entry = loaded.Resolvable |> List.tryFind (fun e -> e.Hash = hash) |> Option.defaultValue (entryOf t)
            Ok(entry.Content, entry.Generic)
        | None -> Error(TemplateResolution.describe (TemplateResolution.ByHash hash) outcome)
