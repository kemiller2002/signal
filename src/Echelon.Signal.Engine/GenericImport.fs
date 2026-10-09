/// Importing submissions of generic templates (WI-0078, ARP-001): the same
/// pipeline order as the pilot's (`Import.evaluateAgainst`) over a generic
/// envelope, scored by the template's own scorers. The result has the shape
/// the group pipeline reads: one dimension per section, its score or why it
/// has none, and answer counts only (ARP §11).
///
/// Pure.
module Echelon.Signal.Engine.GenericImport

open System
open System.Buffers.Text
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import

/// A template's shape for the result pipeline: a dimension per section and
/// an item per question.
let shapeOf (surveyId: string) (version: string) (content: Content) : Assessment =
    { Id = surveyId
      Title = content.Metadata.Title
      Version = version
      Dimensions = content.Sections |> List.map (fun s -> { Id = s.Id; Label = s.Title })
      Items = content.Sections |> List.collect (fun s -> s.Questions |> List.map (fun q -> { Id = q.Id; DimensionId = s.Id; Prompt = q.Prompt }))
      MinimumNumericAnswers = 1 }

/// What a group needs to run a published generic template.
let formOf (surveyId: string) (version: string) (content: Content) : GenericForm =
    { Content = content
      Reference = GenericEnvelope.referenceOf surveyId version content
      Hash = TemplateCanonical.templateHash surveyId version content }

let private dimensionResults (shape: Assessment) (content: Content) (result: SurveyResult.Result) =
    shape.Dimensions
    |> List.map (fun d ->
        let items = shape.Items |> List.filter (fun i -> i.DimensionId = d.Id) |> List.length
        let minimum = content.Sections |> List.tryFind (fun s -> s.Id = d.Id) |> Option.bind _.Scoring |> Option.map _.Scorer.Missing.MinimumObservations |> Option.defaultValue 1

        let outcome =
            match result.Evaluation.Sections |> List.tryFind (fst >> (=) d.Id) |> Option.map snd with
            | Some(Rules.SectionScored(Scoring.Score(value, included, _))) -> Scored(value, included, items)
            | Some(Rules.SectionScored(Scoring.NotScored _)) -> Unscored(0, items, minimum)
            | _ -> Unscored(0, items, minimum)

        d, outcome)

/// Where an import runs (AUT-006 §60). Production refuses test and preview
/// artifacts; only an import explicitly opened as a test environment reads
/// them, as the submissions they stand in for.
type Environment =
    | ProductionImport
    | TestEnvironment

/// Reads and validates one generic submission against a group, without
/// changing anything, in an environment; `accepted` gives the SubmissionHash
/// already accepted for an identity key, if any.
let evaluateIn (environment: Environment) (definition: GroupDefinition) (form: GenericForm) (accepted: string -> string option) (text: string) : ImportOutcome =
    match payloadOf text with
    | None -> Rejected NoSubmissionFound
    | Some payload ->
        match GenericEnvelope.decode form.Content form.Reference payload with
        | Error error -> Rejected(Unreadable error)
        | Ok envelope ->
            let binding =
                match environment with
                | ProductionImport -> envelope.Binding
                | TestEnvironment -> production envelope.Binding

            match identify definition binding with
            | Error error -> Rejected error
            | Ok identity ->
                let result = SurveyResult.compute form.Hash form.Content envelope.Answers true

                match result.Evaluation.Completion with
                | Rules.InProgress missing -> Rejected(IncompleteSubmission missing)
                | Rules.NotStarted -> Rejected(IncompleteSubmission(questions form.Content |> List.length))
                | Rules.Invalid problems -> Rejected(IncompleteSubmission problems)
                | Rules.ReadyToSubmit
                | Rules.Terminated _ ->
                    let canonical = Base64Url.DecodeFromChars((GenericEnvelope.encode form.Content form.Reference envelope).AsSpan())

                    let accepted' =
                        { Identity = identity
                          SurveyIdentifier = definition.Template.Id
                          TemplateVersion = definition.Template.Version
                          TemplateHash = form.Hash
                          SubmissionHash = sha256Of canonical
                          Dimensions = dimensionResults definition.Template form.Content result
                          AnsweredCount = envelope.Answers.Count
                          NonNumericCount = envelope.Answers |> Map.filter (fun _ a -> match a with Special _ -> true | _ -> false) |> Map.count }

                    match accepted identity.Key with
                    | Some existing when existing = accepted'.SubmissionHash -> AlreadyImported identity
                    | Some existing -> Rejected(DuplicateInstance existing)
                    | None -> Accepted accepted'

/// Reads one generic submission in production: test artifacts are refused.
let evaluateAgainst (definition: GroupDefinition) (form: GenericForm) (accepted: string -> string option) (text: string) : ImportOutcome =
    evaluateIn ProductionImport definition form accepted text

/// One finalized submission link for a generic template (what the respondent
/// page produces): the template's page with `#r=` and the envelope.
let link (form: GenericForm) (pagePath: string) (envelope: GenericEnvelope.Envelope) =
    $"{pagePath}#{LiveUrl.FragmentKey}={GenericEnvelope.encode form.Content form.Reference envelope}"

/// Reads a submission against any group: the pilot's pipeline or the generic one.
let evaluate (definition: GroupDefinition) (accepted: string -> string option) (text: string) : ImportOutcome =
    match definition.Generic with
    | Some form -> evaluateAgainst definition form accepted text
    | None -> Import.evaluateAgainst definition accepted text
