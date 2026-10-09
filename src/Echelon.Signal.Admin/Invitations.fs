/// Issuing respondent links for a group (ID-001, LURL-002, VER-003): one
/// link per invitation, each with a fresh random instance id, the group's
/// id and, for a generic template, the invitation's optional locale and last
/// day (link format 2, DF-SIGNAL-2026-0006).
///
/// Nothing about a person is in a link or stored here: for an identified
/// group the administrator keeps, outside Signal, which link went to whom.
/// Pure: the instance ids arrive as values.
module Echelon.Signal.Admin.Invitations

open System
open System.Globalization
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import

/// The most links one request issues.
[<Literal>]
let MaximumCount = 500

/// The survey pages, relative to the console (web/admin/).
[<Literal>]
let SurveyPage = "../survey/"

[<Literal>]
let PilotPage = "../"

/// What the person typed: how many, a language tag, a last day (yyyy-MM-dd).
type Request =
    { Count: string
      Locale: string
      Expires: string }

let emptyRequest = { Count = "1"; Locale = ""; Expires = "" }

/// The count and terms a request asks for, or why it cannot be issued.
let parse (request: Request) : Result<int * GenericEnvelope.Terms, string> =
    let locale = request.Locale.Trim()
    let expires = request.Expires.Trim()

    match Int32.TryParse(request.Count.Trim(), NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, count when count >= 1 && count <= MaximumCount ->
        let day =
            if expires = "" then Ok None
            else
                match DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, day -> Ok(Some day)
                | _ -> Error "Give the last day as a date (yyyy-MM-dd)."

        day
        |> Result.bind (fun day ->
            let terms: GenericEnvelope.Terms = { Locale = (if locale = "" then None else Some locale); ExpiresOn = day }

            match GenericEnvelope.termsProblem terms with
            | Some problem -> Error problem
            | None -> Ok(count, terms))
    | _ -> Error $"Ask for between 1 and {MaximumCount} links."

/// The links, relative to the console, for fresh instance ids; or why the
/// group cannot have them. `today` refuses a last day already past.
let links (definition: GroupDefinition) (terms: GenericEnvelope.Terms) (today: DateOnly) (instances: OpaqueId list) : Result<string list, string> =
    let binding instance = GenericEnvelope.invitationBinding definition.Mode instance definition.Group

    match terms.ExpiresOn, definition.Generic with
    | Some day, _ when day < today -> Error "The last day is already past."
    | _, Some form ->
        Ok(
            instances
            |> List.map (fun instance ->
                let payload = GenericEnvelope.encodeWith form.Content form.Reference terms { Binding = binding instance; Answers = Map.empty }
                $"{SurveyPage}#{LiveUrl.FragmentKey}={payload}")
        )
    | _, None when terms <> GenericEnvelope.noTerms -> Error "The pilot assessment's links cannot carry a language or a last day."
    | _, None ->
        Ok(instances |> List.map (fun instance -> PilotPage + LiveUrl.fragment definition.Template { Binding = binding instance; Answers = Map.empty }))

/// The links as a file to keep and send from: a number per link, nothing else.
let csv (links: string list) =
    "invitation,link\n" + (links |> List.mapi (fun i link -> $"{i + 1},{link}") |> String.concat "\n") + "\n"
