/// The template catalog's records (WI-0057, AUT-005, AUT-006, CAN-005,
/// VER-001, VER-007): published templates, drafts and the catalog's
/// visibility, as Arca records in a dataset.
///
/// - **Published** (`signal.template`, immutable): the template's canonical
///   bytes (base64) with its identity, hash, provenance, manifest and
///   fixtures. Reading it decodes the bytes (`TemplateDecode`) and refuses a
///   record whose bytes do not hash to its recorded hash, or whose identity
///   differs, so a stored template keeps the identity it was published with.
/// - **Draft** (`signal.template-draft`, mutable): one per survey, its
///   content in the same canonical form and its fixtures.
/// - **Catalog** (`signal.template-catalog`, mutable): the hidden templates.
///
/// Pure.
module Echelon.Signal.Admin.TemplateRecord

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Drafts
open Echelon.Signal.Engine.Publication
open Echelon.Signal.Admin.Problems
open Echelon.Signal.Admin.Codec

let private typeOf (name: string) =
    match RecordType.create name with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let publishedType = typeOf "signal.template"
let draftType = typeOf "signal.template-draft"
let catalogType = typeOf "signal.template-catalog"

let private schemaOf recordType = { Type = recordType; OldestReadable = 1; Current = 1 }

/// A stable, opaque record id from text (a hash or a survey identifier).
let private idFor (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant().Substring(0, 32)

let publishedId (template: Published) = idFor template.Hash
let draftId (surveyId: string) = idFor ("draft:" + surveyId)

[<Literal>]
let CatalogId = "catalog"

let private pathOf (recordType: RecordType) (id: string) : Result<RelativePath, Problem> =
    match RecordId.create id with
    | Ok recordId ->
        { Type = recordType; Partition = []; Id = recordId }
        |> Layout.recordPath
        |> Result.mapError (LocationError.describe >> InvalidDataLocation)
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

let publishedPath template = pathOf publishedType (publishedId template)
let draftPath surveyId = pathOf draftType (draftId surveyId)
let catalogPath () = pathOf catalogType CatalogId

let folderOf (recordType: RecordType) : RelativePath =
    match RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ]) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

// ---- Values ----------------------------------------------------------------------------------

let private real (value: float) = Json.String(value.ToString("R", CultureInfo.InvariantCulture))

let private realOf name value : Decoded<float> =
    text name value
    |> Result.bind (fun t ->
        match Double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, parsed when parsed.ToString("R", CultureInfo.InvariantCulture) = t -> Ok parsed
        | _ -> Error $"'{name}' is not a round-trip number")

let private whole (n: int) = Json.Number(decimal n)
let private optionalText = Option.map Json.String >> Option.defaultValue Json.Null

let private optionalOf (decode: string -> Json -> Decoded<'a>) name value : Decoded<'a option> =
    field name value |> Result.bind (function Json.Null -> Ok None | _ -> decode name value |> Result.map Some)

let private textList = List.map Json.String >> Json.Array

let private specialNames = TemplateDecodeCore.specialStates

let private answerValueJson (v: AnswerValue) =
    match v with
    | Flag b -> Json.objectOf [ "flag", Json.Bool b ]
    | Point p -> Json.objectOf [ "point", whole p ]
    | Choice id -> Json.objectOf [ "choice", Json.String id ]
    | Choices ids -> Json.objectOf [ "choices", textList (Set.toList ids) ]
    | Tick t -> Json.objectOf [ "tick", whole t ]
    | TickRange(lo, hi) -> Json.objectOf [ "low", whole lo; "high", whole hi ]
    | Order ids -> Json.objectOf [ "order", textList ids ]
    | Allocated steps -> Json.objectOf [ "allocated", Json.objectOf (steps |> Map.toList |> List.map (fun (k, n) -> k, whole n)) ]
    | BestWorstPick(b, w) -> Json.objectOf [ "best", Json.String b; "worst", Json.String w ]

let private answerValueOf (value: Json) : Decoded<AnswerValue> =
    let has name = (field name value) |> Result.isOk

    if has "flag" then flag "flag" value |> Result.map Flag
    elif has "point" then integer "point" value |> Result.map Point
    elif has "choice" then text "choice" value |> Result.map Choice
    elif has "choices" then texts "choices" value |> Result.map (Set.ofList >> Choices)
    elif has "tick" then integer "tick" value |> Result.map Tick
    elif has "low" then both (integer "low" value) (integer "high" value) |> Result.map TickRange
    elif has "order" then texts "order" value |> Result.map Order
    elif has "allocated" then
        field "allocated" value
        |> Result.bind (function
            | Json.Object members -> members |> traverse (fun (k, v) -> integer "n" (Json.objectOf [ "n", v ]) |> Result.map (fun n -> k, n)) |> Result.map (Map.ofList >> Allocated)
            | _ -> Error "'allocated' is not an object")
    elif has "best" then both (text "best" value) (text "worst" value) |> Result.map BestWorstPick
    else Error "not a known answer value"

let private answerStateJson (state: AnswerState) =
    match state with
    | Value v -> Json.objectOf [ "value", answerValueJson v ]
    | Special s -> Json.objectOf [ "special", Json.String(specialNames |> List.find (fst >> (=) s) |> snd) ]

let private answerStateOf (value: Json) : Decoded<AnswerState> =
    match field "value" value, text "special" value with
    | Ok v, _ -> answerValueOf v |> Result.map Value
    | _, Ok s -> TemplateDecodeCore.named specialNames "special state" s |> Result.map Special
    | _ -> Error "not a known answer state"

let private factValueJson =
    function
    | BoolValue b -> Json.objectOf [ "bool", Json.Bool b ]
    | NumberValue n -> Json.objectOf [ "number", real n ]
    | CategoryValue c -> Json.objectOf [ "category", Json.String c ]

let private factValueOf (value: Json) : Decoded<FactValue> =
    match flag "bool" value, realOf "number" value, text "category" value with
    | Ok b, _, _ -> Ok(BoolValue b)
    | _, Ok n, _ -> Ok(NumberValue n)
    | _, _, Ok c -> Ok(CategoryValue c)
    | _ -> Error "not a known fact value"

let private optionalReal = Option.map real >> Option.defaultValue Json.Null

let private assertionJson (a: Assertion) =
    match a with
    | ExpectSectionScore(s, v) -> Json.objectOf [ "kind", Json.String "section-score"; "section", Json.String s; "expected", optionalReal v ]
    | ExpectComplete c -> Json.objectOf [ "kind", Json.String "complete"; "expected", Json.Bool c ]
    | ExpectApplicable(q, a) -> Json.objectOf [ "kind", Json.String "applicable"; "question", Json.String q; "expected", Json.Bool a ]
    | ExpectFact(f, v) -> Json.objectOf [ "kind", Json.String "fact"; "fact", Json.String f; "expected", (v |> Option.map factValueJson |> Option.defaultValue Json.Null) ]
    | ExpectRecommended(r, t) -> Json.objectOf [ "kind", Json.String "recommended"; "recommendation", Json.String r; "expected", Json.Bool t ]
    | ExpectOverall v -> Json.objectOf [ "kind", Json.String "overall"; "expected", optionalReal v ]
    | ExpectInterpretation(i, l) -> Json.objectOf [ "kind", Json.String "interpretation"; "interpretation", Json.String i; "expected", optionalText l ]

let private assertionOf (value: Json) : Decoded<Assertion> =
    text "kind" value
    |> Result.bind (function
        | "section-score" -> both (text "section" value) (optionalOf realOf "expected" value) |> Result.map ExpectSectionScore
        | "complete" -> flag "expected" value |> Result.map ExpectComplete
        | "applicable" -> both (text "question" value) (flag "expected" value) |> Result.map ExpectApplicable
        | "fact" ->
            both (text "fact" value) (field "expected" value |> Result.bind (function Json.Null -> Ok None | v -> factValueOf v |> Result.map Some))
            |> Result.map ExpectFact
        | "recommended" -> both (text "recommendation" value) (flag "expected" value) |> Result.map ExpectRecommended
        | "overall" -> optionalOf realOf "expected" value |> Result.map ExpectOverall
        | "interpretation" -> both (text "interpretation" value) (optionalOf text "expected" value) |> Result.map ExpectInterpretation
        | other -> Error $"'{other}' is not a known assertion")

let private fixtureJson (f: Fixture) =
    Json.objectOf
        [ "id", Json.String f.Id
          "name", Json.String f.Name
          "answers", Json.objectOf (f.Answers |> Map.toList |> List.map (fun (q, s) -> q, answerStateJson s))
          "expect", Json.Array(f.Expect |> List.map assertionJson) ]

let private fixtureOf (value: Json) : Decoded<Fixture> =
    let answers =
        field "answers" value
        |> Result.bind (function
            | Json.Object members -> members |> traverse (fun (q, s) -> answerStateOf s |> Result.map (fun a -> q, a)) |> Result.map Map.ofList
            | _ -> Error "'answers' is not an object")

    match both (text "id" value) (text "name" value), answers, list "expect" assertionOf value with
    | Ok(id, name), Ok answers, Ok expect -> Ok { Id = id; Name = name; Answers = answers; Expect = expect }
    | Error e, _, _
    | _, Error e, _
    | _, _, Error e -> Error e

let private parentJson = Option.map (fun (p: ParentReference) -> Json.objectOf [ "version", Json.String p.Version; "hash", Json.String p.Hash ]) >> Option.defaultValue Json.Null

let private parentOf (value: Json) : Decoded<ParentReference option> =
    field "parent" value
    |> Result.bind (function
        | Json.Null -> Ok None
        | p -> both (text "version" p) (text "hash" p) |> Result.map (fun (v, h) -> Some { Version = v; Hash = h }))

let private canonicalText (surveyId: string) (version: string) (content: Template.Content) =
    Convert.ToBase64String(TemplateCanonical.bytes surveyId version content)

let private contentOf (expectedSurvey: string) (expectedVersion: string) (encoded: string) : Decoded<Template.Content * byte[]> =
    let bytes = try Ok(Convert.FromBase64String encoded) with :? FormatException -> Error "'canonical' is not base64"

    bytes
    |> Result.bind (fun b ->
        TemplateDecode.decode b
        |> Result.bind (fun d ->
            if d.SurveyId <> expectedSurvey || d.Version <> expectedVersion then Error "the canonical template names another survey or version"
            else Ok(d.Content, b)))

// ---- Published templates -----------------------------------------------------------------------

let private encodeWith (recordType: RecordType) (mutability: Mutability) (id: string) (body: Json) : Result<string, Problem> =
    match RecordId.create id with
    | Ok recordId ->
        { Id = recordId; Type = recordType; SchemaVersion = 1; Mutability = mutability; Body = body }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(id, "the template record is too large"))
    | Error text -> Error(UnstorableRecord(text, "not a record id"))

/// A stored published template, with the dataset it belongs to.
[<NoComparison>]
type StoredPublished = { DatasetId: string; Template: Published }

let encodePublished (datasetId: string) (t: Published) : Result<string, Problem> =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "surveyId", Json.String t.SurveyId
          "version", Json.String t.Version
          "hash", Json.String t.Hash
          "canonical", Json.String(canonicalText t.SurveyId t.Version t.Content)
          "parent", parentJson t.Parent
          "publishedAt", Json.String(t.PublishedAt.ToString("O", CultureInfo.InvariantCulture))
          "publishedBy", Json.String t.PublishedBy
          "manifest",
          Json.Array(t.Manifest |> List.map (fun m -> Json.objectOf [ "fixture", Json.String m.FixtureId; "expectedResultHash", Json.String m.ExpectedResultHash; "passed", Json.Bool m.PassedAtPublication ]))
          "fixtures", Json.Array(t.Fixtures |> List.map fixtureJson) ]
    |> encodeWith publishedType Mutability.Immutable (publishedId t)

let publishedOfBody (value: Json) : Decoded<StoredPublished> =
    let manifestEntry (m: Json) =
        match text "fixture" m, text "expectedResultHash" m, flag "passed" m with
        | Ok f, Ok h, Ok p -> Ok { FixtureId = f; ExpectedResultHash = h; PassedAtPublication = p }
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e

    let at =
        text "publishedAt" value
        |> Result.bind (fun t ->
            match DateTimeOffset.TryParseExact(t, "O", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, parsed -> Ok parsed
            | _ -> Error "'publishedAt' is not a timestamp")

    closed [ "canonical"; "datasetId"; "fixtures"; "hash"; "manifest"; "parent"; "publishedAt"; "publishedBy"; "surveyId"; "version" ] value
    |> Result.bind (fun () ->
        match both (text "datasetId" value) (both (text "surveyId" value) (text "version" value)), text "hash" value, parentOf value, both at (text "publishedBy" value) with
        | Ok(datasetId, (surveyId, version)), Ok hash, Ok parent, Ok(publishedAt, publishedBy) ->
            match text "canonical" value |> Result.bind (contentOf surveyId version), list "manifest" manifestEntry value, list "fixtures" fixtureOf value with
            | Ok(content, _), Ok manifest, Ok fixtures ->
                // The bytes decide the identity: a record whose content does not hash to its hash is refused.
                let actual = TemplateCanonical.templateHash surveyId version content

                // ADM-046: a stored expression is held to the same limits as at publication.
                let tooComplex =
                    match content.Results.Overall with
                    | Some(ResultModel.Custom custom) ->
                        Expression.check Validation.defaultPolicy.ExpressionLimits content "overall" custom |> List.exists (fun f -> f.Code = "EXPR-TOO-COMPLEX")
                    | _ -> false

                if actual <> hash then
                    Error $"the canonical template hashes to {actual}, not {hash}"
                elif tooComplex then
                    Error "its custom scoring expression exceeds the complexity limits"
                else
                    Ok
                        { DatasetId = datasetId
                          Template =
                            { SurveyId = surveyId
                              Version = version
                              Hash = hash
                              Content = content
                              Parent = parent
                              PublishedAt = publishedAt
                              PublishedBy = publishedBy
                              Manifest = manifest
                              Fixtures = fixtures } }
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

let publishedReader: Loading.RecordReader<StoredPublished> =
    { Type = publishedType
      Schema = schemaOf publishedType
      MaxBytes = Record.DefaultMaxBytes
      Decode = publishedOfBody
      IdOf = fun stored -> publishedId stored.Template
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

// ---- Drafts ------------------------------------------------------------------------------------

/// The version a draft's canonical form is written at: it has none yet.
[<Literal>]
let DraftVersion = "draft"

[<NoComparison>]
type StoredDraft = { DatasetId: string; Draft: Draft }

let encodeDraft (datasetId: string) (d: Draft) : Result<string, Problem> =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "surveyId", Json.String d.SurveyId
          "parent", parentJson d.Parent
          "canonical", Json.String(canonicalText d.SurveyId DraftVersion d.Content)
          "fixtures", Json.Array(d.Fixtures |> List.map fixtureJson) ]
    |> encodeWith draftType Mutability.Mutable (draftId d.SurveyId)

let draftOfBody (value: Json) : Decoded<StoredDraft> =
    closed [ "canonical"; "datasetId"; "fixtures"; "parent"; "surveyId" ] value
    |> Result.bind (fun () ->
        match both (text "datasetId" value) (text "surveyId" value), parentOf value, list "fixtures" fixtureOf value with
        | Ok(datasetId, surveyId), Ok parent, Ok fixtures ->
            text "canonical" value
            |> Result.bind (contentOf surveyId DraftVersion)
            |> Result.map (fun (content, _) -> { DatasetId = datasetId; Draft = { SurveyId = surveyId; Parent = parent; Content = content; Fixtures = fixtures } })
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e)

let draftReader: Loading.RecordReader<StoredDraft> =
    { Type = draftType
      Schema = schemaOf draftType
      MaxBytes = Record.DefaultMaxBytes
      Decode = draftOfBody
      IdOf = fun stored -> draftId stored.Draft.SurveyId
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

// ---- The catalog's visibility -------------------------------------------------------------------

[<NoComparison>]
type StoredCatalog = { DatasetId: string; Hidden: Set<string * string> }

let encodeCatalog (datasetId: string) (hidden: Set<string * string>) : Result<string, Problem> =
    Json.objectOf
        [ "datasetId", Json.String datasetId
          "hidden", Json.Array(hidden |> Set.toList |> List.map (fun (s, v) -> Json.objectOf [ "surveyId", Json.String s; "version", Json.String v ])) ]
    |> encodeWith catalogType Mutability.Mutable CatalogId

let catalogOfBody (value: Json) : Decoded<StoredCatalog> =
    closed [ "datasetId"; "hidden" ] value
    |> Result.bind (fun () -> both (text "datasetId" value) (list "hidden" (fun h -> both (text "surveyId" h) (text "version" h)) value))
    |> Result.map (fun (datasetId, hidden) -> { DatasetId = datasetId; Hidden = Set.ofList hidden })

let catalogReader: Loading.RecordReader<StoredCatalog> =
    { Type = catalogType
      Schema = schemaOf catalogType
      MaxBytes = Record.DefaultMaxBytes
      Decode = catalogOfBody
      IdOf = fun _ -> CatalogId
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }

/// The catalog the stored records make: every published template, and the hidden set.
let catalogOf (published: StoredPublished list) (hidden: Set<string * string>) : Catalog =
    { Templates = published |> List.map _.Template |> List.sortBy (fun t -> t.SurveyId, t.PublishedAt)
      Hidden = hidden }

// ---- Review (AUT-006 §§64-65) ------------------------------------------------------------------

/// A draft's review (`signal.template-review`, mutable, one per survey): the
/// author asks for review; a reviewer approves the exact content in front
/// of them. An edit after approval changes the content hash, so the approval
/// no longer covers it.
let reviewType = typeOf "signal.template-review"

type ReviewState =
    | ReadyForReview
    | Reviewed of reviewer: string

[<NoComparison>]
type StoredReview =
    { DatasetId: string
      SurveyId: string
      /// The draft content's hash the state is about.
      ContentHash: string
      State: ReviewState }

/// The hash a review covers: the draft's content at the draft version.
let draftContentHash (d: Draft) = TemplateCanonical.templateHash d.SurveyId DraftVersion d.Content

let reviewId (surveyId: string) = idFor ("review:" + surveyId)
let reviewPath surveyId = pathOf reviewType (reviewId surveyId)

let encodeReview (r: StoredReview) : Result<string, Problem> =
    Json.objectOf
        [ "datasetId", Json.String r.DatasetId
          "surveyId", Json.String r.SurveyId
          "contentHash", Json.String r.ContentHash
          "state", Json.String(match r.State with ReadyForReview -> "ready-for-review" | Reviewed _ -> "reviewed")
          "reviewer", (match r.State with Reviewed who -> Json.String who | ReadyForReview -> Json.Null) ]
    |> encodeWith reviewType Mutability.Mutable (reviewId r.SurveyId)

let reviewOfBody (value: Json) : Decoded<StoredReview> =
    closed [ "contentHash"; "datasetId"; "reviewer"; "state"; "surveyId" ] value
    |> Result.bind (fun () -> both (both (text "datasetId" value) (text "surveyId" value)) (both (text "contentHash" value) (text "state" value)))
    |> Result.bind (fun ((datasetId, surveyId), (hash, state)) ->
        let review state = { DatasetId = datasetId; SurveyId = surveyId; ContentHash = hash; State = state }

        match state, field "reviewer" value with
        | "ready-for-review", Ok Json.Null -> Ok(review ReadyForReview)
        | "reviewed", Ok(Json.String who) -> Ok(review (Reviewed who))
        | _ -> Error "not a review state")

let reviewReader: Loading.RecordReader<StoredReview> =
    { Type = reviewType
      Schema = schemaOf reviewType
      MaxBytes = Record.DefaultMaxBytes
      Decode = reviewOfBody
      IdOf = fun stored -> reviewId stored.SurveyId
      DatasetOf = fun stored -> Some stored.DatasetId
      References = fun _ -> [] }
