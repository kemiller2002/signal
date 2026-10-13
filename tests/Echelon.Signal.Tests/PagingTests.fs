/// Pages on the survey page (WI-0081, VER-004, ARX-013): one page of
/// applicable questions at a time, Back and Next under the template's
/// navigation policy, and locked answers that cannot change.
module Echelon.Signal.Tests.PagingTests

open System
open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.GenericSession

let private content = SurveyPageTests.paged
let private bytes = publishedFile "PAGED" "1" content |> snd
let private oid (seed: int) = (OpaqueId.ofBytes (Array.init 16 (fun i -> byte (seed + i)))).Value
let private day = DateOnly(2026, 1, 1)
let private ids = questions content |> List.map (fun (_, q) -> q.Id)

let private openWith (answers: Answers) =
    let link = GenericEnvelope.encode content (GenericImport.formOf "PAGED" "1" content).Reference { Binding = IdentifiedInvitation(oid 1, oid 70); Answers = answers }
    start ("#r=" + link) |> received day (Found bytes)

let private responding =
    function
    | Responding r -> r
    | other -> failwith $"%A{other}"

let private onPage session =
    let r = responding session
    let page, count, shown = GenericPaging.current r.Published.Form.Content r.Answers r.Position
    page, count, shown

/// An answer every question in the demo takes: its first value.
let private first (id: string) =
    choices (tryQuestion content id).Value |> List.head |> fst

let private answer id session = update (Chose(id, first id)) session

[<Fact>]
let ``the pages follow the template: two a page, and each section on its own page`` () =
    Assert.Equal<string list list>([ ids[0..1]; [ ids[2] ]; ids[3..4] ], GenericPaging.pagesOf content Map.empty)
    Assert.Equal((0, 3, ids[0..1]), onPage (openWith Map.empty))

[<Fact>]
let ``Next and Back move a page, and Back stops at the first page`` () =
    // Under a locking policy every page's required answers come first.
    Assert.Equal(0, (responding (openWith Map.empty |> update (Turned GenericPaging.Next))).Position.Page)
    let s = openWith Map.empty |> answer ids[0] |> answer ids[1] |> update (Turned GenericPaging.Next)
    Assert.Equal((1, 3, [ ids[2] ]), onPage s)
    let s = update (Turned GenericPaging.Back) s
    Assert.Equal((0, 3, ids[0..1]), onPage s)
    let refused = responding (update (Turned GenericPaging.Back) s)
    Assert.Equal(0, refused.Position.Page)
    Assert.True(refused.Refusal.IsSome)

[<Fact>]
let ``leaving a locked section needs its required answers, and then its answers cannot change`` () =
    let s = openWith Map.empty |> answer ids[0] |> answer ids[1] |> update (Turned GenericPaging.Next)
    // Q3 is unanswered: the section cannot be left.
    let blocked = responding (update (Turned GenericPaging.Next) s)
    Assert.Equal(1, blocked.Position.Page)
    Assert.Contains("1 required question on this page needs an answer", blocked.Refusal.Value)

    let s = s |> answer ids[2] |> update (Turned GenericPaging.Next)
    Assert.Equal(2, (responding s).Position.Page)
    // The first section is behind a lock: no Back, and its answers hold.
    let r = responding s
    Assert.False(GenericPaging.canGoBack r.Published.Form.Content r.Answers r.Position)
    Assert.Equal(2, (responding (update (Turned GenericPaging.Back) s)).Position.Page)
    let other = choices (tryQuestion content ids[0]).Value |> List.item 1 |> fst
    Assert.True((r.Answers = (responding (update (Chose(ids[0], other)) s)).Answers))

[<Fact>]
let ``a reopened link resumes on its last answered page, and its locks still hold`` () =
    let answers = Map.ofList [ for id in ids[0..3] -> id, first id ]
    let s = openWith answers
    Assert.Equal((2, 3, ids[3..4]), onPage s)
    Assert.True((answers = (responding (update (Chose(ids[0], choices (tryQuestion content ids[0]).Value |> List.item 1 |> fst)) s)).Answers))

[<Fact>]
let ``a survey without pagination is one page, with nothing locked`` () =
    let demo = SurveyPageTests.demo
    let link = GenericEnvelope.invitation SurveyPageTests.DemoSurvey SurveyPageTests.DemoVersion demo Import.IdentifiedGroup (oid 1) (oid 70)
    let file = publishedFile SurveyPageTests.DemoSurvey SurveyPageTests.DemoVersion demo |> snd
    let r = responding (start ("#r=" + link) |> received day (Found file))
    Assert.Equal(1, GenericPaging.pagesOf r.Published.Form.Content r.Answers |> List.length)
    Assert.True((GenericPaging.locked r.Published.Form.Content r.Answers r.Position).IsEmpty)
    Assert.True(GenericPaging.move r.Published.Form.Content r.Answers r.Position GenericPaging.Next |> Result.isError)
