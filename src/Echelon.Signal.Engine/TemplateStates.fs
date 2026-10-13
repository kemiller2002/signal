/// Template authoring and publication as an ARX-002 state system (WI-0092):
/// the open draft and the published catalog, the explicit result of each
/// command, the capabilities the state grants, the obligations that keep a
/// draft from publication with what discharges them, and what is known
/// about a version looked up in the catalog.
///
/// Pure.
module Echelon.Signal.Engine.TemplateStates

open System
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Findings
open Echelon.Signal.Engine.Validation
open Echelon.Signal.Engine.Publication
open Echelon.Signal.Engine.StateModel

let SystemName = "template"

/// The catalog and the draft being authored, if one is open.
[<NoComparison>]
type AuthoringState =
    { Policy: Policy
      Catalog: Catalog
      Draft: Draft option }

type Command =
    | PublishDraft of acknowledged: Set<string>
    | DeriveFrom of version: string
    | DiscardDraft
    | HideVersion of version: string

let commandName =
    function
    | PublishDraft _ -> "publish"
    | DeriveFrom _ -> "derive"
    | DiscardDraft -> "discard"
    | HideVersion _ -> "hide"

let stateName (s: AuthoringState) =
    match s.Draft with
    | None -> "NoDraft"
    | Some d ->
        match draftState s.Policy d with
        | ValidatedState -> "Validated"
        | _ -> "Draft"

let capabilities (s: AuthoringState) =
    set
        [ if s.Draft.IsSome then Capability.CanEditDraft
          if s.Draft |> Option.exists (fun d -> draftState s.Policy d = ValidatedState) then Capability.CanPublishTemplate ]

let requires =
    function
    | PublishDraft _ -> Some Capability.CanPublishTemplate
    | DiscardDraft -> Some Capability.CanEditDraft
    | DeriveFrom _
    | HideVersion _ -> None

/// The obligation a blocking category leaves, and what discharges it.
let private obligationFor (category: Category) (count: int) =
    let make code discharge = { Code = code; Detail = $"{count} blocker(s) in {category}."; DischargedBy = discharge }

    match category with
    | PrivacyCategory -> make "CompletePrivacyValidation" "rewording each text flagged as asking for identifying information"
    | CompatibilityCategory -> make "ResolveUnsupportedCapability" "declaring only capabilities this engine supports, and matching versions"
    | FixturesCategory -> make "SupplyPublicationEvidence" "test fixtures that pass, one at least"
    | other -> make $"ResolveValidationBlocker:{other}" "content that passes that category's checks"

let obligations (s: AuthoringState) =
    match s.Draft with
    | None -> []
    | Some d ->
        let report = validate s.Policy d

        (report.Blockers |> List.countBy _.Category |> List.map (fun (c, n) -> obligationFor c n))
        @ [ if not report.Warnings.IsEmpty then
                { Code = "AcknowledgeWarnings"
                  Detail = $"{report.Warnings.Length} warning(s)."
                  DischargedBy = "acknowledging each warning when publishing" } ]

/// A version looked up in the catalog: known, or searched and not found
/// (named by survey and version, in a catalog of a stated size).
let lookup (catalog: Catalog) (surveyId: string) (version: string) : Knowledge<Published> =
    match resolve catalog surveyId version with
    | Some t -> Known t
    | None -> SearchedNotFound($"{surveyId} version {version}", $"the template catalog of {catalog.Templates.Length} published version(s)")

let private surveyOf (s: AuthoringState) =
    s.Draft |> Option.map _.SurveyId |> Option.orElse (s.Catalog.Templates |> List.tryHead |> Option.map _.SurveyId)

let attempt (command: Command) (s: AuthoringState) : Result<AuthoringState, Illegal> =
    let refuse reason = illegal SystemName (stateName s) (commandName command) reason
    let need = requires command |> Option.filter (fun c -> not ((capabilities s).Contains c))

    match command, need with
    | _, Some capability ->
        refuse (
            match obligations s with
            | [] -> $"{capability} does not hold"
            | open' -> $"""{capability} does not hold: {open' |> List.map _.Code |> String.concat ", "}"""
        )
    | PublishDraft acknowledged, None ->
        match s.Draft with
        | None -> refuse "no draft is open"
        | Some d ->
            match publish s.Policy s.Catalog acknowledged DateTimeOffset.UnixEpoch "author" d with
            | Ok(_, catalog) -> Ok { s with Catalog = catalog; Draft = None }
            | Error(Blocked _) -> refuse "validation blockers remain"
            | Error(UnacknowledgedWarnings ws) -> refuse $"""unacknowledged warnings: {ws |> List.map _.Code |> String.concat ", "}"""
            | Error(UnknownParent p) -> refuse $"the parent version {p.Version} is not published"
            | Error(Unchanged v) -> refuse $"the content is the same as published version {v}"
    | DeriveFrom version, None ->
        match s.Draft, surveyOf s |> Option.map (fun survey -> lookup s.Catalog survey version) with
        | Some _, _ -> refuse "a draft is already open"
        | None, Some(Known t) -> Ok { s with Draft = Some(deriveDraft t) }
        | None, Some(SearchedNotFound(what, scope)) -> refuse $"{what} was not found in {scope}"
        | None, _ -> refuse "there is no survey to derive from"
    | DiscardDraft, None -> Ok { s with Draft = None }
    | HideVersion version, None ->
        match surveyOf s |> Option.bind (fun survey -> resolve s.Catalog survey version) with
        | None -> refuse $"version {version} is not published"
        | Some t when s.Catalog.Hidden.Contains(t.SurveyId, t.Version) -> refuse $"version {version} is already hidden"
        | Some t -> Ok { s with Catalog = hide t s.Catalog }

let system: System<AuthoringState, Command> =
    { Name = SystemName
      StateName = stateName
      CommandName = commandName
      Attempt = attempt
      Capabilities = capabilities
      Requires = requires
      Obligations = obligations
      Terminal = fun _ -> false }
