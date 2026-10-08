/// The custom scoring expression interpreter (AST-001..AST-006, ALG-004,
/// ARX-014): typed expression data, validated once at publication and
/// interpreted by precompiled pure F#. No survey can run code; it can only
/// combine the operators below.
///
/// Missing is explicit, never zero: arithmetic on a missing value is
/// missing; Sum/Mean/Min/Max skip missing values and are missing when all
/// are. Undefined arithmetic (a zero divisor, a non-finite result) is an
/// error, never NaN or Infinity. Evaluation returns a trace tree built by the
/// same recursion that produced the value.
module Echelon.Signal.Engine.Expression

open System
open Echelon.Signal.Engine.RuleModel
open Echelon.Signal.Engine.ResultModel
open Echelon.Signal.Engine.Template
open Echelon.Signal.Engine.Findings

/// One evaluated node: what it is, what it produced, and its operands.
type Node =
    { Label: string
      Value: float option
      Children: Node list }

let private leaf label value =
    { Label = label
      Value = value
      Children = [] }

/// Evaluates an expression against the rule environment (applicable answers,
/// section scores, facts). Ok None is "missing"; Error is "undefined".
let rec evaluate (env: Rules.Env) (expr: ScoreExpr) : Result<float option, string> * Node =
    let number n = Rules.number env Set.empty n

    let combine label (operands: ScoreExpr list) (f: float list -> Result<float, string>) (skipMissing: bool) =
        let results = operands |> List.map (evaluate env)
        let nodes = results |> List.map snd

        let value =
            match results |> List.tryPick (fun (r, _) -> match r with Error e -> Some e | Ok _ -> None) with
            | Some e -> Error e
            | None ->
                let values = results |> List.map (fun (r, _) -> match r with Ok v -> v | Error _ -> None)

                if skipMissing then
                    match List.choose id values with
                    | [] -> Ok None
                    | present -> f present |> Result.map Some
                elif List.forall Option.isSome values then
                    f (List.choose id values) |> Result.map Some
                else
                    Ok None

        let value =
            value
            |> Result.bind (function
                | Some v when Double.IsNaN v || Double.IsInfinity v -> Error $"{label} is not a finite number"
                | v -> Ok v)

        value,
        { Label = label
          Value = (match value with Ok v -> v | Error _ -> None)
          Children = nodes }

    match expr with
    | Num v -> Ok(Some v), leaf $"{v}" (Some v)
    | Item id -> let v = number (AnswerNumber id) in Ok v, leaf $"item {id}" v
    | Section id -> let v = number (SectionScore id) in Ok v, leaf $"section {id}" v
    | FactNumber id -> let v = number (NumberFact id) in Ok v, leaf $"fact {id}" v
    | Add xs -> combine "add" xs (List.sum >> Ok) false
    | Multiply xs -> combine "multiply" xs (List.fold (*) 1.0 >> Ok) false
    | Subtract(a, b) -> combine "subtract" [ a; b ] (fun vs -> Ok(vs[0] - vs[1])) false
    | Divide(a, b) -> combine "divide" [ a; b ] (fun vs -> if vs[1] = 0.0 then Error "division by zero" else Ok(vs[0] / vs[1])) false
    | Sum xs -> combine "sum" xs (List.sum >> Ok) true
    | Mean xs -> combine "mean" xs (List.average >> Ok) true
    | Min xs -> combine "min" xs (List.min >> Ok) true
    | Max xs -> combine "max" xs (List.max >> Ok) true
    | Bound(x, lo, hi) -> combine $"bound {lo}..{hi}" [ x ] (fun vs -> Ok(max lo (min hi vs[0]))) false
    | When(c, a, b) ->
        match Rules.truth env c with
        | Some true ->
            let r, n = evaluate env a
            r, { Label = "when: true"; Value = n.Value; Children = [ n ] }
        | Some false ->
            let r, n = evaluate env b
            r, { Label = "when: false"; Value = n.Value; Children = [ n ] }
        | None -> Ok None, leaf "when: unknown" None

/// The outcome of a custom scoring, rounded once at the declared boundary.
let score (env: Rules.Env) (custom: CustomScoring) : Scoring.Outcome * Node =
    match evaluate env custom.Expression with
    | Ok(Some v), node -> Scoring.Score(Math.Round(v, custom.Decimals, MidpointRounding.AwayFromZero), 1, 0), node
    | Ok None, node -> Scoring.NotScored(Scoring.InsufficientObservations(0, 1)), node
    | Error e, node -> Scoring.NotScored(Scoring.Undefined e), node

// ---------------------------------------------------------------------------
// Publication checks (AST-002 §§10-11).
// ---------------------------------------------------------------------------

/// Resource limits (AST-002 §11): the same for every author (Q11).
type Limits =
    { MaximumDepth: int
      MaximumNodes: int
      MaximumReferencedItems: int }

let defaultLimits =
    { MaximumDepth = 12
      MaximumNodes = 200
      MaximumReferencedItems = 200 }

let private children =
    function
    | Num _
    | Item _
    | Section _
    | FactNumber _ -> []
    | Add xs
    | Multiply xs
    | Sum xs
    | Mean xs
    | Min xs
    | Max xs -> xs
    | Subtract(a, b)
    | Divide(a, b) -> [ a; b ]
    | Bound(x, _, _) -> [ x ]
    | When(_, a, b) -> [ a; b ]

let rec private fold (f: 'a -> ScoreExpr -> 'a) (acc: 'a) (expr: ScoreExpr) =
    children expr |> List.fold (fold f) (f acc expr)

let rec depth (expr: ScoreExpr) =
    1 + (children expr |> List.map depth |> List.fold max 0)

let nodeCount (expr: ScoreExpr) = fold (fun n _ -> n + 1) 0 expr

let check (limits: Limits) (content: Content) (subject: string) (custom: CustomScoring) : Finding list =
    let block = finding ScoringCategory Blocker
    let expr = custom.Expression
    let nodes = fold (fun acc e -> e :: acc) [] expr |> List.rev

    let items =
        nodes |> List.choose (function Item id -> Some id | _ -> None) |> List.distinct

    [ if custom.LanguageVersion <> ExpressionLanguageVersion then
          block "EXPR-LANGUAGE-VERSION" subject $"Expression language {custom.LanguageVersion} is not supported (engine reads {ExpressionLanguageVersion})."
      if custom.Decimals < 0 || custom.Decimals > 10 then
          block "EXPR-DECIMALS" subject "Decimals must be between 0 and 10."
      if depth expr > limits.MaximumDepth then
          block "EXPR-TOO-COMPLEX" subject $"The expression is {depth expr} levels deep; the limit is {limits.MaximumDepth}."
      if nodes.Length > limits.MaximumNodes then
          block "EXPR-TOO-COMPLEX" subject $"The expression has {nodes.Length} nodes; the limit is {limits.MaximumNodes}."
      if items.Length > limits.MaximumReferencedItems then
          block "EXPR-TOO-COMPLEX" subject $"The expression reads {items.Length} questions; the limit is {limits.MaximumReferencedItems}."
      for node in nodes do
          match node with
          | Item id when (tryQuestion content id).IsNone -> block "EXPR-REFERENCE" subject $"Unknown question '{id}'."
          | Section id ->
              match content.Sections |> List.tryFind (fun s -> s.Id = id) with
              | None -> block "EXPR-REFERENCE" subject $"Unknown section '{id}'."
              | Some s when s.Scoring.IsNone -> block "EXPR-TYPE" subject $"Section '{id}' is not scored."
              | Some _ -> ()
          | FactNumber id ->
              match content.Rules.Facts |> List.tryFind (fun f -> f.Id = id) with
              | None -> block "EXPR-REFERENCE" subject $"Unknown fact '{id}'."
              | Some { Expr = NumberFactOf _ } -> ()
              | Some _ -> block "EXPR-TYPE" subject $"Fact '{id}' is not a number."
          | Divide(_, Num 0.0) -> block "EXPR-DIVISION-BY-ZERO" subject "A literal zero divisor."
          | Add []
          | Multiply []
          | Sum []
          | Mean []
          | Min []
          | Max [] -> block "EXPR-EMPTY" subject "An aggregate has no operands."
          | Bound(_, lo, hi) when lo > hi -> block "EXPR-TYPE" subject "A bound's minimum exceeds its maximum."
          | When(c, _, _) -> yield! RuleChecks.checkCondition content subject c
          | _ -> () ]
