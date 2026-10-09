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
        match! TemplateStore.publish actor Validation.defaultPolicy acknowledged now opened draft with
        | Ok published ->
            return!
                reloaded env now opened [ AdminApp.DraftPublished(published.SurveyId, published.Version); AdminApp.Noted(notice "SIGNAL.AUTHORING.PUBLISHED" $"{published.SurveyId} version {published.Version} is published ({published.Hash})." false) ]
        | Error(TemplateStore.Refused r) -> return [ ToEngine(AdminApp.Failed(notice "SIGNAL.AUTHORING.REFUSED" (refusal r) false)) ]
        | Error(TemplateStore.NotStored failure) -> return [ ToEngine(storeFailure failure) ]
    }

/// Hides a published version from new groups; it still resolves the groups that use it.
let hide (env: Env) (actor: Store.Actor) (opened: Store.Opened) (survey: string) (version: string) (now: DateTimeOffset) =
    async {
        match! TemplateStore.hide actor now opened survey version with
        | Ok() -> return! reloaded env now opened [ AdminApp.Noted(notice "SIGNAL.AUTHORING.HIDDEN" $"{survey} version {version} is hidden from new groups; groups that use it are unaffected." false) ]
        | Error failure -> return [ ToEngine(storeFailure failure) ]
    }
