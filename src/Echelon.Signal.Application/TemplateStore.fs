/// The template catalog through Arca (WI-0057, AUT-005, AUT-006, VER-001,
/// VER-007): drafts are saved, published templates are stored once and never
/// changed, and the catalog's visibility is one record, all in the dataset's
/// namespace and under the dataset's roster.
///
/// - **Saving a draft** needs `EditDrafts`; it replaces the survey's draft at
///   the revision it was read at, so a concurrent edit is a conflict, never a
///   lost write.
/// - **Publishing** needs `PublishTemplates`. It is `Publication.publish`
///   over the stored catalog, committed as one `Create` of the immutable
///   template record, so a version is never published twice.
/// - **Hiding** a version needs `PublishTemplates`; the template stays
///   resolvable for groups that use it (AUT-006 §58).
/// - Every record is read as untrusted input: a published template whose
///   bytes do not hash to its recorded hash is a problem, never a template.
module Echelon.Signal.Application.TemplateStore

open System
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Publication
open Echelon.Signal.Admin
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Application.Flow
open Echelon.Signal.Application.GroupStore

/// The stored catalog, the drafts and the revisions to update them at.
[<NoComparison; NoEquality>]
type StoredCatalog =
    { Catalog: Catalog
      CatalogRevision: Revision option
      Drafts: Map<string, Draft * Revision>
      Problems: Problem list }

let private loadAll now (opened: Store.Opened) (reader: Loading.RecordReader<'a>) (recordType: RecordType) =
    asyncResult {
        let folder = TemplateRecord.folderOf recordType
        let! listing, objects = readTree now opened.Provider opened.Namespace folder
        return Loading.load opened.Verified reader folder listing objects
    }

/// Reads the dataset's catalog and drafts.
let load (now: DateTimeOffset) (opened: Store.Opened) : AsyncResult<StoredCatalog, GroupFailure> =
    asyncResult {
        let! published = loadAll now opened TemplateRecord.publishedReader TemplateRecord.publishedType
        let! drafts = loadAll now opened TemplateRecord.draftReader TemplateRecord.draftType
        let! visibility = loadAll now opened TemplateRecord.catalogReader TemplateRecord.catalogType
        let hidden = visibility.Records |> Map.tryFind TemplateRecord.CatalogId

        return
            { Catalog = TemplateRecord.catalogOf (published.Records |> Map.toList |> List.map (snd >> _.Value)) (hidden |> Option.map _.Value.Hidden |> Option.defaultValue Set.empty)
              CatalogRevision = hidden |> Option.map _.Revision
              Drafts = drafts.Records |> Map.toList |> List.map (fun (_, d) -> d.Value.Draft.SurveyId, (d.Value.Draft, d.Revision)) |> Map.ofList
              Problems = published.Problems @ drafts.Problems @ visibility.Problems }
    }

let private commit now (opened: Store.Opened) (actor: Store.Actor) (summary: string) (changes: Result<Change list, Problem>) =
    asyncResult {
        let! operation = changes |> Result.bind (fun c -> Storage.operation opened.Namespace (actor.NewContext()) summary c |> Result.mapError List.head) |> Result.mapError (List.singleton >> Unusable) |> lift
        let! _ = call now (opened.Provider.Commit operation)
        return ()
    }

/// Saves a survey's draft at the revision it was read at (None: a new draft).
let saveDraft (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (revision: Revision option) (draft: Draft) : AsyncResult<unit, GroupFailure> =
    asyncResult {
        let! _ = permitted opened actor Access.EditDrafts |> lift

        let changes =
            match TemplateRecord.draftPath draft.SurveyId, TemplateRecord.encodeDraft opened.DatasetId draft with
            | Ok path, Ok content -> Ok [ (match revision with Some r -> Change.Update(path, content, r) | None -> Change.Create(path, content)) ]
            | Error p, _
            | _, Error p -> Error p

        return! commit now opened actor $"save draft {draft.SurveyId}" changes
    }

/// Why publishing did not happen: the domain's refusal, or storage.
type PublishFailure =
    | Refused of Refusal
    | NotStored of GroupFailure

/// Publishes a draft into the stored catalog: one `Create` of the immutable template.
let publish
    (actor: Store.Actor)
    (policy: Validation.Policy)
    (acknowledged: Set<string>)
    (now: DateTimeOffset)
    (opened: Store.Opened)
    (draft: Draft)
    : Async<Result<Published, PublishFailure>> =
    async {
        match permitted opened actor Access.PublishTemplates with
        | Error failure -> return Error(NotStored failure)
        | Ok _ ->
            match! load now opened with
            | Error failure -> return Error(NotStored failure)
            | Ok stored ->
                match Publication.publish policy stored.Catalog acknowledged now actor.Principal.PrincipalId draft with
                | Error refusal -> return Error(Refused refusal)
                | Ok(published, _) ->
                    let changes =
                        match TemplateRecord.publishedPath published, TemplateRecord.encodePublished opened.DatasetId published with
                        | Ok path, Ok content -> Ok [ Change.Create(path, content) ]
                        | Error p, _
                        | _, Error p -> Error p

                    match! commit now opened actor $"publish {published.SurveyId} {published.Version}" changes with
                    | Ok() -> return Ok published
                    | Error failure -> return Error(NotStored failure)
    }

/// Hides a published version from new authoring and distribution.
let hide (actor: Store.Actor) (now: DateTimeOffset) (opened: Store.Opened) (surveyId: string) (version: string) : AsyncResult<unit, GroupFailure> =
    asyncResult {
        let! _ = permitted opened actor Access.PublishTemplates |> lift
        let! stored = load now opened

        let changes =
            match Publication.resolve stored.Catalog surveyId version with
            | None -> Error(InvalidStoredRecord($"{surveyId} {version}", "no such published version"))
            | Some template ->
                let hidden = (Publication.hide template stored.Catalog).Hidden

                match TemplateRecord.catalogPath (), TemplateRecord.encodeCatalog opened.DatasetId hidden with
                | Ok path, Ok content -> Ok [ (match stored.CatalogRevision with Some r -> Change.Update(path, content, r) | None -> Change.Create(path, content)) ]
                | Error p, _
                | _, Error p -> Error p

        return! commit now opened actor $"hide {surveyId} {version}" changes
    }
