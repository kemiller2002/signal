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

// ---------------------------------------------------------------------------
// The page in the link (SIG-LINK-014, DF-SIGNAL-2026-0007).
// ---------------------------------------------------------------------------

[<Fact>]
let ``the link names a page as p, from 2, written once`` () =
    Assert.Equal(Some 2, GenericPaging.pageIn "#r=abc&p=3")
    Assert.Equal(Some 1, GenericPaging.pageIn "#p=2&r=abc")

    for hash in [ "#r=abc"; "#r=abc&p=1"; "#r=abc&p=03"; "#r=abc&p=x"; "#r=abc&p=2&p=3"; "#r=abc&p=-2"; "#r=abc&p=99999" ] do
        Assert.Equal(None, GenericPaging.pageIn hash)

let private openAt (answers: Answers) (page: int) =
    let link = GenericEnvelope.encode content (GenericImport.formOf "PAGED" "1" content).Reference { Binding = IdentifiedInvitation(oid 1, oid 70); Answers = answers }
    start ($"#r={link}&p={page}") |> received day (Found bytes)

[<Fact>]
let ``a link's page is reached under the rules, never granted`` () =
    // Nothing answered under a locking policy: the link cannot skip page 1.
    Assert.Equal(0, (responding (openAt Map.empty 3)).Position.Page)
    // Page 1 answered: page 2 opens, and page 3 is still out of reach.
    let answers = Map.ofList [ for id in ids[0..1] -> id, first id ]
    Assert.Equal(1, (responding (openAt answers 3)).Position.Page)
    Assert.Equal(1, (responding (openAt answers 2)).Position.Page)

[<Fact>]
let ``the response's link carries its page, and the first page is left out`` () =
    let s = openWith Map.empty |> answer ids[0] |> answer ids[1]
    Assert.DoesNotContain("&p=", fragment (responding s))
    let s = update (Turned GenericPaging.Next) s
    Assert.EndsWith("&p=2", fragment (responding s))
    Assert.True(isOwnLink (responding s) (fragment (responding (openWith Map.empty))))
    // Another binding is another response.
    let other = GenericEnvelope.encode content (GenericImport.formOf "PAGED" "1" content).Reference { Binding = IdentifiedInvitation(oid 2, oid 70); Answers = Map.empty }
    Assert.False(isOwnLink (responding s) ("#r=" + other))

[<Fact>]
let ``browser Back moves a page under the rules and keeps the answers held now`` () =
    let s = openWith Map.empty |> answer ids[0] |> answer ids[1] |> update (Turned GenericPaging.Next)
    let s = s |> answer ids[2] |> update (Reached 0)
    Assert.Equal(0, (responding s).Position.Page)
    Assert.True((responding s).Answers.ContainsKey ids[2])
    // Past the section lock, Back is refused, however it arrives.
    let s = s |> update (Reached 2)
    Assert.Equal(2, (responding s).Position.Page)
    Assert.Equal(2, (responding (update (Reached 0) s)).Position.Page)

[<Fact>]
let ``a submission carries no page, only its envelope`` () =
    let s =
        openWith Map.empty |> answer ids[0] |> answer ids[1] |> update (Turned GenericPaging.Next)
        |> answer ids[2] |> update (Turned GenericPaging.Next) |> answer ids[3] |> answer ids[4]

    Assert.EndsWith("&p=3", fragment (responding s))
    let submitted = responding (update (SubmitRequested(Array.zeroCreate 16)) s)
    Assert.Equal(Submitted, submitted.Phase)
    Assert.DoesNotContain("&p=", fragment submitted)
