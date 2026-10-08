/// Published artifacts, the catalog, the derived lifecycle and publication
/// (AUT-005 §§52-55, AUT-006 §§56-59, CAN-005 §29, ARX-002).
///
/// Publication is a pure function from a draft and the catalog of what is
/// already published to a new immutable artifact and the next catalog, or a
/// refusal that says exactly why. Nothing is stored here: persistence of the
/// catalog is `TemplateStore`'s job (WI-0057), and it stores these values
/// unchanged.
module Echelon.Signal.Engine.Publication

open System
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Selectors
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Layout
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Findings
open Echelon.Signal.Engine.Validation
open Echelon.Signal.Engine.TemplateDiff

type ManifestEntry =
    { FixtureId: string
      ExpectedResultHash: string
      PassedAtPublication: bool }

/// An immutable published template. The record has no update function in
/// this module; a change is a new draft and a new version.
type Published =
    { SurveyId: string
      /// Assigned by publication: "1", "2", ... per survey identifier.
      Version: string
      Hash: string
      Content: Content
      /// Provenance, outside the hash (AUT-006 §70).
      Parent: ParentReference option
      PublishedAt: DateTimeOffset
      PublishedBy: string
      Manifest: ManifestEntry list
      Fixtures: Fixture list }

type Visibility =
    | Listed
    /// Hidden from new authoring and distribution; still resolvable,
    /// scorable and reportable (AUT-006 §58).
    | HiddenFromDistribution

type Catalog =
    { Templates: Published list
      Hidden: Set<string * string> }

let emptyCatalog = { Templates = []; Hidden = Set.empty }

let versionsOf (catalog: Catalog) (surveyId: string) =
    catalog.Templates |> List.filter (fun t -> t.SurveyId = surveyId) |> List.sortBy (fun t -> int t.Version)

let resolve (catalog: Catalog) (surveyId: string) (version: string) =
    catalog.Templates |> List.tryFind (fun t -> t.SurveyId = surveyId && t.Version = version)

let resolveHash (catalog: Catalog) (hash: string) =
    catalog.Templates |> List.tryFind (fun t -> t.Hash = hash)

/// The template a URL's compact reference names, if exactly one does.
let resolveReference (catalog: Catalog) (reference: byte[]) =
    match
        catalog.Templates
        |> List.filter (fun t -> TemplateCanonical.reference t.SurveyId t.Version t.Content = reference)
    with
    | [ single ] -> Some single
    | _ -> None

/// The lifecycle state (AUT-001 §2), derived, never stored: a draft is
/// Validated while its current content passes; a published version is
/// Superseded once a later version of the same survey exists.
type Lifecycle =
    | DraftState
    | ValidatedState
    | PublishedState
    | SupersededState

let draftState (policy: Policy) (draft: Draft) =
    if (validate policy draft).Passes then ValidatedState else DraftState

let publishedState (catalog: Catalog) (template: Published) =
    if versionsOf catalog template.SurveyId |> List.exists (fun t -> int t.Version > int template.Version) then
        SupersededState
    else
        PublishedState

/// What may be done in each state (ARX-002: capabilities are derived from
/// state, not granted separately).
type Action =
    | Edit
    | Validate
    | Publish
    | DeleteDraft
    | DeriveDraft
    | Hide

let allowedActions =
    function
    | DraftState -> [ Edit; Validate; DeleteDraft ]
    | ValidatedState -> [ Edit; Validate; Publish; DeleteDraft ]
    | PublishedState -> [ DeriveDraft; Hide ]
    | SupersededState -> [ DeriveDraft; Hide ]

/// A new draft from a published version: a new version, or a rollback to an
/// older design published as a new version (AUT-001 §8, AUT-006 §57).
let deriveDraft (template: Published) : Draft =
    { SurveyId = template.SurveyId
      Parent = Some { Version = template.Version; Hash = template.Hash }
      Content = template.Content
      Fixtures = template.Fixtures }

let hide (template: Published) (catalog: Catalog) =
    { catalog with Hidden = catalog.Hidden.Add(template.SurveyId, template.Version) }

let visibility (catalog: Catalog) (template: Published) =
    if catalog.Hidden.Contains(template.SurveyId, template.Version) then HiddenFromDistribution else Listed

type Refusal =
    /// Validation blockers, with the full report.
    | Blocked of ValidationReport
    /// Warnings the publisher did not acknowledge (AUT-005 §51).
    | UnacknowledgedWarnings of Finding list
    /// The draft's parent is not a published version of this survey.
    | UnknownParent of ParentReference
    /// The content is identical to an already published version.
    | Unchanged of version: string

/// The publication summary shown before confirming (AUT-005 §55).
type PublicationSummary =
    { SurveyId: string
      NewVersion: string
      ParentVersion: string option
      TemplateHash: string
      LayoutFingerprint: string
      QuestionCount: int
      SectionCount: int
      MaximumUrlCharacters: int
      BreakingScoringChanges: bool
      EncodingChanges: bool
      Comparability: Comparability option }

let private nextVersion (catalog: Catalog) (surveyId: string) =
    match versionsOf catalog surveyId with
    | [] -> "1"
    | versions -> string (int (List.last versions).Version + 1)

let private parentOf (catalog: Catalog) (draft: Draft) =
    match draft.Parent with
    | None -> Ok None
    | Some p ->
        match resolve catalog draft.SurveyId p.Version with
        | Some t when t.Hash = p.Hash -> Ok(Some t)
        | _ -> Error(UnknownParent p)

/// What publishing the draft now would produce, without publishing it.
let preview (policy: Policy) (catalog: Catalog) (draft: Draft) : Result<PublicationSummary, Refusal> =
    parentOf catalog draft
    |> Result.map (fun parent ->
        let version = nextVersion catalog draft.SurveyId
        let changes = parent |> Option.map (fun p -> diff p.Content draft.Content)

        { SurveyId = draft.SurveyId
          NewVersion = version
          ParentVersion = parent |> Option.map _.Version
          TemplateHash = TemplateCanonical.templateHash draft.SurveyId version draft.Content
          LayoutFingerprint = TemplateCanonical.layoutFingerprint draft.Content
          QuestionCount = (questions draft.Content).Length
          SectionCount = draft.Content.Sections.Length
          MaximumUrlCharacters = (capacity draft.Content).EncodedCharacters + policy.BaseUrlAllowance
          BreakingScoringChanges = changes |> Option.exists (List.exists (fun c -> (impact c).Scoring))
          EncodingChanges = changes |> Option.exists (List.exists (fun c -> (impact c).Encoding))
          Comparability = changes |> Option.map comparability })

/// Publishes a draft (CAN-005 §29 sequence: validate, test, check capacity
/// and privacy, compare to the parent, canonicalize, hash, assign the
/// version, lock). `acknowledged` names the warning codes the publisher
/// accepted. The returned catalog holds the new version; the previous
/// latest version becomes Superseded by derivation, and nothing is removed.
let publish
    (policy: Policy)
    (catalog: Catalog)
    (acknowledged: Set<string>)
    (publishedAt: DateTimeOffset)
    (publishedBy: string)
    (draft: Draft)
    : Result<Published * Catalog, Refusal> =
    let report = validate policy draft

    if not report.Passes then
        Error(Blocked report)
    else
        match report.Warnings |> List.filter (fun w -> not (acknowledged.Contains w.Code)) with
        | _ :: _ as unacknowledged -> Error(UnacknowledgedWarnings unacknowledged)
        | [] ->
            match versionsOf catalog draft.SurveyId |> List.tryFind (fun t -> t.Content = draft.Content) with
            | Some same -> Error(Unchanged same.Version)
            | None ->
                parentOf catalog draft
                |> Result.map (fun _ ->
                    let version = nextVersion catalog draft.SurveyId

                    let published =
                        { SurveyId = draft.SurveyId
                          Version = version
                          Hash = TemplateCanonical.templateHash draft.SurveyId version draft.Content
                          Content = draft.Content
                          Parent = draft.Parent
                          PublishedAt = publishedAt
                          PublishedBy = publishedBy
                          Manifest =
                            report.Fixtures
                            |> List.map (fun r ->
                                { FixtureId = r.FixtureId
                                  ExpectedResultHash = r.ResultHash
                                  PassedAtPublication = r.Failures.IsEmpty })
                          Fixtures = draft.Fixtures }

                    published, { catalog with Templates = catalog.Templates @ [ published ] })

/// Re-derives a published artifact's hash from its content: true when the
/// artifact is exactly what was published (AUT-006 §54, hash lock).
let verify (template: Published) =
    TemplateCanonical.templateHash template.SurveyId template.Version template.Content = template.Hash
