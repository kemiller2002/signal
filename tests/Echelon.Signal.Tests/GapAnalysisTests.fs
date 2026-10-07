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
    // ADM-001..ADM-076 share one range row; ADM-077 has its own.
    let expected = ledgerGroups |> Set.filter (fun g -> not (g.StartsWith "ADM-") || g = "ADM-077")
    Assert.Equal(183, ledgerGroups.Count)
    Assert.Equal<string list>(List.distinct analysed, analysed)
    Assert.Equal<Set<string>>(expected, Set.ofList analysed)
    Assert.Contains("| ADM-001 to ADM-076 |", analysis)

let private currentSection = analysis.Substring(analysis.IndexOf "## Coverage after this programme")

let private summaryRowIn (text: string) (label: string) =
    let m = Regex.Match(text, $@"(?m)^\| {Regex.Escape label}[^|]*\| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (\d+) \|")
    Assert.True(m.Success, $"summary row '{label}' not found")
    [ for i in 1..5 -> int m.Groups[i].Value ]

let private summaryRow = summaryRowIn analysis

let private countColumn (column: string * string * string -> string) (families: string list) (adm: int * int) =
    let rows =
        statusRows
        |> List.filter (fun (group, _, _) -> families |> List.exists (fun f -> group.StartsWith(f + "-")) && not (group.StartsWith "ADM-"))

    let count status = rows |> List.filter (fun row -> column row = status) |> List.length
    let admPartial, admMissing = adm
    [ count "tested"; count "partial" + admPartial; count "missing" + admMissing; count "n/a" ]

[<Fact>]
let ``the baseline summary counts are the counts of the status rows`` () =
    let core = [ "AST"; "ACR"; "ARP"; "CAN"; "LURL"; "RPT"; "ANS"; "ALG"; "AUT"; "URLC"; "ID"; "VER" ]
    let check label families adm =
        let row = summaryRow label
        Assert.Equal<int list>(countColumn (fun (_, baseline, _) -> baseline) families adm, row.Tail)
        Assert.Equal(List.sum row.Tail, row.Head)

    check "Core survey engine" core (0, 0)
    check "Advanced stress trial" [ "ARX" ] (0, 0)
    check "Scoring and selector completeness" [ "SCS" ] (0, 0)
    // ADM: ADM-077 has its own row; ADM-001..076 are one missing range.
    check "Administrator console" [ "ADM" ] (1, 76)

[<Fact>]
let ``the current summary counts are the counts of the current column`` () =
    let core = [ "AST"; "ACR"; "ARP"; "CAN"; "LURL"; "RPT"; "ANS"; "ALG"; "AUT"; "URLC"; "ID"; "VER" ]
    let check label families adm =
        let row = summaryRowIn currentSection label
        Assert.Equal<int list>(countColumn (fun (_, _, current) -> current) families adm, row.Tail)
        Assert.Equal(List.sum row.Tail, row.Head)

    check "Core survey engine" core (0, 0)
    check "Advanced stress trial" [ "ARX" ] (0, 0)
    check "Scoring and selector completeness" [ "SCS" ] (0, 0)
    check "Administrator console" [ "ADM" ] (1, 76)
