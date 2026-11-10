/// What the console shows about the dataset's stored template catalog
/// (WI-0073): every published version with its visibility and whether it can
/// start groups, and the drafts. Pure.
module Echelon.Signal.Admin.TemplateListing

open Echelon.Signal.Engine
open Echelon.Signal.Engine.Publication

/// Where a stored draft's review stands (AUT-006 §§64-65).
type ReviewState =
    | NotSubmitted
    /// It was submitted, and the stored draft changed since.
    | ChangedSinceSubmitted
    | ReadyForReview
    | Reviewed

/// Where a template version stands in its lifecycle: a draft, a draft in
/// review or reviewed, a published version, or one hidden from new groups.
[<RequireQualifiedAccess>]
type Lifecycle =
    | Draft
    | InReview
    | ReviewedDraft
    | Published
    | Hidden

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
      Reviews: Map<string, string>
      /// The same, typed: what the chips and lifecycle read (SIG-VIS-PENDING-001).
      ReviewStates: Map<string, ReviewState> }

let empty =
    { Published = []
      Drafts = []
      Catalog = emptyCatalog
      DraftsById = Map.empty
      Reviews = Map.empty
      ReviewStates = Map.empty }

/// The assessment a published version starts groups with, if it can.
let assessmentOf (template: Published) = Pilot.assessmentOf template.SurveyId template.Version template.Content

/// What a review means for the draft as stored now.
let reviewState (draft: Drafts.Draft) (review: TemplateRecord.StoredReview option) =
    match review with
    | None -> NotSubmitted
    | Some r when r.ContentHash <> TemplateRecord.draftContentHash draft -> ChangedSinceSubmitted
    | Some { State = TemplateRecord.ReadyForReview } -> ReadyForReview
    | Some { State = TemplateRecord.Reviewed _ } -> Reviewed

let reviewText (state: ReviewState) =
    match state with
    | NotSubmitted -> "Not submitted for review"
    | ChangedSinceSubmitted -> "Changed since it was submitted for review"
    | ReadyForReview -> "Ready for review"
    | Reviewed -> "Reviewed"

/// What a review means for the draft as stored now, in words.
let reviewStatus (draft: Drafts.Draft) (review: TemplateRecord.StoredReview option) = reviewText (reviewState draft review)

/// A stored draft's lifecycle, from its review as stored now: a draft that
/// changed since it was submitted is a draft again.
let draftLifecycle (state: ReviewState) =
    match state with
    | NotSubmitted
    | ChangedSinceSubmitted -> Lifecycle.Draft
    | ReadyForReview -> Lifecycle.InReview
    | Reviewed -> Lifecycle.ReviewedDraft

/// A published version's lifecycle, from its visibility.
let versionLifecycle (row: Row) = if row.Hidden then Lifecycle.Hidden else Lifecycle.Published

/// The lifecycle of a survey's draft as listed: an unlisted draft (not yet
/// stored) is a draft.
let lifecycleOfDraft (listing: Listing) (surveyId: string) =
    listing.ReviewStates |> Map.tryFind surveyId |> Option.map draftLifecycle |> Option.defaultValue Lifecycle.Draft

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
              // Every published template runs through the generic pipeline (WI-0078).
              NotForGroups = None })
      Drafts = drafts |> List.map (fun d -> d.SurveyId, d.Content.Metadata.Title) |> List.sort
      Catalog = catalog
      DraftsById = drafts |> List.map (fun d -> d.SurveyId, d) |> Map.ofList
      Reviews = Map.empty
      ReviewStates = Map.empty }

/// The listing with each draft's review status.
let ofCatalogReviewed (catalog: Catalog) (drafts: Drafts.Draft list) (reviews: Map<string, TemplateRecord.StoredReview>) : Listing =
    let states = drafts |> List.map (fun d -> d.SurveyId, reviewState d (reviews |> Map.tryFind d.SurveyId)) |> Map.ofList
    { listingOf catalog drafts with Reviews = states |> Map.map (fun _ state -> reviewText state); ReviewStates = states }

let ofCatalog (catalog: Catalog) (drafts: Drafts.Draft list) : Listing = ofCatalogReviewed catalog drafts Map.empty
