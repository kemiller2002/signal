/// Every answer primitive the engine evaluates is covered by the capability
/// list (WI-0090): a primitive with a capability has it supported, and each
/// capability the engine refuses names why no template can need it. The
/// primitives and capabilities come from the engine's own unions, so a new
/// one cannot drift past this test.
module Echelon.Signal.Tests.CapabilityCoverageTests

open System
open Microsoft.FSharp.Reflection
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Primitives
open Echelon.Signal.Engine.Template

/// One value of each case of a union, with default fields: enough for a
/// function that matches on the case alone.
let private everyCase<'T> () : (string * 'T) list =
    [ for case in FSharpType.GetUnionCases typeof<'T> ->
          let fields =
              case.GetFields()
              |> Array.map (fun f -> if f.PropertyType.IsValueType then Activator.CreateInstance f.PropertyType else null)

          case.Name, FSharpValue.MakeUnion(case, fields) :?> 'T ]

[<Fact>]
let ``every primitive with a capability has it supported`` () =
    let primitives = everyCase<AnswerDefinition> ()
    Assert.True(primitives.Length >= 11, $"%A{primitives |> List.map fst}")

    let mapped = primitives |> List.choose (fun (name, p) -> capabilityOf p |> Option.map (fun c -> name, c))
    Assert.NotEmpty mapped

    for name, capability in mapped do
        Assert.True(supportedCapabilities.Contains capability, $"{name} needs {capability}, which is not supported")
        Assert.False(unsupportedCapabilities.ContainsKey capability, $"{name} maps to the refused {capability}")

[<Fact>]
let ``every capability is either supported or refused with a reason, never both`` () =
    for name, capability in everyCase<Capability> () do
        let supported = supportedCapabilities.Contains capability
        let refused = unsupportedCapabilities.ContainsKey capability
        Assert.True(supported <> refused, $"{name}: supported={supported}, refused={refused}")

    for KeyValue(capability, reason) in unsupportedCapabilities do
        Assert.False(String.IsNullOrWhiteSpace reason, $"{capability} has no reason")
