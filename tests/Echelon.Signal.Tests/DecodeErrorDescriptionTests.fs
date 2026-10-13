/// Every coded error a link or the survey page can report has a safe
/// description (WI-0081): it says what is wrong in words, and never echoes
/// the link's payload. Cases are enumerated by reflection, so a new case is
/// checked without editing this test.
module Echelon.Signal.Tests.DecodeErrorDescriptionTests

open System
open Microsoft.FSharp.Reflection
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.UrlState

/// One value of each case of a union, with plain arguments.
let private every<'u> () : 'u list =
    let argument (t: Type) : obj =
        if t = typeof<int> then box 0
        elif t = typeof<string> then box "reason"
        elif t = typeof<DateOnly> then box (DateOnly(2026, 1, 1))
        elif t = typeof<DecodeError> then box InvalidTerms
        else failwith $"no sample argument for {t.Name}"

    FSharpType.GetUnionCases typeof<'u>
    |> Array.map (fun case -> FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun f -> argument f.PropertyType)) :?> 'u)
    |> Array.toList

/// A real link payload: no description may contain it, or any of its start.
let private payload =
    GenericEnvelope.invitation "DEMO" "1" SurveyPageTests.demo Import.IdentifiedGroup (OpaqueId.ofBytes (Array.init 16 byte)).Value (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (40 + i)))).Value

/// Safe: words, and no run of 10 characters from the payload. (Naming the
/// `#r=` marker as guidance is fine; echoing what follows it is not.)
let private safe (text: string) =
    not (String.IsNullOrWhiteSpace text)
    && Seq.windowed 10 payload |> Seq.forall (fun w -> not (text.Contains(String(w))))

[<Fact>]
let ``damaged invitation details are described without naming only some of them`` () =
    Assert.Equal("This link's invitation details are damaged.", describe InvalidTerms)
    Assert.Equal(describe InvalidTerms, GenericSession.describe (GenericSession.UnreadableLink InvalidTerms))

[<Fact>]
let ``every coded decode error and page refusal has a safe description`` () =
    let decodeErrors = every<DecodeError> ()
    Assert.Equal(FSharpType.GetUnionCases(typeof<DecodeError>).Length, decodeErrors.Length)

    for e in decodeErrors do
        Assert.True(safe (describe e), $"%A{e}: '{describe e}'")

    for r in every<GenericSession.Refusal> () do
        let text = GenericSession.describe r
        Assert.True(safe text, $"%A{r}: '{text}'")
