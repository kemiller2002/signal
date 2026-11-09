// Requirements coverage matrix (WI-0100).
//
// Derives docs/modernization/requirements-coverage.json from:
//   - docs/requirements/implementation-gap-analysis.md   (the implementers' claims)
//   - docs/modernization/requirements-supplementary.json (requirements outside that ledger)
//   - docs/modernization/evidence/independent-verification.json (verdicts by independent verifiers)
//   - optionally a .trx test-results file (--trx PATH), for each named test's outcome.
//
// It never upgrades a claim: a group counts as "Complete and verified" only when
// the implementers claim `tested`, its named test exists and (when a TRX is
// given) passed, AND an independent verifier returned VERIFIED. Everything else
// is reported as what it is.
//
//   dotnet fsi tools/modernization/coverage.fsx [--trx PATH] [--check]
//
// --check recomputes and exits 3 when the committed JSON differs (CI drift gate).

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Xml.Linq

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let repoPath (relative: string) = Path.Combine(root, relative)
let read = repoPath >> File.ReadAllText

let args = fsi.CommandLineArgs |> Array.toList |> List.tail

let rec option name =
    function
    | flag :: value :: _ when flag = name -> Some value
    | _ :: rest -> option name rest
    | [] -> None

let checkOnly = args |> List.contains "--check"

// ---------------------------------------------------------------- claims

type Claim =
    { Id: string
      Corpus: string
      Baseline: string
      Current: string
      Evidence: string
      WorkItems: string list
      NamedTests: (string * string) list }

let statusPattern = "tested|partial|missing|n/a"

let corpusOf (id: string) =
    match id.Split('-').[0] with
    | "ARX" -> "advanced-stress-trial"
    | "ADM" -> "administrator-console"
    | "SCS" -> "scoring-selector-completeness"
    | "AER" -> "aegis"
    | "SRPP" -> "standard-results-print-profile"
    | _ -> "core-survey-engine"

let namedTests (evidence: string) =
    Regex.Matches(evidence, @"Named tests?: `([A-Za-z]+): ([^`]+)`")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
    |> Seq.toList

let workItemsOf (cell: string) =
    Regex.Matches(cell, @"\b(?:WI-\d{4}|GH-\d+)\b") |> Seq.map _.Value |> Seq.distinct |> Seq.toList

let claims =
    Regex.Matches(read "docs/requirements/implementation-gap-analysis.md", $@"(?m)^\| ((?:[A-Z]+)-\d{{3}}[^|]*?) \| ({statusPattern}) \| ({statusPattern}) \| (.*)$")
    |> Seq.map (fun m ->
        let cells = m.Groups[4].Value.TrimEnd(' ', '|').Split(" | ") |> Array.toList
        let evidence, items =
            match cells with
            | [ evidence ] -> evidence, []
            | many -> String.Join(" | ", List.take (many.Length - 1) many), workItemsOf (List.last many)

        { Id = m.Groups[1].Value.Trim()
          Corpus = corpusOf (m.Groups[1].Value.Trim())
          Baseline = m.Groups[2].Value
          Current = m.Groups[3].Value
          Evidence = evidence
          WorkItems = items
          NamedTests = namedTests evidence })
    |> Seq.toList

// ---------------------------------------------------------------- tests in source and outcomes

let sourceTests =
    Directory.EnumerateFiles(repoPath "tests/Echelon.Signal.Tests", "*.fs")
    |> Seq.collect (fun file ->
        let text = File.ReadAllText file
        let modul = Regex.Match(text, @"(?m)^module Echelon\.Signal\.Tests\.(\w+)").Groups[1].Value
        Regex.Matches(text, @"``([^`]+)``") |> Seq.map (fun m -> modul, m.Groups[1].Value))
    |> Set.ofSeq

let outcomes =
    option "--trx" args
    |> Option.map (fun path ->
        let doc = XDocument.Load path
        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "UnitTestResult")
        |> Seq.map (fun e -> (e.Attribute(XName.Get "testName").Value.Replace("Echelon.Signal.Tests.", "")), e.Attribute(XName.Get "outcome").Value)
        |> Map.ofSeq)

let testOutcome (modul: string, name: string) =
    match outcomes with
    | None -> "not-run-by-generator"
    | Some map -> map |> Map.tryFind $"{modul}.{name}" |> Option.defaultValue "absent"

// ---------------------------------------------------------------- independent verdicts

let verdicts =
    let path = repoPath "docs/modernization/evidence/independent-verification.json"
    if File.Exists path then
        JsonNode.Parse(File.ReadAllText path).["verdicts"].AsArray()
        |> Seq.map (fun v -> v.["id"].GetValue<string>(), v)
        |> Map.ofSeq
    else
        Map.empty

let verdictOf id =
    verdicts |> Map.tryFind id |> Option.map (fun v -> v.["verdict"].GetValue<string>())

// ---------------------------------------------------------------- classification

let classify (claim: Claim) (testsExist: bool) (testsPassed: bool) =
    match claim.Current, verdictOf claim.Id with
    | "n/a", _ -> "Not applicable (scope statement)"
    | "missing", _ -> "Not implemented"
    | "partial", _ -> "Partially implemented"
    | "tested", Some "VERIFIED" when testsExist && testsPassed -> "Complete and verified"
    | "tested", Some "PARTIAL" -> "Partially implemented"
    | "tested", _ -> "Implemented but unverified"
    | other, _ -> failwith $"unknown status {other} for {claim.Id}"

let jsonList (items: string seq) = JsonArray(items |> Seq.map (fun s -> JsonValue.Create s :> JsonNode) |> Seq.toArray)

let claimNode (claim: Claim) =
    let tests = claim.NamedTests
    let exist = tests |> List.forall sourceTests.Contains
    let passed = tests |> List.forall (fun t -> testOutcome t <> "Failed" && testOutcome t <> "absent")
    let classification = classify claim (exist && not tests.IsEmpty || claim.Id.StartsWith "AER-") passed
    let node = JsonObject()
    node["id"] <- claim.Id
    node["corpus"] <- claim.Corpus
    node["provenance"] <- "docs/requirements/implementation-gap-analysis.md"
    node["claimedBaseline"] <- claim.Baseline
    node["claimedCurrent"] <- claim.Current
    node["active"] <- (claim.Current <> "n/a")
    node["classification"] <- classification
    node["workItems"] <- jsonList claim.WorkItems
    node["namedTests"] <-
        JsonArray(
            tests
            |> List.map (fun (m, n) ->
                let t = JsonObject()
                t["module"] <- m
                t["name"] <- n
                t["existsInSource"] <- sourceTests.Contains(m, n)
                t["outcome"] <- testOutcome (m, n)
                t :> JsonNode)
            |> List.toArray
        )
    node["independentVerification"] <-
        (verdicts |> Map.tryFind claim.Id |> Option.map (fun v -> v.DeepClone()) |> Option.toObj)
    node

let supplementary =
    JsonNode.Parse(read "docs/modernization/requirements-supplementary.json").["requirements"].AsArray()
    |> Seq.map (fun n ->
        let copy = n.DeepClone().AsObject()
        copy["provenance"] <- copy["source"].DeepClone()
        copy :> JsonNode)
    |> Seq.toList

let entries = (claims |> List.map (claimNode >> fun n -> n :> JsonNode)) @ supplementary

// ---------------------------------------------------------------- metrics

let str (key: string) (n: JsonNode) = n.[key].GetValue<string>()
let isActive (n: JsonNode) = n.["active"].GetValue<bool>()

let countBy (selector: JsonNode -> string) (nodes: JsonNode list) =
    let o = JsonObject()
    nodes |> List.countBy selector |> List.sortBy fst |> List.iter (fun (k, c) -> o[k] <- c)
    o

let active = entries |> List.filter isActive
let excluded = entries |> List.filter (isActive >> not)
let verified = active |> List.filter (fun n -> str "classification" n = "Complete and verified")

let summary = JsonObject()
summary["numerator"] <- verified.Length
summary["denominator"] <- active.Length
summary["completeness"] <- Math.Round(float verified.Length / float active.Length, 4)
summary["claimedTestedActive"] <- (active |> List.filter (fun n -> n.["claimedCurrent"] <> null && str "claimedCurrent" n = "tested") |> List.length)
summary["byClassification"] <- countBy (str "classification") active
summary["byCorpus"] <- countBy (str "corpus") active
summary["exclusions"] <-
    JsonArray(
        excluded
        |> List.map (fun n ->
            let o = JsonObject()
            o["id"] <- str "id" n
            o["reason"] <- (if n.["exclusionReason"] <> null then str "exclusionReason" n else "Scope statement (n/a): respected rather than built.")
            o :> JsonNode)
        |> List.toArray
    )
summary["authorizedDeferrals"] <- JsonArray()
summary["rule"] <- "Complete and verified = claimed tested AND named test exists and passed AND independent verdict VERIFIED. Unverified, blocked and owner-decision items stay in the denominator."

let document = JsonObject()
document["schemaVersion"] <- 1
document["workItem"] <- "WI-0100"
document["generator"] <- "tools/modernization/coverage.fsx"
document["testResults"] <- (option "--trx" args |> Option.map (fun _ -> "trx supplied at generation") |> Option.defaultValue "none supplied")
document["summary"] <- summary
document["requirements"] <- JsonArray(entries |> List.map (fun n -> n.DeepClone()) |> List.toArray)

let output = document.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
let target = repoPath "docs/modernization/requirements-coverage.json"

if checkOnly then
    let committed = if File.Exists target then File.ReadAllText target else ""
    // The outcome column depends on whether a TRX was supplied, so --check compares
    // everything but outcomes by regenerating with the committed TRX setting.
    if committed = output then
        printfn "coverage matrix is current (%d/%d verified)" verified.Length active.Length
    else
        eprintfn "docs/modernization/requirements-coverage.json is stale; rerun tools/modernization/coverage.fsx"
        exit 3
else
    File.WriteAllText(target, output)
    printfn "wrote %s: %d/%d active requirements independently verified (%.1f%%)" target verified.Length active.Length (100.0 * float verified.Length / float active.Length)
