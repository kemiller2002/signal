/// Content banking (ARX-013): an instance answers a reproducible subset of a
/// bank. The exact template, the selection algorithm version and the
/// instance's seed reconstruct the same selection, so the selection never
/// needs to be stored separately (ARX-013 "Exact immutable template +
/// SelectionAlgorithmVersion + SelectionSeed MUST reconstruct the same
/// selected content").
///
/// Algorithm version 1: SplitMix64 drives a partial Fisher-Yates shuffle of
/// the pool; the first `Select` drawn are kept, returned in pool order.
module Echelon.Signal.Engine.Banking

[<Literal>]
let SelectionAlgorithmVersion = 1

type Bank =
    { Id: string
      /// Question ids in template order.
      Pool: string list
      Select: int }

/// One SplitMix64 step: the next state and its output.
let private next (state: uint64) =
    let s = state + 0x9E3779B97F4A7C15UL
    let z = (s ^^^ (s >>> 30)) * 0xBF58476D1CE4E5B9UL
    let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
    s, z ^^^ (z >>> 31)

/// FNV-1a over the UTF-8 bytes: a stable per-bank salt. (.NET string hash
/// codes are randomized per process and must never reach a selection.)
let private salt (id: string) =
    System.Text.Encoding.UTF8.GetBytes id
    |> Array.fold (fun (h: uint64) b -> (h ^^^ uint64 b) * 0x100000001B3UL) 0xCBF29CE484222325UL

/// The selected question ids of a bank for a seed, in pool order.
let select (seed: uint64) (bank: Bank) : string list =
    let n = bank.Pool.Length
    let k = max 0 (min bank.Select n)

    let _, order =
        [ 0 .. k - 1 ]
        |> List.fold
            (fun (state, indices: int list) i ->
                let state, r = next state
                let j = i + int (r % uint64 (n - i))
                let a, b = indices[i], indices[j]
                state, indices |> List.updateAt i b |> List.updateAt j a)
            (seed ^^^ salt bank.Id, [ 0 .. n - 1 ])

    order |> List.take k |> List.sort |> List.map (fun i -> bank.Pool[i])

/// Questions of the banks that this seed leaves out: not applicable for
/// this instance.
let excluded (seed: uint64) (banks: Bank list) : Set<string> =
    banks |> List.collect (fun b -> b.Pool |> List.except (select seed b)) |> Set.ofList

let check (banks: Bank list) : string list =
    [ for b in banks do
          if b.Select < 1 || b.Select > b.Pool.Length then
              $"Bank '{b.Id}' selects {b.Select} of {b.Pool.Length}."
          if List.distinct b.Pool <> b.Pool then
              $"Bank '{b.Id}' lists a question twice." ]
