/// The templates the console can start groups from and resolve groups by
/// (WI-0073): the built-in pilot and the dataset's stored, published
/// templates that have the group pipeline's shape (`Pilot.assessmentOf`).
/// Hidden versions still resolve the groups that use them (AUT-006 §58) but
/// are not offered for new groups.
module Echelon.Signal.Application.TemplateCatalog

open System
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Application.Flow

/// The catalog as the console uses it.
[<NoComparison>]
type Loaded =
    { /// Offered for new groups.
      Offered: AdminApp.CatalogEntry list
      /// Every template a group may name.
      Resolvable: AdminApp.CatalogEntry list
      Listing: TemplateListing.Listing
      /// Each stored draft's revision, to save it at.
      Revisions: Map<string, Arca.Revision> }

let private entryOf (assessment: Assessment.Assessment) : AdminApp.CatalogEntry =
    { Hash = Canonical.templateHash assessment
      SurveyIdentifier = assessment.Id
      Version = assessment.Version
      Title = assessment.Title
      Content = assessment }

/// The built-in templates alone (no dataset open).
let builtIn (entries: AdminApp.CatalogEntry list) =
    { Offered = entries
      Resolvable = entries
      Listing = TemplateListing.empty
      Revisions = Map.empty }

/// The built-in templates and the stored catalog.
let combine (builtIns: AdminApp.CatalogEntry list) (stored: TemplateStore.StoredCatalog) : Loaded =
    let usable =
        stored.Catalog.Templates
        |> List.choose (fun t -> TemplateListing.assessmentOf t |> Result.toOption |> Option.map (fun a -> t, entryOf a))
        |> List.filter (fun (_, e) -> not (builtIns |> List.exists (fun b -> b.Hash = e.Hash)))

    { Offered = builtIns @ (usable |> List.filter (fun (t, _) -> Publication.visibility stored.Catalog t = Publication.Listed) |> List.map snd)
      Resolvable = builtIns @ (usable |> List.map snd)
      Listing = TemplateListing.ofCatalog stored.Catalog (stored.Drafts |> Map.toList |> List.map (snd >> fst))
      Revisions = stored.Drafts |> Map.map (fun _ (_, revision) -> revision) }

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
    { Templates =
        loaded.Resolvable
        |> List.map (fun e ->
            { SurveyId = e.SurveyIdentifier
              Version = e.Version
              Hash = e.Hash
              Content = Pilot.contentOf e.Content
              Parent = None
              PublishedAt = DateTimeOffset.UnixEpoch
              PublishedBy = "catalog"
              Manifest = []
              Fixtures = [] })
      Hidden = Set.empty }

/// A group's template by the hash its configuration records.
let resolver (loaded: Loaded) : GroupRecord.TemplateResolver =
    fun hash -> loaded.Resolvable |> List.tryFind (fun e -> e.Hash = hash) |> Option.map _.Content
