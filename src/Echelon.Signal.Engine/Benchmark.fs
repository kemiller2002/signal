/// Norm-referenced scores against an immutable, versioned reference
/// distribution (SCS-007): percentile rank, z-score and T-score.
///
/// A benchmark is identified by its content hash; every norm score records
/// which benchmark produced it. With no usable benchmark the result is
/// unavailable, never fabricated. Norm scores are a separate type from
/// criterion scores so the two cannot be mixed by accident.
module Echelon.Signal.Engine.Benchmark

open System
open System.Security.Cryptography
open System.Text

type Benchmark =
    { Id: string
      Version: string
      /// The reference distribution's values.
      Values: float list }

/// SHA-256 over the canonical text of the benchmark: identity, version and
/// the sorted values in round-trip form.
let hash (b: Benchmark) =
    let values = b.Values |> List.sort |> List.map (fun v -> v.ToString("R", Globalization.CultureInfo.InvariantCulture))
    let text = String.concat "\n" ([ "benchmark/1"; b.Id; b.Version ] @ values)
    "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

type NormScore =
    { Value: float
      BenchmarkId: string
      BenchmarkVersion: string
      BenchmarkHash: string }

type Unavailable =
    | EmptyBenchmark
    /// Every reference value is the same, so a z-score is undefined.
    | ZeroStandardDeviation

let private stamp (b: Benchmark) value =
    { Value = value
      BenchmarkId = b.Id
      BenchmarkVersion = b.Version
      BenchmarkHash = hash b }

/// Percentage of reference values below the score, counting ties as half
/// (mid-rank), with no interpolation between reference values.
let percentileRank (b: Benchmark) (score: float) : Result<NormScore, Unavailable> =
    match b.Values with
    | [] -> Error EmptyBenchmark
    | values ->
        let below = values |> List.filter (fun v -> v < score) |> List.length
        let equal = values |> List.filter (fun v -> v = score) |> List.length
        Ok(stamp b (100.0 * (float below + 0.5 * float equal) / float values.Length))

/// (score - mean) / population standard deviation.
let zScore (b: Benchmark) (score: float) : Result<NormScore, Unavailable> =
    match b.Values with
    | [] -> Error EmptyBenchmark
    | values ->
        let mean = List.average values
        let sd = sqrt (values |> List.averageBy (fun v -> (v - mean) ** 2.0))

        if sd = 0.0 then Error ZeroStandardDeviation else Ok(stamp b ((score - mean) / sd))

/// A declared linear transform of the z-score.
type TScale =
    /// The conventional T-score: 50 + 10z, used only when selected.
    | Conventional
    | Linear of mean: float * spread: float

let tScore (scale: TScale) (b: Benchmark) (score: float) : Result<NormScore, Unavailable> =
    let mean, spread =
        match scale with
        | Conventional -> 50.0, 10.0
        | Linear(m, s) -> m, s

    zScore b score |> Result.map (fun z -> { z with Value = mean + spread * z.Value })
