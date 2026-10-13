/// The requirement gap analysis stays complete and internally consistent:
/// every requirement group in the migration ledger has exactly one row, and
/// the summary counts are the counts of those rows.
module Echelon.Signal.Tests.GapAnalysisTests

open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Tests.Support

let private ledger = readRepoFile "docs/requirements/survey-engine-requirements-migration.md"
let private analysis = readRepoFile "docs/requirements/implementation-gap-analysis.md"

let private groupPattern = @"(?:AST|ACR|ARP|CAN|LURL|RPT|ANS|ALG|AUT|URLC|ID|VER|ARX|ADM|SCS)-\d{3}"

/// Group ids that open a table row (`| GROUP |`) in a document.
let private rowGroups (text: string) =
    Regex.Matches(text, $@"(?m)^\| ({groupPattern}) \|")
    |> Seq.map _.Groups[1].Value
    |> Seq.toList

/// Status rows of the analysis: group, baseline, current.
let private statusRows =
    Regex.Matches(analysis, $@"(?m)^\| ({groupPattern}) \| (tested|partial|missing|n/a) \| (tested|partial|missing|n/a) \|")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
    |> Seq.toList

[<Fact>]
let ``every ledger requirement group has exactly one status row`` () =
    let ledgerGroups = rowGroups ledger |> Set.ofList
    let analysed = statusRows |> List.map (fun (group, _, _) -> group)
    // Every group, ADM included, has its own row (since WI-0038).
    Assert.Equal(183, ledgerGroups.Count)
    Assert.Equal<string list>(List.distinct analysed, analysed)
    Assert.Equal<Set<string>>(ledgerGroups, Set.ofList analysed)

let private currentSection = analysis.Substring(analysis.IndexOf "## Coverage after this programme")

let private summaryRowIn (text: string) (label: string) =
    let m = Regex.Match(text, $@"(?m)^\| {Regex.Escape label}[^|]*\| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (\d+) \|")
    Assert.True(m.Success, $"summary row '{label}' not found")
    [ for i in 1..5 -> int m.Groups[i].Value ]

let private summaryRow = summaryRowIn analysis

let private countColumn (column: string * string * string -> string) (families: string list) =
    let rows =
        statusRows
        |> List.filter (fun (group, _, _) -> families |> List.exists (fun f -> group.StartsWith(f + "-")))

    let count status = rows |> List.filter (fun row -> column row = status) |> List.length
    [ count "tested"; count "partial"; count "missing"; count "n/a" ]

[<Fact>]
let ``the baseline summary counts are the counts of the status rows`` () =
    let core = [ "AST"; "ACR"; "ARP"; "CAN"; "LURL"; "RPT"; "ANS"; "ALG"; "AUT"; "URLC"; "ID"; "VER" ]
    let check label families =
        let row = summaryRow label
        Assert.Equal<int list>(countColumn (fun (_, baseline, _) -> baseline) families, row.Tail)
        Assert.Equal(List.sum row.Tail, row.Head)

    check "Core survey engine" core
    check "Advanced stress trial" [ "ARX" ]
    check "Scoring and selector completeness" [ "SCS" ]
    check "Administrator console" [ "ADM" ]

[<Fact>]
let ``the current summary counts are the counts of the current column`` () =
    let core = [ "AST"; "ACR"; "ARP"; "CAN"; "LURL"; "RPT"; "ANS"; "ALG"; "AUT"; "URLC"; "ID"; "VER" ]
    let check label families =
        let row = summaryRowIn currentSection label
        Assert.Equal<int list>(countColumn (fun (_, _, current) -> current) families, row.Tail)
        Assert.Equal(List.sum row.Tail, row.Head)

    check "Core survey engine" core
    check "Advanced stress trial" [ "ARX" ]
    check "Scoring and selector completeness" [ "SCS" ]
    check "Administrator console" [ "ADM" ]

/// Every group that is not yet `tested` (and not `n/a`) is planned: an open
/// work item in the Praxis queue (captured, ready, active or blocked) names
/// it, directly (`ADM-052`) or inside a range of the same family
/// (`ADM-008..011`). A gap that no open work item names would be silently dropped from
/// the backlog.
let private openWorkText =
    use queue = System.Text.Json.JsonDocument.Parse(readRepoFile ".ros/work/queue.json")

    queue.RootElement.GetProperty("items").EnumerateArray()
    |> Seq.filter (fun item ->
        match item.GetProperty("status").GetString() with
        | "complete"
        | "abandoned" -> false
        | _ -> true)
    |> Seq.map (fun item ->
        let text (name: string) =
            match item.TryGetProperty name with
            | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> string (value.GetString())
            | _ -> ""

        text "title" + "\n" + text "description")
    |> String.concat "\n"

let private plannedGroups =
    let direct = Regex.Matches(openWorkText, groupPattern) |> Seq.map _.Value

    let ranges =
        Regex.Matches(openWorkText, @"\b([A-Z]+)-(\d{3})\.\.(?:[A-Z]+-)?(\d{3})")
        |> Seq.collect (fun m ->
            let family, low, high = m.Groups[1].Value, int m.Groups[2].Value, int m.Groups[3].Value
            seq { for n in low..high -> sprintf "%s-%03d" family n })

    Seq.append direct ranges |> Set.ofSeq

[<Fact>]
let ``every group that is not yet tested is named by an open work item`` () =
    let open' =
        statusRows
        |> List.filter (fun (_, _, current) -> current = "partial" || current = "missing")
        |> List.map (fun (group, _, _) -> group)

    let unplanned = open' |> List.filter (plannedGroups.Contains >> not)
    Assert.Empty unplanned

/// A row marked tested cites tests that exist (WI-0081): at least one test
/// file in backticks (`ImportTests`), each cited file exists with at least
/// one fact, and each cited test (`ImportTests: test name`) is a test in that
/// file. A citation that names no real test fails, so a row cannot be marked
/// tested on prose alone.
let private testSource (file: string) =
    let path = repoFile $"tests/Echelon.Signal.Tests/{file}.fs"
    if System.IO.File.Exists path then Some(System.IO.File.ReadAllText path) else None

let private citations (row: string) =
    Regex.Matches(row, @"`([A-Z][A-Za-z0-9]*Tests)(?:: ([^`]+))?`")
    |> Seq.map (fun m -> m.Groups[1].Value, (if m.Groups[2].Success then Some m.Groups[2].Value else None))
    |> Seq.toList

let private testedRows =
    Regex.Matches(analysis, $@"(?m)^\| ({groupPattern}) \| (?:tested|partial|missing|n/a) \| tested \|(.*)$")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
    |> Seq.toList

[<Fact>]
let ``every tested row cites at least one test, and every cited test exists`` () =
    let problems =
        [ for group, row in testedRows do
              let cited = citations row

              if cited.IsEmpty then
                  $"{group}: cites no test file"

              for file, name in cited do
                  match testSource file, name with
                  | None, _ -> $"{group}: {file}.fs does not exist"
                  | Some text, _ when not (text.Contains "[<Fact>]") -> $"{group}: {file}.fs has no tests"
                  | Some text, Some n when not (text.Contains("``" + n + "``")) -> $"{group}: {file} has no test '{n}'"
                  | _ -> () ]

    Assert.True(problems.IsEmpty, String.concat "\n" problems)

[<Fact>]
let ``the citation check fails a row that cites nothing or a test that does not exist`` () =
    Assert.Empty(citations "No test is named here.")
    Assert.Equal<(string * string option) list>([ "ImportTests", None ], citations "Evidence (`ImportTests`).")

    match citations "(`ImportTests: no such test`)" with
    | [ file, Some name ] ->
        let text = (testSource file).Value
        Assert.False(text.Contains("``" + name + "``"))
    | other -> failwith $"%A{other}"

    Assert.Equal(None, testSource "NoSuchTests")
