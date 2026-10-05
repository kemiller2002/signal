/// index.html and the engine are not type-checked against each other: the
/// page names events and view keys as strings. These tests hold the page to
/// its engine in both directions, so a renamed key fails here instead of
/// silently rendering nothing (Limen unmounts a data-if whose key is missing).
module Echelon.Signal.Tests.BindingAgreementTests

open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.Session
open Echelon.Signal.Tests.Support

let private attributeValues (attribute: string) (html: string) =
    Regex.Matches(html, $"\\s{attribute}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

/// Every key the page binds: data-text, data-if, data-each, data-key and every data-bind-*.
let private boundKeys (html: string) =
    let bindings =
        Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    [ "data-text"; "data-if"; "data-each"; "data-key" ]
    |> List.map (fun attribute -> attributeValues attribute html)
    |> Set.unionMany
    |> Set.union bindings

/// The names a view offers: its own keys and the fields of its list items.
let private viewNames (view: View) =
    view
    |> List.collect (fun (name, value) ->
        match value with
        | Value _ -> [ name ]
        | Items items -> name :: (items |> List.collect (List.map fst)))
    |> Set.ofList

let private faultNames = Echelon.Signal.Application.Boundary.faultView None |> List.map fst |> Set.ofList

[<Fact>]
let ``the page binds only what its engine projects and sends only what it handles`` () =
    let html = readRepoFile "web/index.html"
    let offered = Set.union (viewNames (view (start Echelon.Signal.Engine.Pilot.assessment))) faultNames
    Assert.Empty(Set.difference (boundKeys html) offered)
    // Every event the page can send is one the engine handles, and every
    // event the engine handles is one the page can send.
    Assert.Equal<Set<string>>(Echelon.Signal.Application.Wire.events |> Map.keys |> Set.ofSeq, attributeValues "data-event" html)

[<Fact>]
let ``every answer choice on the page is a code the engine reads`` () =
    let html = readRepoFile "web/index.html"

    let choices =
        Regex.Matches(html, "<input type=\"radio\" value=\"([^\"]+)\" data-event=\"answered\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Seq.toList

    Assert.Equal(8, choices.Length)
    Assert.All(choices, fun code -> Assert.True((parseAnswer code).IsSome, code))
    Assert.Equal(8, choices |> List.distinct |> List.length)
