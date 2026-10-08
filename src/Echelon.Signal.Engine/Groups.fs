/// Survey group semantics (ACR-002 §§5-6, VER-006 "Survey Group", ID-001,
/// ID-004): respondent roles relative to a subject, required and optional
/// surveys, ordering, dependencies between instances, and explicit group
/// completion.
///
/// Identity stays opaque (ID-001): instances, groups and subjects are
/// 16-byte ids that are never a person, and the administrator keeps the
/// mapping outside Signal. In an anonymous group a contribution carries no
/// instance (ID-004 §§7-11), so requirements that need to know *which*
/// instance finished are refused at design time instead of silently
/// unsatisfiable. Pure: the progress observed so far is an argument.
module Echelon.Signal.Engine.Groups

open Echelon.Signal.Engine.UrlState
open Echelon.Signal.Engine.Import

/// The respondent's role relative to the subject (ACR-002 §5).
type Role =
    | Self
    | Manager
    | Peer
    | DirectReport
    | Customer
    | Reviewer
    | Auditor
    | Observer
    | CustomRole of string

/// One invited survey instance in a group.
[<NoComparison>]
type Member =
    { Instance: OpaqueId
      SurveyId: string
      Role: Role option
      Required: bool
      /// Position in the group's suggested order; None is unordered.
      Order: int option
      /// Instances that must be complete before this one begins.
      After: OpaqueId list }

type RoleRequirement = { Role: Role; Minimum: int }

/// Every rule listed must hold for the group to be complete (VER-006).
type CompletionRule =
    /// Every required member has contributed (identified groups).
    | AllRequiredMembers
    /// At least this many contributions in total.
    | MinimumContributions of int
    /// At least one contribution of each survey.
    | SurveysCompleted of surveyIds: string list
    /// At least this many contributions per role (360-style).
    | RoleMinimums of RoleRequirement list

[<NoComparison>]
type GroupDesign =
    { Group: OpaqueId
      Mode: IdentityMode
      /// The person or entity assessed, as an opaque id; None for
      /// self-assessments and anonymous pulse surveys.
      Subject: OpaqueId option
      ExpectedCount: int
      Members: Member list
      Completion: CompletionRule list }

/// One accepted submission as the group sees it. Anonymous contributions
/// have no instance; they may carry the invitation's role.
[<NoComparison>]
type Contribution =
    { Instance: OpaqueId option
      SurveyId: string
      Role: Role option }

[<NoComparison>]
type DesignProblem =
    | ExpectedCountNotPositive
    | DuplicateMember of OpaqueId
    | MoreMembersThanExpected of members: int * expected: int
    | UnknownDependency of instance: OpaqueId * dependsOn: OpaqueId
    | DependencyCycle of OpaqueId
    /// Anonymous submissions remove the instance, so instance-level
    /// requirements could never be observed.
    | InstanceRuleInAnonymousGroup
    | RoleMinimumNotPositive of Role
    | RoleMinimumsExceedExpected of total: int * expected: int
    | RoleRequirementWithoutSubject
    | NoCompletionRule

let private memberKey (m: Member) = string m.Instance

let validateDesign (design: GroupDesign) : DesignProblem list =
    let keys = design.Members |> List.map memberKey
    let byKey = design.Members |> List.map (fun m -> memberKey m, m) |> Map.ofList

    let rec reaches (target: string) (visited: Set<string>) (key: string) =
        match byKey.TryFind key with
        | None -> false
        | Some m ->
            m.After
            |> List.map string
            |> List.exists (fun next -> next = target || (not (visited.Contains next) && reaches target (visited.Add next) next))

    let roleRules =
        design.Completion
        |> List.collect (function
            | RoleMinimums rs -> rs
            | _ -> [])

    [ if design.ExpectedCount < 1 then ExpectedCountNotPositive
      if design.Completion.IsEmpty then NoCompletionRule
      for key, n in keys |> List.countBy id do
          if n > 1 then DuplicateMember byKey[key].Instance
      if design.Members.Length > design.ExpectedCount && design.ExpectedCount >= 1 then
          MoreMembersThanExpected(design.Members.Length, design.ExpectedCount)
      for m in design.Members do
          for dep in m.After do
              if not (byKey.ContainsKey(string dep)) then UnknownDependency(m.Instance, dep)
          if reaches (memberKey m) (Set.singleton (memberKey m)) (memberKey m) then DependencyCycle m.Instance
      if design.Mode = AnonymousGroup
         && (design.Completion |> List.contains AllRequiredMembers || design.Members |> List.exists (fun m -> not m.After.IsEmpty)) then
          InstanceRuleInAnonymousGroup
      for r in roleRules do
          if r.Minimum < 1 then RoleMinimumNotPositive r.Role
      let total = roleRules |> List.sumBy _.Minimum
      if total > design.ExpectedCount && design.ExpectedCount >= 1 then RoleMinimumsExceedExpected(total, design.ExpectedCount)
      if not roleRules.IsEmpty && design.Subject.IsNone then RoleRequirementWithoutSubject ]

/// Members in the group's suggested order: ordered members first by
/// position, then unordered members in declaration order.
let ordered (design: GroupDesign) =
    design.Members
    |> List.indexed
    |> List.sortBy (fun (i, m) ->
        match m.Order with
        | Some o -> 0, o, i
        | None -> 1, 0, i)
    |> List.map snd

/// Whether an identified member may begin, given the instances already
/// complete: Ok, or the dependencies still outstanding (ACR-002 §6).
let canBegin (design: GroupDesign) (completed: OpaqueId list) (instance: OpaqueId) : Result<unit, OpaqueId list> =
    let done' = completed |> List.map string |> Set.ofList

    match design.Members |> List.tryFind (fun m -> m.Instance = instance) with
    | None -> Ok()
    | Some m ->
        match m.After |> List.filter (fun d -> not (done'.Contains(string d))) with
        | [] -> Ok()
        | waiting -> Error waiting

/// Why a group is not complete yet.
[<NoComparison>]
type Unmet =
    | RequiredMembersMissing of OpaqueId list
    | TooFewContributions of have: int * need: int
    | SurveyMissing of surveyId: string
    | RoleShort of role: Role * have: int * need: int

[<NoComparison>]
type GroupProgress =
    { Contributions: int
      Expected: int
      /// Expected minus contributions, never negative. In an anonymous group
      /// this is a count only; who is missing is never inferred.
      Missing: int
      Unmet: Unmet list
      Complete: bool }

let progress (design: GroupDesign) (contributions: Contribution list) : GroupProgress =
    let contributed =
        contributions |> List.choose _.Instance |> List.map string |> Set.ofList

    let roleCount role =
        contributions |> List.filter (fun c -> c.Role = Some role) |> List.length

    let unmet =
        design.Completion
        |> List.collect (function
            | AllRequiredMembers ->
                match
                    design.Members
                    |> List.filter (fun m -> m.Required && not (contributed.Contains(memberKey m)))
                    |> List.map _.Instance
                with
                | [] -> []
                | missing -> [ RequiredMembersMissing missing ]
            | MinimumContributions n when contributions.Length < n -> [ TooFewContributions(contributions.Length, n) ]
            | MinimumContributions _ -> []
            | SurveysCompleted ids ->
                ids
                |> List.filter (fun id -> not (contributions |> List.exists (fun c -> c.SurveyId = id)))
                |> List.map SurveyMissing
            | RoleMinimums rs ->
                rs
                |> List.choose (fun r ->
                    let have = roleCount r.Role
                    if have < r.Minimum then Some(RoleShort(r.Role, have, r.Minimum)) else None))

    { Contributions = contributions.Length
      Expected = design.ExpectedCount
      Missing = max 0 (design.ExpectedCount - contributions.Length)
      Unmet = unmet
      Complete = unmet.IsEmpty }
