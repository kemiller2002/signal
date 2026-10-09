/// What the console shows about the dataset's stored template catalog
/// (WI-0073): every published version with its visibility and whether it can
/// start groups, and the drafts. Pure.
module Echelon.Signal.Admin.TemplateListing

open Echelon.Signal.Engine
open Echelon.Signal.Engine.Publication

/// One published version.
type Row =
    { SurveyId: string
      Version: string
      Hash: string
      Title: string
      Hidden: bool
      /// A later version of the same survey exists.
      Superseded: bool
      /// Why it cannot start groups, if it cannot.
      NotForGroups: string option }

/// The catalog as the console lists it, and what the authoring screens edit.
type Listing =
    { Published: Row list
      /// Survey id and title of each draft.
      Drafts: (string * string) list
      Catalog: Catalog
      DraftsById: Map<string, Drafts.Draft>
      /// Each draft's review as it stands for the stored draft (AUT-006 §65).
      Reviews: Map<string, string> }

let empty =
    { Published = []
      Drafts = []
      Catalog = emptyCatalog
      DraftsById = Map.empty
      Reviews = Map.empty }

/// The assessment a published version starts groups with, if it can.
let assessmentOf (template: Published) = Pilot.assessmentOf template.SurveyId template.Version template.Content

/// What a review means for the draft as stored now.
let reviewStatus (draft: Drafts.Draft) (review: TemplateRecord.StoredReview option) =
    match review with
    | None -> "Not submitted for review"
    | Some r when r.ContentHash <> TemplateRecord.draftContentHash draft -> "Changed since it was submitted for review"
    | Some { State = TemplateRecord.ReadyForReview } -> "Ready for review"
    | Some { State = TemplateRecord.Reviewed _ } -> "Reviewed"

let private listingOf (catalog: Catalog) (drafts: Drafts.Draft list) : Listing =
    let latest surveyId = versionsOf catalog surveyId |> List.tryLast |> Option.map _.Version

    { Published =
        catalog.Templates
        |> List.sortBy (fun t -> t.SurveyId, int t.Version)
        |> List.map (fun t ->
            { SurveyId = t.SurveyId
              Version = t.Version
              Hash = t.Hash
              Title = t.Content.Metadata.Title
              Hidden = visibility catalog t = HiddenFromDistribution
              Superseded = latest t.SurveyId <> Some t.Version
              NotForGroups =
                match assessmentOf t with
                | Ok _ -> None
                | Error reason -> Some reason })
      Drafts = drafts |> List.map (fun d -> d.SurveyId, d.Content.Metadata.Title) |> List.sort
      Catalog = catalog
      DraftsById = drafts |> List.map (fun d -> d.SurveyId, d) |> Map.ofList
      Reviews = Map.empty }

/// The listing with each draft's review status.
let ofCatalogReviewed (catalog: Catalog) (drafts: Drafts.Draft list) (reviews: Map<string, TemplateRecord.StoredReview>) : Listing =
    { listingOf catalog drafts with Reviews = drafts |> List.map (fun d -> d.SurveyId, reviewStatus d (reviews |> Map.tryFind d.SurveyId)) |> Map.ofList }

let ofCatalog (catalog: Catalog) (drafts: Drafts.Draft list) : Listing = ofCatalogReviewed catalog drafts Map.empty
