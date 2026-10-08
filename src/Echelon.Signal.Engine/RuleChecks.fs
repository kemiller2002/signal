/// Publication-time checks of a template's rules (CAN-005 §28 flow, derived
/// facts and recommendations; AUT-004 §§34, 36-38; ACR-007 §21):
/// unique ids, references that resolve, conditions that type-check, an
/// acyclic fact graph, flow that depends only on earlier content (so one pass
/// in template order decides applicability), no permanently hidden required
/// content, and declared capabilities for every rule kind used.
module Echelon.Signal.Engine.RuleChecks

open Echelon.Signal.Engine.Responses
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

// ---------------------------------------------------------------------------
// What a rule reads.
// ---------------------------------------------------------------------------

type private Reference =
    | QuestionRef of string
    | SectionRef of string
    | FactRef of string

let rec private numberRefs =
    function
    | Constant _ -> []
    | AnswerNumber id -> [ QuestionRef id ]
    | SectionScore id -> [ SectionRef id ]
    | NumberFact id -> [ FactRef id ]

let rec private conditionRefs =
    function
    | Always -> []
    | Answered id
    | AnswerIs(id, _)
    | AnswerIn(id, _)
    | IsSpecial(id, _) -> [ QuestionRef id ]
    | Compare(a, _, b) -> numberRefs a @ numberRefs b
    | FactTrue id
    | CategoryIs(id, _) -> [ FactRef id ]
    | All cs
    | Any cs -> cs |> List.collect conditionRefs
    | Not c -> conditionRefs c

let private factRefs (f: Fact) =
    match f.Expr with
    | BooleanFact c -> conditionRefs c
    | NumberFactOf n -> numberRefs n
    | CategoryFact(cases, _) -> cases |> List.collect (fst >> conditionRefs)

let private factOf (content: Content) (id: string) =
    content.Rules.Facts |> List.tryFind (fun f -> f.Id = id)

/// Every question a condition reads, through facts and section scores.
let questionDependencies (content: Content) (c: Condition) : Set<string> =
    let sectionQuestions id =
        content.Sections |> List.filter (fun s -> s.Id = id) |> List.collect (fun s -> s.Questions |> List.map _.Id)

    let rec go (visited: Set<string>) (refs: Reference list) =
        refs
        |> List.fold
            (fun (acc: Set<string>, visited: Set<string>) r ->
                match r with
                | QuestionRef id -> acc.Add id, visited
                | SectionRef id -> Set.union acc (Set.ofList (sectionQuestions id)), visited
                | FactRef id when visited.Contains id -> acc, visited
                | FactRef id ->
                    match factOf content id with
                    | None -> acc, visited.Add id
                    | Some f ->
                        let inner, visited = go (visited.Add id) (factRefs f)
                        Set.union acc inner, visited)
            (Set.empty, visited)

    go Set.empty (conditionRefs c) |> fst

// ---------------------------------------------------------------------------
// Capabilities the rules need.
// ---------------------------------------------------------------------------

let requiredCapabilities (content: Content) : Set<Capability> =
    let r = content.Rules

    let sectionFlow =
        r.Flow
        |> List.exists (fun f ->
            match f.Then with
            | ShowSection _
            | HideSection _
            | SkipToSection _ -> true
            | _ -> false)

    Set.ofList
        [ if not r.Flow.IsEmpty then UsesBranching
          if sectionFlow then UsesConditionalSections
          if not r.Facts.IsEmpty then UsesDerivedFacts
          if not r.Recommendations.IsEmpty then UsesRecommendations
          if not r.Validation.IsEmpty || r.Completion <> defaultCompletion then UsesAdvancedValidation
          match content.Results.Overall with
          | Some(ResultModel.Custom _) -> UsesCustomScoring
          | _ -> () ]

// ---------------------------------------------------------------------------
// The checks.
// ---------------------------------------------------------------------------

let private block = finding RulesCategory Blocker

let private identities (content: Content) =
    let r = content.Rules

    [ for kind, ids in
          [ "fact", r.Facts |> List.map _.Id
            "flow rule", r.Flow |> List.map _.Id
            "validation rule", r.Validation |> List.map _.Id
            "recommendation", r.Recommendations |> List.map _.Id ] do
          for id in duplicates ids do
              block "RULE-DUPLICATE-ID" id $"The {kind} id '{id}' is used more than once."
          for id in ids do
              if not (isIdentifier id) then
                  block "RULE-ID" id $"The {kind} id '{id}' is not a stable identifier." ]

let private referenceFindings (content: Content) subject =
    let section id = content.Sections |> List.tryFind (fun s -> s.Id = id)

    function
    | QuestionRef id when (tryQuestion content id).IsNone -> [ block "RULE-REFERENCE" subject $"'{subject}' refers to unknown question '{id}'." ]
    | SectionRef id when (section id).IsNone -> [ block "RULE-REFERENCE" subject $"'{subject}' refers to unknown section '{id}'." ]
    | SectionRef id when (section id).Value.Scoring.IsNone -> [ block "RULE-TYPE" subject $"'{subject}' reads the score of unscored section '{id}'." ]
    | FactRef id when (factOf content id).IsNone -> [ block "RULE-REFERENCE" subject $"'{subject}' refers to unknown fact '{id}'." ]
    | _ -> []

let private references (content: Content) =
    let r = content.Rules
    let section id = content.Sections |> List.tryFind (fun s -> s.Id = id)
    let checkRef = referenceFindings content

    let conditions subject c = conditionRefs c |> List.collect (checkRef subject)

    let actionRefs =
        function
        | ShowQuestion q
        | HideQuestion q -> [ QuestionRef q ]
        | ShowSection s
        | HideSection s -> [ SectionRef s ]
        | SkipToQuestion(a, b) -> [ QuestionRef a; QuestionRef b ]
        | SkipToSection(a, s) -> [ QuestionRef a; SectionRef s ]
        | Terminate(a, _) -> [ QuestionRef a ]

    let notScoredOk subject =
        // Flow may target any section, scored or not.
        function
        | SectionRef id when (section id).IsNone -> [ block "RULE-REFERENCE" subject $"'{subject}' refers to unknown section '{id}'." ]
        | SectionRef _ -> []
        | other -> checkRef subject other

    [ for f in r.Facts do
          yield! factRefs f |> List.collect (checkRef f.Id)
      for f in r.Flow do
          yield! conditions f.Id f.When
          yield! actionRefs f.Then |> List.collect (notScoredOk f.Id)
      for v in r.Validation do
          match v.Check with
          | RequiredWhen(q, c) ->
              yield! checkRef v.Id (QuestionRef q)
              yield! conditions v.Id c
          | Prohibited c -> yield! conditions v.Id c
          | AllowedRange(q, lo, hi) ->
              yield! checkRef v.Id (QuestionRef q)
              if lo > hi then block "RULE-TYPE" v.Id $"'{v.Id}' has an empty range."
      for rec' in r.Recommendations do
          yield! conditions rec'.Id rec'.When
          yield! rec'.RelatedQuestion |> Option.toList |> List.collect (QuestionRef >> notScoredOk rec'.Id)
          yield! rec'.RelatedSection |> Option.toList |> List.collect (SectionRef >> notScoredOk rec'.Id)
          if System.String.IsNullOrWhiteSpace rec'.Title then
              block "RULE-RECOMMENDATION" rec'.Id $"Recommendation '{rec'.Id}' has no title."
      for s in r.Completion.RequiredSections do
          yield! notScoredOk "completion" (SectionRef s)
      yield! r.Completion.Condition |> Option.toList |> List.collect (conditions "completion")
      match r.Completion.MinimumAnsweredPercent with
      | Some p when p <= 0.0 || p > 100.0 -> block "RULE-COMPLETION" "completion" "The minimum answered percentage must be within (0, 100]."
      | _ -> () ]

/// Conditions that compare answers or facts with values they cannot hold.
let rec private conditionTypes (content: Content) subject c =
    let factKind id = factOf content id |> Option.map _.Expr

    match c with
    | AnswerIs(q, v) -> valueTypes content subject q [ v ]
    | AnswerIn(q, vs) -> valueTypes content subject q vs
    | IsSpecial(q, s) ->
        match tryQuestion content q with
        | Some question when not (List.contains s question.SpecialStates) ->
            [ block "RULE-TYPE" subject $"'{subject}' tests a special state '{q}' does not offer." ]
        | _ -> []
    | FactTrue id ->
        match factKind id with
        | Some(BooleanFact _)
        | None -> []
        | Some _ -> [ block "RULE-TYPE" subject $"'{subject}' uses fact '{id}' as a condition, but it is not boolean." ]
    | CategoryIs(id, label) ->
        match factKind id with
        | Some(CategoryFact(cases, otherwise)) when not (List.contains label (otherwise |> Option.toList |> List.append (List.map snd cases))) ->
            [ block "RULE-TYPE" subject $"'{subject}' tests category '{label}', which fact '{id}' never produces." ]
        | Some(CategoryFact _)
        | None -> []
        | Some _ -> [ block "RULE-TYPE" subject $"'{subject}' uses fact '{id}' as a category, but it is not one." ]
    | Compare(a, _, b) -> numberTypes content subject a @ numberTypes content subject b
    | All cs
    | Any cs -> cs |> List.collect (conditionTypes content subject)
    | Not inner -> conditionTypes content subject inner
    | Always
    | Answered _ -> []

and private numberTypes (content: Content) subject =
    let factKind id = factOf content id |> Option.map _.Expr

    function
    | NumberFact id ->
        match factKind id with
        | Some(NumberFactOf _)
        | None -> []
        | Some _ -> [ block "RULE-TYPE" subject $"'{subject}' uses fact '{id}' as a number, but it is not one." ]
    | _ -> []

and private valueTypes (content: Content) subject q values =
    values
    |> List.choose (fun v ->
        match checkAnswer content q (Value v) with
        | Some(UnknownQuestion _)
        | None -> None
        | Some _ -> Some(block "RULE-TYPE" subject $"'{subject}' compares '{q}' with a value it cannot hold."))

/// The reference and type findings of one condition, for any consumer of
/// conditions (rules here, custom scoring expressions in `Expression`).
let checkCondition (content: Content) (subject: string) (c: Condition) =
    (conditionRefs c |> List.collect (referenceFindings content subject)) @ conditionTypes content subject c

let private types (content: Content) =
    let check = conditionTypes content
    let numberCheck = numberTypes content
    let r = content.Rules

    [ for f in r.Facts do
          match f.Expr with
          | BooleanFact c -> yield! check f.Id c
          | NumberFactOf n -> yield! numberCheck f.Id n
          | CategoryFact(cases, _) -> yield! cases |> List.collect (fst >> check f.Id)
      for f in r.Flow do
          yield! check f.Id f.When
      for v in r.Validation do
          match v.Check with
          | RequiredWhen(_, c)
          | Prohibited c -> yield! check v.Id c
          | AllowedRange _ -> ()
      for rec' in r.Recommendations do
          yield! check rec'.Id rec'.When ]

/// Facts must form a directed acyclic graph (ACR-007 §21).
let private cycles (content: Content) =
    let edges =
        content.Rules.Facts
        |> List.map (fun f ->
            f.Id,
            factRefs f
            |> List.choose (function
                | FactRef id -> Some id
                | _ -> None))
        |> Map.ofList

    let rec reaches (target: string) (visited: Set<string>) (id: string) =
        edges.TryFind id
        |> Option.defaultValue []
        |> List.exists (fun next -> next = target || (not (visited.Contains next) && reaches target (visited.Add next) next))

    [ for f in content.Rules.Facts do
          if reaches f.Id (Set.singleton f.Id) f.Id then
              block "RULE-CYCLE" f.Id $"Fact '{f.Id}' depends on itself." ]

/// Flow reads only content that comes before what it changes, and required
/// content is not unconditionally hidden (CAN-005 §28 "no permanently
/// unreachable required content").
let private flowOrder (content: Content) =
    let order = questions content |> List.mapi (fun i (_, q) -> q.Id, i) |> Map.ofList
    let position id = order.TryFind id |> Option.defaultValue System.Int32.MaxValue

    let sectionStart id =
        content.Sections
        |> List.tryFind (fun s -> s.Id = id)
        |> Option.bind (fun s -> s.Questions |> List.tryHead)
        |> Option.map (fun q -> position q.Id)
        |> Option.defaultValue System.Int32.MaxValue

    let requiredSection id =
        content.Sections |> List.exists (fun s -> s.Id = id && s.Required && s.Questions |> List.exists _.Required)

    let requiredQuestion id =
        questions content |> List.exists (fun (s, q) -> q.Id = id && s.Required && q.Required)

    [ for rule in content.Rules.Flow do
          let latest =
              questionDependencies content rule.When |> Seq.map position |> Seq.fold max -1

          // The position the rule acts at: what it reads must come before it
          // (or be the skip/termination trigger itself).
          let limit, strict =
              match rule.Then with
              | ShowQuestion q
              | HideQuestion q -> position q, true
              | ShowSection s
              | HideSection s -> sectionStart s, true
              | SkipToQuestion(from, _)
              | SkipToSection(from, _)
              | Terminate(from, _) -> position from, false

          if (strict && latest >= limit) || (not strict && latest > limit) then
              block "RULE-FLOW-ORDER" rule.Id $"Flow rule '{rule.Id}' depends on content that is not before what it changes."

          match rule.Then with
          | SkipToQuestion(from, target) when position target <= position from ->
              block "RULE-FLOW-ORDER" rule.Id $"Flow rule '{rule.Id}' skips backwards."
          | SkipToSection(from, target) when sectionStart target <= position from ->
              block "RULE-FLOW-ORDER" rule.Id $"Flow rule '{rule.Id}' skips backwards."
          | HideQuestion q when rule.When = Always && requiredQuestion q ->
              block "RULE-UNREACHABLE" q $"Required question '{q}' is always hidden."
          | HideSection s when rule.When = Always && requiredSection s ->
              block "RULE-UNREACHABLE" s $"Required section '{s}' is always hidden."
          | _ -> () ]

let private capabilities (content: Content) =
    let declared = Set.ofList content.Compatibility.Capabilities

    [ for cap in requiredCapabilities content do
          if not (declared.Contains cap) then
              finding
                  CompatibilityCategory
                  Blocker
                  "COMPAT-UNDECLARED-CAPABILITY"
                  (string cap)
                  $"The rules use {cap}, which the template does not declare." ]

/// Every rule finding, in a fixed order.
let check (content: Content) : Finding list =
    identities content @ references content @ types content @ cycles content @ flowOrder content @ capabilities content
