/// Every CSV Signal writes (WI-0084): spreadsheet-safe and RFC 4180.
///
/// A text cell that starts with =, +, -, @, a tab or a carriage return is
/// read by spreadsheets as a formula (CSV injection), so it gets a leading
/// single quote and is kept as text. A cell that is a number keeps its
/// number, a leading minus included. A cell holding a comma, a double quote,
/// a line feed or a carriage return is quoted, with its quotes doubled
/// (RFC 4180).
///
/// Pure.
module Echelon.Signal.Engine.Csv

open System
open System.Globalization

let private formulaStarts = [| '='; '+'; '-'; '@'; '\t'; '\r' |]

/// Whether a cell is a plain number (invariant culture), such as -1.5 or 42.
let private isNumber (text: string) =
    match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, value -> text.Trim() = text && Double.IsFinite value
    | _ -> false

/// One cell, neutralized and quoted.
let field (text: string) =
    let neutral =
        if text.Length > 0 && Array.contains text[0] formulaStarts && not (isNumber text) then "'" + text else text

    if neutral.IndexOfAny([| ','; '"'; '\n'; '\r' |]) >= 0 then "\"" + neutral.Replace("\"", "\"\"") + "\"" else neutral

/// One record.
let row (cells: string list) = cells |> List.map field |> String.concat ","

/// A whole file: a header and records, each line ending in a line feed.
let table (header: string list) (rows: string list list) = (header :: rows) |> List.map row |> String.concat "\n" |> fun body -> body + "\n"
