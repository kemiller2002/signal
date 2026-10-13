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

/// Reads RFC 4180 text into records of cells: quoted cells may hold commas,
/// line breaks and doubled quotes. Records end at a line feed (a carriage
/// return before it is dropped); a final line ending adds no record.
let parse (text: string) : string list list =
    let rec go (i: int) (cell: Text.StringBuilder) (cells: string list) (records: string list list) (quoted: bool) =
        let endCell () = cell.ToString() :: cells

        if i >= text.Length then
            let records = if cell.Length = 0 && cells.IsEmpty then records else List.rev (endCell ()) :: records
            List.rev records
        else
            match quoted, text[i] with
            | true, '"' when i + 1 < text.Length && text[i + 1] = '"' -> go (i + 2) (cell.Append '"') cells records true
            | true, '"' -> go (i + 1) cell cells records false
            | true, c -> go (i + 1) (cell.Append c) cells records true
            | false, '"' when cell.Length = 0 -> go (i + 1) cell cells records true
            | false, ',' -> go (i + 1) (Text.StringBuilder()) (endCell ()) records false
            | false, '\r' when i + 1 < text.Length && text[i + 1] = '\n' -> go (i + 1) cell cells records false
            | false, '\n' -> go (i + 1) (Text.StringBuilder()) [] (List.rev (endCell ()) :: records) false
            | false, c -> go (i + 1) (cell.Append c) cells records false

    go 0 (Text.StringBuilder()) [] [] false

/// A stored CSV as it is delivered: every cell rewritten by this writer
/// (DF-SIGNAL-2026-0017). Text this writer produced comes back byte for
/// byte; anything that would have run as a formula comes back neutralized.
/// Whether delivery changed it is returned with it.
let neutralize (text: string) : string * bool =
    let rewritten = (parse text |> List.map row |> String.concat "\n") + "\n"
    rewritten, rewritten <> text
