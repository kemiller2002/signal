/// AdminReportState and its persistence (ARP-003, ARP-004): the projection
/// of a group's result that reports render from, and where it lives.
///
/// - The state is a deterministic projection of the SurveyGroupResult plus
///   what continuing to import needs (§54-57): the accepted identities with
///   their SubmissionHashes and the incremental aggregation evidence. It is
///   not a copy of the submissions: no answers, no raw URLs.
/// - **Persistence is chosen by size** (§45-47): a state whose encoded form
///   fits the administrator URL budget, less a safety margin, travels in the
///   URL (`EmbeddedInUrl`); a larger one is stored as an immutable record and
///   the URL carries only its opaque id, the group and an integrity check
///   (`ExternalStore`). Reporting reads an `AdminReportState` either way.
/// - **Integrity** (§50): an embedded state carries a hash of its canonical
///   text; a reference carries the group and the stored state's hash, so a
///   tampered, truncated, wrong-group or unknown reference is refused.
/// - **Resume** (§53): the decoded state rebuilds the incremental
///   accumulator, so imports continue without the submissions.
///
/// Pure.
module Echelon.Signal.Admin.ReportState

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Aggregation
open Echelon.Signal.Admin.Codec

/// The state's schema version.
[<Literal>]
let StateVersion = 1

/// One section (dimension) as reports summarize it (§34).
type SectionSummary =
    { SectionId: string
      AggregateScore: float option
      Scored: int
      Unscored: int
      Suppressed: bool }

/// The administrator report state (§32).
[<NoComparison>]
type AdminReportState =
    { ResultSchemaVersion: int
      Group: OpaqueId
      Mode: IdentityMode
      ExpectedSurveyCount: int
      AcceptedSurveyCount: int
      Completion: GroupCompletion
      TemplateHash: string
      /// Lowest and highest mean section, when sections are reportable (§33).
      WeakestArea: string option
      StrongestArea: string option
      Sections: SectionSummary list
      /// Share of answers that were non-numeric: coverage, not performance.
      Coverage: float option
      /// The group result's derivation hash: which inputs this reflects.
      DerivationHash: string
      /// SubmissionHash by accepted identity key, for duplicate prevention (§54-55).
      Accepted: Map<string, string>
      /// Incremental aggregation evidence, so imports can continue (§53).
      Evidence: Map<string, Incremental.DimensionEvidence>
      Answered: int
      NonNumeric: int }

/// The state for an accumulator, under a policy.
let project (policy: Policy) (accumulator: Incremental.Accumulator) : AdminReportState =
    let result = Incremental.result policy accumulator

    let sections =
        result.Dimensions
        |> List.map (fun (dimension, aggregate) ->
            match aggregate with
            | Aggregated statistics ->
                { SectionId = dimension.Id
                  AggregateScore = statistics.Mean
                  Scored = statistics.Scored
                  Unscored = statistics.Unscored
                  Suppressed = false }
            | Suppressed(_, _) ->
                { SectionId = dimension.Id
                  AggregateScore = None
                  Scored = 0
                  Unscored = 0
                  Suppressed = true })

    let scored = sections |> List.choose (fun s -> s.AggregateScore |> Option.map (fun score -> s.SectionId, score))

    { ResultSchemaVersion = StateVersion
      Group = result.Group
      Mode = result.Mode
      ExpectedSurveyCount = result.ExpectedCount
      AcceptedSurveyCount = result.AcceptedCount
      Completion = result.Completion
      TemplateHash = result.Template.TemplateHash
      // Ties go to the earlier section, so the choice is deterministic.
      WeakestArea = scored |> List.sortBy snd |> List.tryHead |> Option.map fst
      StrongestArea = scored |> List.sortByDescending snd |> List.tryHead |> Option.map fst
      Sections = sections
      Coverage = result.NonNumericShare
      DerivationHash = result.Lineage.DerivationHash
      Accepted = accumulator.Accepted
      Evidence = accumulator.Evidence
      Answered = accumulator.Answered
      NonNumeric = accumulator.NonNumeric }

/// Why a stored or embedded state cannot be resumed against a group.
type ResumeRefusal =
    | OtherGroup
    | OtherTemplate of stored: string * configured: string

/// The accumulator a state stands for, to continue importing (§53).
let resume (definition: GroupDefinition) (state: AdminReportState) : Result<Incremental.Accumulator, ResumeRefusal> =
    if state.Group <> definition.Group then
        Error OtherGroup
    elif state.TemplateHash <> Canonical.templateHash definition.Template then
        Error(OtherTemplate(state.TemplateHash, Canonical.templateHash definition.Template))
    else
        Ok
            { Definition = definition
              Accepted = state.Accepted
              Evidence = state.Evidence
              Answered = state.Answered
              NonNumeric = state.NonNumeric }

// ---- Canonical form -------------------------------------------------------------------------

let private real (value: float) = Json.String(value.ToString("R", CultureInfo.InvariantCulture))
let private whole (value: int) = Json.Number(decimal value)

let private optional f =
    function
    | Some value -> f value
    | None -> Json.Null

/// The state's canonical JSON value.
let toJson (state: AdminReportState) =
    Json.objectOf
        [ "version", whole state.ResultSchemaVersion
          "group", Json.String(string state.Group)
          "mode", Json.String(if state.Mode = IdentifiedGroup then "identified" else "anonymous")
          "expected", whole state.ExpectedSurveyCount
          "accepted", whole state.AcceptedSurveyCount
          "complete", Json.Bool(state.Completion = Complete)
          "template", Json.String state.TemplateHash
          "weakest", optional Json.String state.WeakestArea
          "strongest", optional Json.String state.StrongestArea
          "sections",
          Json.Array(
              state.Sections
              |> List.map (fun s ->
                  Json.objectOf
                      [ "id", Json.String s.SectionId
                        "score", optional real s.AggregateScore
                        "scored", whole s.Scored
                        "unscored", whole s.Unscored
                        "suppressed", Json.Bool s.Suppressed ])
          )
          "coverage", optional real state.Coverage
          "derivation", Json.String state.DerivationHash
          "identities", Json.objectOf (state.Accepted |> Map.toList |> List.map (fun (key, hash) -> key, Json.String hash))
          "evidence",
          Json.objectOf (
              state.Evidence
              |> Map.toList
              |> List.map (fun (id, e) -> id, Json.objectOf [ "scores", Json.Array(e.Scores |> List.map real); "unscored", whole e.Unscored ])
          )
          "answered", whole state.Answered
          "nonNumeric", whole state.NonNumeric ]

let private realOf name value : Decoded<float> =
    text name value
    |> Result.bind (fun t ->
        match Double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, parsed when Double.IsFinite parsed && parsed.ToString("R", CultureInfo.InvariantCulture) = t -> Ok parsed
        | _ -> Error $"'{name}' is not a round-trip number")

let private optionalOf name decode value : Decoded<'a option> =
    field name value |> Result.bind (function Json.Null -> Ok None | _ -> decode name value |> Result.map Some)

let private realValue (value: Json) =
    match value with
    | Json.String _ -> realOf "score" (Json.objectOf [ "score", value ])
    | _ -> Error "a score is not text"

/// A state from its canonical JSON value; every field checked.
let ofJson (value: Json) : Decoded<AdminReportState> =
    let members name =
        field name value
        |> Result.bind (function
            | Json.Object found -> Ok found
            | _ -> Error $"'{name}' is not an object")

    let section (s: Json) =
        match text "id" s, optionalOf "score" realOf s, integer "scored" s, integer "unscored" s, flag "suppressed" s with
        | Ok id, Ok score, Ok scored, Ok unscored, Ok suppressed ->
            Ok
                { SectionId = id
                  AggregateScore = score
                  Scored = scored
                  Unscored = unscored
                  Suppressed = suppressed }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e

    let evidence =
        members "evidence"
        |> Result.bind (
            traverse (fun (id, e) ->
                match list "scores" realValue e, integer "unscored" e with
                | Ok scores, Ok unscored when scores = List.sort scores -> Ok(id, ({ Scores = scores; Unscored = unscored }: Incremental.DimensionEvidence))
                | Ok _, Ok _ -> Error "evidence scores are not in ascending order"
                | Error e, _
                | _, Error e -> Error e)
        )

    let identities =
        members "identities"
        |> Result.bind (traverse (fun (key, hash) -> match hash with Json.String h -> Ok(key, h) | _ -> Error "an identity's hash is not text"))

    let group =
        text "group" value
        |> Result.bind (fun t -> OpaqueId.tryParse t |> Option.map Ok |> Option.defaultValue (Error "'group' is not a group id"))

    let mode =
        text "mode" value
        |> Result.bind (function
            | "identified" -> Ok IdentifiedGroup
            | "anonymous" -> Ok AnonymousGroup
            | other -> Error $"'{other}' is not a group mode")

    match
        both (integer "version" value) group,
        both mode (both (integer "expected" value) (integer "accepted" value)),
        both (flag "complete" value) (text "template" value),
        both (optionalOf "weakest" text value) (optionalOf "strongest" text value),
        both (list "sections" section value) (optionalOf "coverage" realOf value),
        both (text "derivation" value) identities,
        both evidence (both (integer "answered" value) (integer "nonNumeric" value))
    with
    | Ok(version, _), _, _, _, _, _, _ when version <> StateVersion -> Error $"state version {version} is not {StateVersion}"
    | Ok(version, group), Ok(mode, (expected, accepted)), Ok(complete, template), Ok(weakest, strongest), Ok(sections, coverage), Ok(derivation, identities), Ok(evidence, (answered, nonNumeric)) ->
        Ok
            { ResultSchemaVersion = version
              Group = group
              Mode = mode
              ExpectedSurveyCount = expected
              AcceptedSurveyCount = accepted
              Completion = if complete then Complete else WaitingForResponses
              TemplateHash = template
              WeakestArea = weakest
              StrongestArea = strongest
              Sections = sections
              Coverage = coverage
              DerivationHash = derivation
              Accepted = Map.ofList identities
              Evidence = Map.ofList evidence
              Answered = answered
              NonNumeric = nonNumeric }
    | Error e, _, _, _, _, _, _
    | _, Error e, _, _, _, _, _
    | _, _, Error e, _, _, _, _
    | _, _, _, Error e, _, _, _
    | _, _, _, _, Error e, _, _
    | _, _, _, _, _, Error e, _
    | _, _, _, _, _, _, Error e -> Error e

/// The state's canonical text.
let canonical (state: AdminReportState) = Json.canonicalText (toJson state)

let private sha256 (text: string) = SHA256.HashData(Encoding.UTF8.GetBytes text)
let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

/// The state's stable, opaque id when stored externally (§49): never sequential.
let resultId (state: AdminReportState) = "rs-" + (hex (sha256 (canonical state))).Substring(0, 32)

// ---- Persistence by size ---------------------------------------------------------------------

/// The administrator URL budget and the share of it a state may use (§46).
type UrlBudget =
    { /// The longest administrator URL the deployment supports.
      MaximumUrlLength: int
      /// The base URL and routing parameters.
      Reserved: int
      /// The fraction of what is left a state may fill, for future expansion.
      SafetyFactor: float }

/// A conservative default: 8,000-character URLs, 500 reserved, three quarters usable.
let defaultBudget =
    { MaximumUrlLength = 8000
      Reserved = 500
      SafetyFactor = 0.75 }

/// Where a state lives (§41).
type Persistence =
    | EmbeddedInUrl of fragment: string
    | ExternalStore of resultId: string * fragment: string

let private base64 (bytes: byte[]) = Buffers.Text.Base64Url.EncodeToString(ReadOnlySpan bytes)

/// The embedded fragment: `a=<version>.<payload>.<integrity>`.
let embedded (state: AdminReportState) =
    let text = canonical state
    $"a={StateVersion}.{base64 (Encoding.UTF8.GetBytes text)}.{base64 ((sha256 text)[0..15])}"

/// The reference fragment: `s=<result id>.<group>.<integrity>`.
let reference (state: AdminReportState) =
    let text = canonical state
    $"s={resultId state}.{GroupRecord.groupKey state.Group}.{base64 ((sha256 text)[0..15])}"

/// Chooses by encoded size, automatically (§45, §47).
let persistence (budget: UrlBudget) (state: AdminReportState) =
    let fragment = embedded state
    let usable = int (float (budget.MaximumUrlLength - budget.Reserved) * budget.SafetyFactor)

    if fragment.Length <= usable then
        EmbeddedInUrl fragment
    else
        ExternalStore(resultId state, reference state)

/// Why an administrator fragment was refused (§50).
type FragmentError =
    | Malformed
    | UnsupportedVersion of int
    | IntegrityMismatch
    | WrongGroupReference
    | InvalidState of reason: string

/// Reads an embedded fragment back, checking its integrity.
let readEmbedded (fragment: string) : Result<AdminReportState, FragmentError> =
    match fragment.Split('.') with
    | [| head; payload; integrity |] when head.StartsWith "a=" ->
        match Int32.TryParse(head.Substring 2), tryFromBase64Url payload with
        | (true, version), _ when version <> StateVersion -> Error(UnsupportedVersion version)
        | (true, _), Some bytes ->
            let text =
                if Unicode.Utf8.IsValid(ReadOnlySpan bytes) then Some(Encoding.UTF8.GetString bytes) else None

            match text with
            | Some text when base64 ((sha256 text)[0..15]) = integrity ->
                Json.parse text
                |> Result.mapError (fun _ -> Malformed)
                |> Result.bind (fun value -> ofJson value |> Result.mapError InvalidState)
            | Some _ -> Error IntegrityMismatch
            | None -> Error Malformed
        | _ -> Error Malformed
    | _ -> Error Malformed

/// The result id a reference fragment names, once its group and the stored
/// state's integrity are checked against the state loaded for that id.
let checkReference (fragment: string) (group: OpaqueId) (stored: AdminReportState) : Result<AdminReportState, FragmentError> =
    match fragment.Split('.') with
    | [| head; groupKey; integrity |] when head.StartsWith "s=" ->
        if groupKey <> GroupRecord.groupKey group || stored.Group <> group then
            Error WrongGroupReference
        elif head.Substring 2 <> resultId stored || base64 ((sha256 (canonical stored))[0..15]) <> integrity then
            Error IntegrityMismatch
        else
            Ok stored
    | _ -> Error Malformed

/// The result id a reference fragment names, before loading it.
let referencedId (fragment: string) =
    match fragment.Split('.') with
    | [| head; _; _ |] when head.StartsWith "s=rs-" && head.Length = 37 -> Some(head.Substring 2)
    | _ -> None

// ---- The external store (§43, §48) -------------------------------------------------------------

let recordType =
    match RecordType.create "signal.report-state" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// Where a stored state lives: by its result id.
let path (id: string) : Result<RelativePath, Problems.Problem> =
    match RecordId.create id with
    | Ok recordId ->
        Layout.recordPath
            { Type = recordType
              Partition = []
              Id = recordId }
        |> Result.mapError (LocationError.describe >> Problems.InvalidDataLocation)
    | Error text -> Error(Problems.UnstorableRecord(text, "not a result id"))

/// The state as an immutable record: the same state always has the same id
/// and content, so saving it again is a no-op conflict, never a change.
let encodeRecord (state: AdminReportState) : Result<RelativePath * string, Problems.Problem> =
    let id = resultId state

    path id
    |> Result.bind (fun target ->
        match RecordId.create id with
        | Error text -> Error(Problems.UnstorableRecord(text, "not a result id"))
        | Ok recordId ->
            { Id = recordId
              Type = recordType
              SchemaVersion = schema.Current
              Mutability = Mutability.Immutable
              Body = toJson state }
            |> Record.encode Record.DefaultMaxBytes
            |> Result.map (fun text -> target, text)
            |> Result.mapError (fun _ -> Problems.UnstorableRecord(id, "the report state is too large")))

/// A stored state, read as untrusted input: the record must be valid, at its
/// own path, and its content must hash to its id.
let readRecord (id: string) (stored: ReadOutcome) : Result<AdminReportState, Problems.Problem> =
    match stored with
    | ReadOutcome.Absent -> Error(Problems.InvalidStoredRecord(id, "no report state is stored under this id"))
    | ReadOutcome.Found found ->
        let where = RelativePath.render found.Path

        match RecordId.create id with
        | Error _ -> Error(Problems.InvalidStoredRecord(where, "not a result id"))
        | Ok recordId ->
            Integrity.validate { Type = recordType; Partition = []; Id = recordId } schema Record.DefaultMaxBytes found
            |> Result.mapError (fun failure -> Problems.InvalidStoredRecord(where, describeIntegrity failure))
            |> Result.bind (fun valid -> ofJson valid.Record.Body |> Result.mapError (fun detail -> Problems.InvalidStoredRecord(where, detail)))
            |> Result.bind (fun state ->
                if resultId state = id then Ok state else Error(Problems.InvalidStoredRecord(where, "the state does not hash to its id")))
