// Limen.Routing (interim copy in Signal): routes, query parameters, percent-encoding and typed values
//
// Copied from kemiller2002/limen libraries/fsharp/Limen.Routing/Routing.fs at
// e935da7 (PR #101, WI-0168), the F# reference library Limen 0.9.0 ships as
// EchelonFoundry.Limen.Routing. It is split into files below Ordo's
// structural-review size, and the cross-file `private` members are
// `internal` (with ModuleSuffix where a module now sits in another file
// from its type, the name the compiler gave it); nothing else differs. When
// Limen 0.9.0 is released, delete this project and reference the package:
// the namespace, modules and signatures are the package's own
// (DF-SIGNAL-2026-0003, WI-0066).
namespace Limen.Routing

open System
open System.Globalization
open System.Text

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Route =
    let private parseType (text: string) =
        match text.Split(':', 2) with
        | [| "string" |] -> Some ParamType.String
        | [| "int" |] -> Some ParamType.Int
        | [| "date" |] -> Some ParamType.Date
        | [| "month" |] -> Some ParamType.Month
        | [| "enum"; values |] when values <> "" -> Some(ParamType.Enum(List.ofArray (values.Split '|')))
        | _ -> None

    let private parseSegment (text: string) : Result<Segment, string> =
        if text.StartsWith "{*" && text.EndsWith "}" && text.Length > 3 then Ok(Segment.Wildcard(text.Substring(2, text.Length - 3)))
        elif text.StartsWith "{" && text.EndsWith "}" then
            let body = text.Substring(1, text.Length - 2)
            match body.IndexOf ':' with
            | -1 when body <> "" -> Ok(Segment.Param(body, ParamType.String))
            | -1 -> Error text
            | index ->
                match parseType (body.Substring(index + 1)) with
                | Some kind when index > 0 -> Ok(Segment.Param(body.Substring(0, index), kind))
                | _ -> Error text
        elif text.Contains '{' || text.Contains '}' then Error text
        else Ok(Segment.Literal text)

    /// "invoices/{id:int}/lines/{line:int}" → segments, or the first segment
    /// that is not one. "" is an index. Types: string (the default), int,
    /// date, month and enum:a|b.
    let tryPath (text: string) : Result<Segment list, string> =
        text.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
        |> List.fold (fun acc piece -> acc |> Result.bind (fun segments -> parseSegment piece |> Result.map (fun s -> s :: segments))) (Ok [])
        |> Result.map List.rev

    let private blank name segments =
        { Name = name
          Path = segments
          Query = []
          Children = []
          Redirect = None
          Guard = None
          Requires = []
          ReturnTarget = true }

    /// A route, or InvalidSegment for a path that does not parse.
    let define name pathText : Result<Route, DefinitionError> =
        tryPath pathText |> Result.map (blank name) |> Result.mapError (fun segment -> DefinitionError.InvalidSegment(name, segment))

    /// A route from a path literal written in code. It throws on a path that
    /// does not parse, as before 0.9.0; Route.define is the total form.
    let create name pathText =
        match tryPath pathText with
        | Ok segments -> blank name segments
        | Error segment -> invalidArg "pathText" $"Unknown parameter segment {segment}"

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module QueryParam =
    let optional name kind = { Name = name; Type = kind; Required = false; Default = None }
    let required name kind = { Name = name; Type = kind; Required = true; Default = None }
    let withDefault value (parameter: QueryParam) = { parameter with Default = Some value }

module internal Text =
    let private strict = UTF8Encoding(false, true)

    let private isUnreserved (b: byte) =
        (b >= byte 'A' && b <= byte 'Z') || (b >= byte 'a' && b <= byte 'z') || (b >= byte '0' && b <= byte '9')
        || b = byte '-' || b = byte '.' || b = byte '_' || b = byte '~'

    /// Every UTF-8 byte except A–Z a–z 0–9 - . _ ~ as %XX. None for text that
    /// is not valid UTF-16 (a lone surrogate).
    let tryEncode (value: string) =
        try
            strict.GetBytes value
            |> Array.map (fun b -> if isUnreserved b then string (char b) else $"%%{int b:X2}")
            |> String.concat ""
            |> Some
        with :? EncoderFallbackException -> None

    let private hex (c: char) =
        if c >= '0' && c <= '9' then Some(int c - int '0')
        elif c >= 'A' && c <= 'F' then Some(int c - int 'A' + 10)
        elif c >= 'a' && c <= 'f' then Some(int c - int 'a' + 10)
        else None

    /// Strict percent-decoding as UTF-8: an invalid escape, a lone surrogate
    /// or invalid UTF-8 is None.
    let decode (plusIsSpace: bool) (value: string) =
        let isPlain (c: char) = c <> '%' && not (plusIsSpace && c = '+')

        let rec bytes index (acc: byte list) =
            if index >= value.Length then Some(List.rev acc)
            else
                match value[index] with
                | '%' when index + 2 < value.Length ->
                    match hex value[index + 1], hex value[index + 2] with
                    | Some high, Some low -> bytes (index + 3) (byte (high * 16 + low) :: acc)
                    | _ -> None
                | '%' -> None
                | '+' when plusIsSpace -> bytes (index + 1) (byte ' ' :: acc)
                | _ ->
                    let run = value.Substring(index) |> Seq.takeWhile isPlain |> Seq.length
                    bytes (index + run) (List.rev (List.ofArray (strict.GetBytes(value.Substring(index, run)))) @ acc)

        try
            bytes 0 [] |> Option.map (fun decoded -> strict.GetString(Array.ofList decoded))
        with
        | :? EncoderFallbackException
        | :? DecoderFallbackException -> None

/// Typed values: conversion from and to their canonical text.
module internal Values =
    let maxSafe = 9007199254740991L

    let private ascii (text: string) = text |> Seq.forall Char.IsAsciiDigit

    let parseInt (text: string) =
        let canonical =
            text = "0"
            || (text.Length > 0
                && (let digits = if text.StartsWith "-" then text.Substring 1 else text
                    digits.Length > 0 && digits.Length <= 16 && digits[0] <> '0' && ascii digits))

        if not canonical then None
        else
            match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, value when abs value <= maxSafe -> Some value
            | _ -> None

    let parseDate (text: string) =
        if text.Length = 10 && text[4] = '-' && text[7] = '-' && ascii (text.Substring(0, 4) + text.Substring(5, 2) + text.Substring(8, 2)) then
            match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, date -> Some date
            | _ -> None
        else None

    let parseMonth (text: string) =
        if text.Length = 7 && text[4] = '-' && ascii (text.Substring(0, 4) + text.Substring(5, 2)) then
            let year = int (text.Substring(0, 4))
            let month = int (text.Substring(5, 2))
            if year >= 1 && month >= 1 && month <= 12 then Some(year, month) else None
        else None

    let typeName kind =
        match kind with
        | ParamType.String -> "string"
        | ParamType.Int -> "int"
        | ParamType.Bool -> "bool"
        | ParamType.Date -> "date"
        | ParamType.Month -> "month"
        | ParamType.Enum _ -> "enum"
        | ParamType.Set _ -> "set"

    let expected kind =
        match kind with
        | ParamType.Enum values -> "one of " + String.concat "|" values
        | ParamType.Set [] -> "a set of non-empty values"
        | ParamType.Set values -> "a set of " + String.concat "|" values
        | other -> typeName other

    let private members (values: string list) (items: string list) =
        if items |> List.exists (fun item -> item = "") then None
        elif not values.IsEmpty && items |> List.exists (fun item -> not (List.contains item values)) then None
        else Some(items |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))

    /// A decoded path segment or query value → a typed value. A set is
    /// converted by convertSet, from its raw (still encoded) text.
    let convert kind (text: string) =
        match kind with
        | ParamType.String -> Some(Value.Text text)
        | ParamType.Int -> parseInt text |> Option.map Value.Integer
        | ParamType.Bool ->
            match text with
            | "true" -> Some(Value.Boolean true)
            | "false" -> Some(Value.Boolean false)
            | _ -> None
        | ParamType.Date -> parseDate text |> Option.map Value.Date
        | ParamType.Month -> parseMonth text |> Option.map Value.Month
        | ParamType.Enum values -> if List.contains text values then Some(Value.Text text) else None
        | ParamType.Set values -> members values (List.ofArray (text.Split ',')) |> Option.map Value.Members

    /// A raw (encoded) query value of a set type: split on ",", then decode
    /// each member, so an encoded comma stays inside its member.
    let convertSet values (raw: string) =
        let pieces = raw.Split ',' |> List.ofArray |> List.map (Text.decode true)
        if pieces |> List.forall Option.isSome then members values (pieces |> List.choose id) |> Option.map Value.Members else None

    let private dateText (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    /// A value → its canonical, percent-encoded text, or None when it is not
    /// a value of that type. An empty set is Some "".
    let render kind (value: Value) =
        let encoded = Text.tryEncode
        match kind, value with
        | ParamType.String, Value.Text text -> encoded text
        | ParamType.Int, Value.Integer number when abs number <= maxSafe -> Some(number.ToString(CultureInfo.InvariantCulture))
        | ParamType.Int, Value.Text text when (parseInt text).IsSome -> Some text
        | ParamType.Bool, Value.Boolean flag -> Some(if flag then "true" else "false")
        | ParamType.Date, Value.Date date -> Some(dateText date)
        | ParamType.Month, Value.Month(year, month) when year >= 1 && year <= 9999 && month >= 1 && month <= 12 -> Some $"%04d{year}-%02d{month}"
        | ParamType.Enum values, Value.Text text when List.contains text values -> encoded text
        | ParamType.Set values, Value.Members items ->
            members values items
            |> Option.bind (fun sorted ->
                let pieces = sorted |> List.map encoded
                if pieces |> List.forall Option.isSome then Some(pieces |> List.choose id |> String.concat ",") else None)
        | _ -> None

    /// Whether two values of one type have the same canonical text.
    let same kind a b =
        match render kind a, render kind b with
        | Some x, Some y -> x = y
        | _ -> false
