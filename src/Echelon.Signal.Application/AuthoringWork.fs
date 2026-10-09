/// The authoring screens' work (WI-0073): saving a draft at the revision it
/// was read at, publishing it, and hiding a version, each followed by
/// rereading the catalog so the page and new groups see what is stored.
module Echelon.Signal.Application.AuthoringWork

open System
open Echelon.Signal.Engine
open Echelon.Signal.Admin
open Echelon.Signal.Application.AdminWork

let private reloaded (env: Env) (now: DateTimeOffset) (opened: Store.Opened) (messages: AdminApp.Msg list) =
    async {
        match! TemplateCatalog.load env.Catalog now opened with
        | Ok catalog -> return CatalogReady catalog :: (messages |> List.map ToEngine)
        | Error failure -> return (messages |> List.map ToEngine) @ [ ToEngine(groupFailure failure) ]
    }

let private storeFailure (failure: GroupStore.GroupFailure) =
    match failure with
    | GroupStore.Storage meaning when meaning.Code.Contains "CONFLICT" ->
        AdminApp.Failed(notice "SIGNAL.STORAGE.CONFLICT" "The draft changed elsewhere since it was opened; it was not saved. Reopen it and edit again." false)
    | other -> groupFailure other

/// Stores a draft at the revision the catalog read it at (None: a new draft).
let save (env: Env) (catalog: TemplateCatalog.Loaded) (actor: Store.Actor) (opened: Store.Opened) (draft: Drafts.Draft) (now: DateTimeOffset) =
    async {
        let revision = catalog.Revisions |> Map.tryFind draft.SurveyId

        match! TemplateStore.saveDraft actor now opened revision draft with
        | Ok() -> return! reloaded env now opened [ AdminApp.DraftSaved draft.SurveyId; AdminApp.Noted(notice "SIGNAL.AUTHORING.SAVED" $"The draft of {draft.SurveyId} was saved." false) ]
        | Error failure -> return [ ToEngine(storeFailure failure) ]
    }

let private refusal (refusal: Publication.Refusal) =
    match refusal with
    | Publication.Blocked report -> "Publication is blocked: " + (report.Blockers |> List.map _.Code |> String.concat ", ") + "."
    | Publication.UnacknowledgedWarnings warnings -> "Acknowledge the warnings first: " + (warnings |> List.map _.Code |> String.concat ", ") + "."
    | Publication.UnknownParent parent -> $"The draft's parent version {parent.Version} is not published."
    | Publication.Unchanged version -> $"Nothing changed since version {version}; there is nothing to publish."

/// Publishes a draft as the survey's next version.
let publish (env: Env) (actor: Store.Actor) (opened: Store.Opened) (draft: Drafts.Draft) (acknowledged: Set<string>) (now: DateTimeOffset) =
    async {
        match! TemplateStore.publishReviewed actor Validation.defaultPolicy acknowledged now opened draft with
        | Ok published ->
            return!
                reloaded env now opened [ AdminApp.DraftPublished(published.SurveyId, published.Version); AdminApp.Noted(notice "SIGNAL.AUTHORING.PUBLISHED" $"{published.SurveyId} version {published.Version} is published ({published.Hash})." false) ]
        | Error(TemplateStore.Refused r) -> return [ ToEngine(AdminApp.Failed(notice "SIGNAL.AUTHORING.REFUSED" (refusal r) false)) ]
        | Error(TemplateStore.NotStored failure) -> return [ ToEngine(storeFailure failure) ]
    }

let private reviewFailure (failure: TemplateStore.ReviewFailure) =
    match failure with
    | TemplateStore.NoStoredDraft s -> AdminApp.Failed(notice "SIGNAL.AUTHORING.NO_DRAFT" $"There is no stored draft of {s}; save it first." false)
    | TemplateStore.DraftChanged s -> AdminApp.Failed(notice "SIGNAL.AUTHORING.DRAFT_CHANGED" $"The stored draft of {s} changed since it was opened; reopen it and review again." false)
    | TemplateStore.NotReadyForReview s -> AdminApp.Failed(notice "SIGNAL.AUTHORING.NOT_READY" $"The draft of {s} has not been submitted for review as it is now." false)
    | TemplateStore.ReviewNotStored f -> storeFailure f

/// The author submits the stored draft for review.
let requestReview (env: Env) (actor: Store.Actor) (opened: Store.Opened) (survey: string) (now: DateTimeOffset) =
    async {
        match! TemplateStore.requestReview actor now opened survey with
        | Ok _ -> return! reloaded env now opened [ AdminApp.Noted(notice "SIGNAL.AUTHORING.REVIEW_REQUESTED" $"The draft of {survey} is ready for review." false) ]
        | Error failure -> return [ ToEngine(reviewFailure failure) ]
    }

/// A reviewer approves the stored draft as they see it.
let approveReview (env: Env) (actor: Store.Actor) (opened: Store.Opened) (draft: Drafts.Draft) (now: DateTimeOffset) =
    async {
        match! TemplateStore.approveReview actor now opened draft.SurveyId draft with
        | Ok _ -> return! reloaded env now opened [ AdminApp.Noted(notice "SIGNAL.AUTHORING.REVIEWED" $"The draft of {draft.SurveyId} is reviewed and can be published." false) ]
        | Error failure -> return [ ToEngine(reviewFailure failure) ]
    }

/// Hides a published version from new groups; it still resolves the groups that use it.
let hide (env: Env) (actor: Store.Actor) (opened: Store.Opened) (survey: string) (version: string) (now: DateTimeOffset) =
    async {
        match! TemplateStore.hide actor now opened survey version with
        | Ok() -> return! reloaded env now opened [ AdminApp.Noted(notice "SIGNAL.AUTHORING.HIDDEN" $"{survey} version {version} is hidden from new groups; groups that use it are unaffected." false) ]
        | Error failure -> return [ ToEngine(storeFailure failure) ]
    }
