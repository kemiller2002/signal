/// The respondent session: its transitions and what it projects.
module Echelon.Signal.Tests.SessionTests

open Xunit
open Echelon.Signal.Engine.View
open Echelon.Signal.Engine.Assessment
open Echelon.Signal.Engine.Session

let private initial = start Echelon.Signal.Engine.Pilot.assessment

let private run messages =
    messages |> List.fold (fun session msg -> update msg session) initial

let private answerAll (answer: Answer) =
    initial.Assessment.Items |> List.map (fun item -> Answered(item.Id, Some answer))

let private value name (session: Session) =
    view session |> List.find (fst >> (=) name) |> snd

let private text name session =
    match value name session with
    | Value(Text t) -> t
    | other -> failwith $"{name} is not text: {other}"

let private flag name session =
    match value name session with
    | Value(Flag f) -> f
    | other -> failwith $"{name} is not a flag: {other}"

let private items name session =
    match value name session with
    | Items rows -> rows |> List.map Map.ofList
    | other -> failwith $"{name} is not a list: {other}"

[<Fact>]
let ``a new session is responding, with nothing answered`` () =
    Assert.Equal(Responding, initial.Phase)
    Assert.True(flag "responding" initial)
    Assert.False(flag "reviewing" initial)
    Assert.Equal("0 of 15 answered", text "progress" initial)
    let rows = items "items" initial
    Assert.Equal(15, rows.Length)
    Assert.All(rows, fun row -> Assert.Equal(Text "unanswered", row.["answerState"]))
    Assert.All(rows, fun row -> Assert.Equal(Text "Not answered", row.["answerLabel"]))

[<Fact>]
let ``asking for results with questions unanswered is refused, and says how many`` () =
    let session = run [ Answered("CORE-001", Some(Rated Often)); ResultsRequested ]
    Assert.Equal(Responding, session.Phase)
    Assert.True(flag "hasRefusal" session)
    Assert.StartsWith("14 questions still need an answer.", text "refusal" session)

    let oneLeft = run (answerAll (Withheld DontKnow) |> List.tail) |> update ResultsRequested
    Assert.StartsWith("1 question still needs an answer.", text "refusal" oneLeft)
    // Answering clears the refusal.
    Assert.False(flag "hasRefusal" (update (Answered("CORE-001", Some(Rated Never))) oneLeft))

[<Fact>]
let ``with every question answered the results are shown, scored or not`` () =
    let session =
        run (answerAll (Rated Often) @ [ Answered("CORE-011", Some(Withheld DontKnow))
                                         Answered("CORE-012", Some(Withheld DontKnow))
                                         Answered("CORE-013", Some(Withheld NotApplicable))
                                         ResultsRequested ])

    Assert.Equal(Reviewing, session.Phase)
    Assert.True(flag "reviewing" session)
    Assert.False(flag "hasRefusal" session)
    let results = items "results" session
    Assert.Equal<Scalar list>([ Text "75.0"; Text "75.0"; Text "Not scored" ], results |> List.map (fun r -> r.["score"]))
    Assert.Equal<Scalar list>([ Text "scored"; Text "scored"; Text "unscored" ], results |> List.map (fun r -> r.["scoreState"]))
    Assert.Equal(Text "2 of 5 numeric", results.[2].["coverage"])
    Assert.Equal(Text "Needs at least 3 numeric answers; missing data is not counted as zero.", results.[2].["note"])
    Assert.Equal("2 of 3 dimensions scored", text "scoredCount" session)

[<Fact>]
let ``editing returns to the questions with the answers kept, and starting over clears them`` () =
    let reviewed = run (answerAll (Rated Sometimes) @ [ ResultsRequested ])
    let editing = update EditRequested reviewed
    Assert.Equal(Responding, editing.Phase)
    Assert.Equal(15, editing.Answers.Count)

    let restarted = update Restarted reviewed
    Assert.Equal(initial, restarted)

[<Fact>]
let ``clearing an answer while reviewing returns to the questions`` () =
    let reviewed = run (answerAll (Rated Sometimes) @ [ ResultsRequested ])
    let cleared = update (Answered("CORE-004", None)) reviewed
    Assert.Equal(Responding, cleared.Phase)
    Assert.Equal("14 of 15 answered", text "progress" cleared)

[<Fact>]
let ``each item projects its radio group and exactly the checked choice`` () =
    let session = run [ Answered("CORE-002", Some(Withheld NotObserved)) ]
    let row = items "items" session |> List.find (fun r -> r.["id"] = Text "CORE-002")
    Assert.Equal(Text "answer-CORE-002", row.["group"])
    Assert.Equal(Text "not-observed", row.["answer"])
    Assert.Equal(Text "Not observed", row.["answerLabel"])

    let checkedChoices =
        [ "is0"; "is1"; "is2"; "is3"; "is4"; "isDontKnow"; "isNotObserved"; "isNotApplicable" ]
        |> List.filter (fun name -> row.[name] = Flag true)

    Assert.Equal<string list>([ "isNotObserved" ], checkedChoices)
    Assert.Equal(Number 1.0, match value "answeredCount" session with Value v -> v | _ -> Text "")

[<Fact>]
let ``an answer for an item the assessment does not have changes nothing`` () =
    Assert.Equal(initial, update (Answered("CORE-999", Some(Rated Often))) initial)
