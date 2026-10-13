/// The measurement baseline index (WI-0083, docs/measurement/baseline-sources.md)
/// names repository paths as evidence sources; each one must exist, so the
/// index never points a reader at nothing.
module Echelon.Signal.Tests.BaselineSourcesTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Tests.Support

let private index = "docs/measurement/baseline-sources.md"

/// Backticked repository paths in the index: a dotted root folder, a known
/// top-level folder, or a top-level file with an extension.
let private named =
    Regex.Matches(readRepoFile index, "`([^`\\s]+)`")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Seq.filter (fun p ->
        Regex.IsMatch(p, "^(\\.ros|\\.praxis|\\.conditor|\\.github|docs|research|registries|vendor|telemetry)/") || Regex.IsMatch(p, "^[a-z-]+\\.(json|md)$"))
    |> Seq.distinct
    |> List.ofSeq

/// A glob is checked by the folder it ranges over; anything else must exist as named.
let private exists (path: string) =
    let full = repoFile path

    if path.Contains "*" then
        Directory.Exists(Path.GetDirectoryName(repoFile (path.Substring(0, path.IndexOf '*'))))
    else
        File.Exists full || Directory.Exists full

[<Fact>]
let ``every path the baseline index names exists`` () =
    Assert.True(named.Length >= 10, $"%A{named}")

    for path in named do
        Assert.True(exists path, $"'{path}' is named in {index} but does not exist")
