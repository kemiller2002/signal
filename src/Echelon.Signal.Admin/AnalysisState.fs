/// Analysis state, saved analyses and reversible administration (ADM-019,
/// ADM-032, ADM-052, ADM-046).
///
/// - An `AnalysisState` is the explicit, declarative state every derived
///   view is reproduced from: groups, sections, measures, calculated
///   expressions, filter and sort. It is versioned, free of credentials and
///   personal data, and fits in the administrator URL (`an=` fragment with an
///   integrity check) for bookmarks and back/forward.
/// - A saved analysis is a named `AnalysisState` stored as a mutable record
///   under its revision.
/// - Expressions read from a URL or a record are untrusted: they are parsed
///   within the JSON depth limit and must pass the expression limits.
/// - Administration is reversible: every roster command has an inverse
///   that restores the previous state as a new change, never by rewriting
///   history.
///
/// Pure.
module Echelon.Signal.Admin.AnalysisState

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Arca
open Echelon.Signal.Admin.Analysis
open Echelon.Signal.Admin.Codec

[<Literal>]
let StateVersion = 1

/// Count or percentage display (ADM-019's toggle).
type Display =
    | Counts
    | Percentages

type AnalysisState =
    { Version: int
      /// Group keys.
      Groups: string list
      Sections: string list
      Measures: Measure list
      /// Named calculated measures.
      Expressions: (string * Expr) list
      Filter: string
      SortBy: string
      Display: Display }

let empty =
    { Version = StateVersion
      Groups = []
      Sections = []
      Measures = []
      Expressions = []
      Filter = ""
      SortBy = ""
      Display = Counts }

// ---- Codec ----------------------------------------------------------------------------------

let private measures =
    [ AcceptedCount; CompletionRate; Coverage; Mean; Median; Minimum; Maximum; StandardDeviation; InterquartileRange
      MedianAbsoluteDeviation; ConfidenceLower95; ConfidenceUpper95; CeilingShare; FloorShare; DontKnowRate
      NotApplicableRate; MissingRate; CronbachAlpha; RecommendationFrequency ]

let private measureOf (name: string) : Decoded<Measure> =
    if name.StartsWith "Percentile" then
        match Int32.TryParse(name.Substring 10) with
        | true, p when p >= 1 && p <= 100 -> Ok(Percentile p)
        | _ -> Error $"'{name}' is not a measure"
    else
        measures |> List.tryFind (fun m -> measureName m = name) |> Option.map Ok |> Option.defaultValue (Error $"'{name}' is not a measure")

let private real (value: float) = Json.String(value.ToString("R", CultureInfo.InvariantCulture))

let rec private exprJson (expr: Expr) =
    let binary op a b = Json.objectOf [ "op", Json.String op; "a", exprJson a; "b", exprJson b ]

    match expr with
    | Const value -> Json.objectOf [ "op", Json.String "const"; "value", real value ]
    | Ref(m, section) ->
        Json.objectOf [ "op", Json.String "ref"; "measure", Json.String(measureName m); "section", (section |> Option.map Json.String |> Option.defaultValue Json.Null) ]
    | Add(a, b) -> binary "add" a b
    | Sub(a, b) -> binary "sub" a b
    | Mul(a, b) -> binary "mul" a b
    | Div(a, b) -> binary "div" a b
    | Smaller(a, b) -> binary "min" a b
    | Larger(a, b) -> binary "max" a b
    | Round(e, digits) -> Json.objectOf [ "op", Json.String "round"; "e", exprJson e; "digits", Json.Number(decimal digits) ]

/// An expression from JSON: every node checked, depth bounded by the walk.
let rec private exprOf (depth: int) (value: Json) : Decoded<Expr> =
    if depth > MaxDepth then
        Error "the expression is too deep"
    else
        let sub name = field name value |> Result.bind (exprOf (depth + 1))
        let binary make = both (sub "a") (sub "b") |> Result.map make

        match text "op" value with
        | Ok "const" ->
            text "value" value
            |> Result.bind (fun t ->
                match Double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, v when Double.IsFinite v -> Ok(Const v)
                | _ -> Error "'value' is not a finite number")
        | Ok "ref" ->
            let section =
                field "section" value |> Result.bind (function Json.Null -> Ok None | Json.String s -> Ok(Some s) | _ -> Error "'section' is not text")

            both (text "measure" value |> Result.bind measureOf) section |> Result.map Ref
        | Ok "add" -> binary Add
        | Ok "sub" -> binary Sub
        | Ok "mul" -> binary Mul
        | Ok "div" -> binary Div
        | Ok "min" -> binary Smaller
        | Ok "max" -> binary Larger
        | Ok "round" -> both (sub "e") (integer "digits" value) |> Result.map Round
        | Ok other -> Error $"'{other}' is not an operation"
        | Error e -> Error e

/// An untrusted expression: parsed, then held to the limits (ADM-046).
let parseExpression (value: Json) : Decoded<Expr> =
    exprOf 1 value |> Result.bind (fun expr -> validate expr |> Result.map (fun () -> expr))

let toJson (state: AnalysisState) =
    Json.objectOf
        [ "version", Json.Number(decimal state.Version)
          "groups", textArray state.Groups
          "sections", textArray state.Sections
          "measures", textArray (state.Measures |> List.map measureName)
          "expressions", Json.Array(state.Expressions |> List.map (fun (name, e) -> Json.objectOf [ "name", Json.String name; "expr", exprJson e ]))
          "filter", Json.String state.Filter
          "sortBy", Json.String state.SortBy
          "display", Json.String(if state.Display = Percentages then "percentages" else "counts") ]

let ofJson (value: Json) : Decoded<AnalysisState> =
    closed [ "display"; "expressions"; "filter"; "groups"; "measures"; "sections"; "sortBy"; "version" ] value
    |> Result.bind (fun () ->
        let expression (item: Json) = both (text "name" item) (field "expr" item |> Result.bind parseExpression)

        match
            integer "version" value,
            both (texts "groups" value) (texts "sections" value),
            texts "measures" value |> Result.bind (traverse measureOf),
            list "expressions" expression value,
            both (text "filter" value) (text "sortBy" value),
            text "display" value
        with
        | Ok version, _, _, _, _, _ when version <> StateVersion -> Error $"analysis state version {version} is not {StateVersion}"
        | Ok version, Ok(groups, sections), Ok measures, Ok expressions, Ok(filter, sortBy), Ok display ->
            Ok
                { Version = version
                  Groups = groups
                  Sections = sections
                  Measures = measures
                  Expressions = expressions
                  Filter = filter
                  SortBy = sortBy
                  Display = if display = "percentages" then Percentages else Counts }
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e)

// ---- The URL form (ADM-019) ---------------------------------------------------------------------

let private base64 (bytes: byte[]) = Buffers.Text.Base64Url.EncodeToString(ReadOnlySpan bytes)
let private sha256 (text: string) = SHA256.HashData(Encoding.UTF8.GetBytes text)

/// `an=<payload>.<integrity>`: the state, for bookmarks and history.
let fragment (state: AnalysisState) =
    let text = Json.canonicalText (toJson state)
    $"an={base64 (Encoding.UTF8.GetBytes text)}.{base64 ((sha256 text)[0..7])}"

/// The state a fragment carries, or why it cannot be used.
let ofFragment (fragment: string) : Decoded<AnalysisState> =
    match fragment.Split('.') with
    | [| head; integrity |] when head.StartsWith "an=" ->
        match Echelon.Signal.Engine.UrlState.tryFromBase64Url (head.Substring 3) with
        | Some bytes when Unicode.Utf8.IsValid(ReadOnlySpan bytes) ->
            let text = Encoding.UTF8.GetString bytes

            if base64 ((sha256 text)[0..7]) <> integrity then Error "the analysis link was altered"
            else Json.parse text |> Result.mapError JsonError.describe |> Result.bind ofJson
        | _ -> Error "the analysis link is not readable"
    | _ -> Error "not an analysis link"

// ---- Saved analyses (ADM-032, ADM-052) -----------------------------------------------------------

let recordType =
    match RecordType.create "signal.saved-analysis" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// A saved analysis: a stable id, a display name, the state, a revision.
type Saved =
    { Id: string
      Name: string
      State: AnalysisState
      Revision: int }

let encodeSaved (datasetId: string) (saved: Saved) : Result<RelativePath * string, Problems.Problem> =
    match RecordId.create saved.Id with
    | Error text -> Error(Problems.UnstorableRecord(text, "not a saved-analysis id"))
    | Ok id ->
        let key = { Type = recordType; Partition = []; Id = id }

        match Layout.recordPath key with
        | Error e -> Error(Problems.InvalidDataLocation(LocationError.describe e))
        | Ok path ->
            { Id = id
              Type = recordType
              SchemaVersion = schema.Current
              Mutability = Mutability.Mutable
              Body =
                Json.objectOf
                    [ "datasetId", Json.String datasetId
                      "id", Json.String saved.Id
                      "name", Json.String saved.Name
                      "state", toJson saved.State
                      "revision", Json.Number(decimal saved.Revision) ] }
            |> Record.encode Record.DefaultMaxBytes
            |> Result.map (fun text -> path, text)
            |> Result.mapError (fun _ -> Problems.UnstorableRecord(saved.Id, "the saved analysis is too large"))

let savedOfBody (value: Json) : Decoded<string * Saved> =
    closed [ "datasetId"; "id"; "name"; "revision"; "state" ] value
    |> Result.bind (fun () ->
        match both (text "datasetId" value) (text "id" value), text "name" value, field "state" value |> Result.bind ofJson, integer "revision" value with
        | Ok(dataset, id), Ok name, Ok state, Ok revision when revision >= 1 -> Ok(dataset, { Id = id; Name = name; State = state; Revision = revision })
        | Ok _, Ok _, Ok _, Ok _ -> Error "'revision' must be at least 1"
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

// ---- Reversible administration (ADM-052) ----------------------------------------------------------

/// The command that undoes a roster command, given the roster it applied
/// to: applying it is a new change on top of the current roster.
let inverse (before: Access.Roster) (command: Access.RosterCommand) : Access.RosterCommand option =
    match command with
    | Access.Admit(principal, _) -> Some(Access.Remove principal.PrincipalId)
    | Access.Grant(principalId, capability) when not ((Access.capabilitiesOf before principalId).Contains capability) ->
        Some(Access.Revoke(principalId, capability))
    | Access.Revoke(principalId, capability) when (Access.capabilitiesOf before principalId).Contains capability ->
        Some(Access.Grant(principalId, capability))
    | Access.Remove principalId ->
        before.Members.TryFind principalId |> Option.map (fun m -> Access.Admit(m.Principal, m.Capabilities))
    | Access.Grant _
    | Access.Revoke _ -> None
