/// Which banked questions an instance answers (ARX-013, DF-SIGNAL-2026-0009).
///
/// The selection seed comes from the ids the response's link already
/// carries, so nothing new travels in the link and the same seed is
/// recomputed at intake:
///   - an identified group seeds from the instance id, which its submission
///     keeps: each respondent answers their own reproducible selection;
///   - an anonymous group seeds from the group id, because its submission
///     gets a fresh unlinkable id (LURL-002) and a per-respondent seed
///     carried from invitation to submission would link the two. Everyone in
///     an anonymous group answers the same selection.
/// A test link seeds as the binding it stands in for. A link with no group
/// (the open pilot page) seeds from zero bytes; it can't be submitted.
///
/// The selection is applied as flow rules that hide the questions left out,
/// so the encoding layout, slots and template hash are untouched; only what
/// applies changes, and scoring, completion and the page follow from that.
///
/// Pure.
module Echelon.Signal.Engine.BankSelection

open System
open System.Security.Cryptography
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState

/// Domain separation for the seed hash, with the algorithm version.
[<Literal>]
let SeedLabel = "signal-bank-seed/1"

/// The first 8 bytes, big-endian, of SHA-256(label ‖ id).
let seedOf (id: byte[]) : uint64 =
    let digest = SHA256.HashData(Array.append (Text.Encoding.ASCII.GetBytes SeedLabel) id)
    digest |> Array.take 8 |> Array.fold (fun (acc: uint64) b -> (acc <<< 8) ||| uint64 b) 0UL

/// The seed a response's binding gives its bank selection.
let rec seedFor (binding: Binding) : uint64 =
    match binding with
    | Test inner -> seedFor inner
    | IdentifiedInvitation(instance, _)
    | Identified(instance, _) -> seedOf (OpaqueId.toBytes instance)
    | AnonymousInvitation(_, group)
    | Anonymous(_, group) -> seedOf (OpaqueId.toBytes group)
    | Unbound -> seedOf [||]

/// The flow-rule id prefix for a left-out question; no authored rule may use it.
[<Literal>]
let RulePrefix = Banking.ExcludedRulePrefix

/// The content as this instance answers it: the questions its banks leave
/// out are hidden. A template without banks is returned unchanged.
let instantiate (seed: uint64) (content: Content) : Content =
    match content.Presentation.Banks with
    | [] -> content
    | banks ->
        let hidden =
            Banking.excluded seed banks
            |> Set.toList
            |> List.map (fun id ->
                { Id = RulePrefix + id
                  When = Always
                  Then = HideQuestion id })

        { content with Rules = { content.Rules with Flow = content.Rules.Flow @ hidden } }

/// The content for a response with this binding.
let forBinding (binding: Binding) (content: Content) = instantiate (seedFor binding) content
