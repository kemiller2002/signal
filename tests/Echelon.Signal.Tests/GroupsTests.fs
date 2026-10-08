/// Group semantics (WI-0043): ACR-002, VER-006 groups, ID-001, ID-004.
module Echelon.Signal.Tests.GroupsTests

open Xunit
open Echelon.Signal.Engine
open Echelon.Signal.Engine.Import
open Echelon.Signal.Engine.Groups

let private id (n: int) = (UrlState.OpaqueId.ofBytes (Array.init 16 (fun i -> byte (n * 16 + i)))).Value

let private member' n role required after : Member =
    { Instance = id n
      SurveyId = "leadership-360"
      Role = role
      Required = required
      Order = None
      After = after }

/// A 360: self first, then a manager and three peers.
let private design: GroupDesign =
    { Group = id 0
      Mode = IdentifiedGroup
      Subject = Some(id 9)
      ExpectedCount = 5
      Members =
        [ member' 1 (Some Self) true []
          member' 2 (Some Manager) true [ id 1 ]
          member' 3 (Some Peer) false [ id 1 ]
          member' 4 (Some Peer) false [ id 1 ]
          member' 5 (Some Peer) false [ id 1 ] ]
      Completion = [ AllRequiredMembers; RoleMinimums [ { Role = Peer; Minimum = 2 } ] ] }

let private from n role : Contribution =
    { Instance = Some(id n); SurveyId = "leadership-360"; Role = role }

[<Fact>]
let ``a 360 design validates`` () = Assert.Empty(validateDesign design)

[<Fact>]
let ``dependencies gate when a member may begin`` () =
    Assert.Equal(Ok(), canBegin design [] (id 1))

    match canBegin design [] (id 2) with
    | Error waiting -> Assert.Equal<string list>([ string (id 1) ], waiting |> List.map string)
    | Ok() -> failwith "the manager must wait for self"

    Assert.Equal(Ok(), canBegin design [ id 1 ] (id 2))

[<Fact>]
let ``group completion is explicit: required members and role minimums`` () =
    let partial = progress design [ from 1 (Some Self); from 3 (Some Peer) ]
    Assert.False(partial.Complete)
    Assert.Equal(3, partial.Missing)
    Assert.Equal(2, partial.Unmet.Length) // the manager, and one peer short

    let done' = progress design [ from 1 (Some Self); from 2 (Some Manager); from 3 (Some Peer); from 5 (Some Peer) ]
    Assert.True(done'.Complete)
    Assert.Equal(1, done'.Missing)

[<Fact>]
let ``anonymous groups count contributions and refuse instance-level rules`` () =
    let anonymous =
        { design with
            Mode = AnonymousGroup
            Members = []
            Completion = [ MinimumContributions 3; RoleMinimums [ { Role = Peer; Minimum = 2 } ] ] }

    Assert.Empty(validateDesign anonymous)
    let anon role : Contribution = { Instance = None; SurveyId = "leadership-360"; Role = role }
    Assert.False((progress anonymous [ anon (Some Peer); anon None ]).Complete)
    Assert.True((progress anonymous [ anon (Some Peer); anon (Some Peer); anon None ]).Complete)

    Assert.Contains(InstanceRuleInAnonymousGroup, validateDesign { anonymous with Completion = [ AllRequiredMembers ] })

[<Fact>]
let ``design problems are reported, not guessed around`` () =
    let problems d = validateDesign d |> List.map (sprintf "%A")
    let has (case: string) d = Assert.Contains(problems d, fun p -> p.StartsWith case)

    has "ExpectedCountNotPositive" { design with ExpectedCount = 0 }
    has "NoCompletionRule" { design with Completion = [] }
    has "DuplicateMember" { design with Members = design.Members @ [ design.Members.Head ]; ExpectedCount = 9 }
    has "MoreMembersThanExpected" { design with ExpectedCount = 4 }
    has "UnknownDependency" { design with Members = [ member' 1 None true [ id 7 ] ] }
    has "DependencyCycle" { design with Members = [ member' 1 None true [ id 2 ]; member' 2 None true [ id 1 ] ] }
    has "RoleRequirementWithoutSubject" { design with Subject = None }
    has "RoleMinimumsExceedExpected" { design with Completion = [ RoleMinimums [ { Role = Peer; Minimum = 6 } ] ] }

[<Fact>]
let ``ordering puts ordered members first by position, then declaration order`` () =
    let d =
        { design with
            Members =
                [ { member' 1 None true [] with Order = Some 2 }
                  member' 2 None true []
                  { member' 3 None true [] with Order = Some 1 } ] }

    Assert.Equal<string list>([ id 3; id 1; id 2 ] |> List.map string, ordered d |> List.map (fun m -> string m.Instance))
