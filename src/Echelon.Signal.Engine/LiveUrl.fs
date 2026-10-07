/// The live respondent URL (LURL-001, ARX-007): where in the URL the answer
/// state lives, and how a URL is read back into a resume decision.
///
/// The state goes in the fragment (`#r=<envelope>`), never the path or query:
/// browsers do not send the fragment to the server or in a Referer header, so
/// answers do not leave the respondent's machine by being loaded (ARX-007
/// fragment-first placement). Pure: the URL arrives and leaves as text through
/// Limen's navigation, and nothing here touches the browser.
module Echelon.Signal.Engine.LiveUrl

open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.UrlState

[<Literal>]
let FragmentKey = "r"

/// What a URL's fragment says about saved answers.
[<NoComparison>]
type Resume =
    /// No saved answers in the URL: a fresh start.
    | NoSavedState
    | Saved of Envelope
    /// The URL carries saved answers that cannot be read; the reason is
    /// explicit and nothing is guessed (URLC-003 §6).
    | Unreadable of DecodeError

/// The fragment, including `#`, for an envelope.
let fragment (assessment: Assessment) (envelope: Envelope) = $"#{FragmentKey}={encode assessment envelope}"

/// Reads a fragment (`location.hash`, with or without the leading `#`).
/// Parameters other than `r` are ignored; a repeated `r` is ambiguous and
/// refused rather than resolved by position.
let read (assessment: Assessment) (hash: string) : Resume =
    let body = if hash.StartsWith "#" then hash.Substring 1 else hash

    let values =
        body.Split('&')
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| key; value |] when key = FragmentKey -> Some value
            | _ -> None)

    match values with
    | [||] -> NoSavedState
    | [| value |] ->
        match decode assessment value with
        | Ok envelope -> Saved envelope
        | Error error -> Unreadable error
    | _ -> Unreadable NotBase64Url

/// The same-origin, root-relative URL that carries an envelope: the current
/// path and query, with the fragment replaced.
let urlFor (assessment: Assessment) (path: string) (query: string) (envelope: Envelope) =
    let path = if path.StartsWith "/" then path else "/" + path
    path + query + fragment assessment envelope
