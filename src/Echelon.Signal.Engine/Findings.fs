/// Validation findings: a stable reason code, a category and a severity.
/// Blockers stop publication; warnings need acknowledgement.
module Echelon.Signal.Engine.Findings

type Category =
    | StructuralCategory
    | AnswerCategory
    | ScoringCategory
    | CompletionCategory
    | CompatibilityCategory
    | EncodingCategory
    | PrivacyCategory
    | FixturesCategory
    /// Flow, facts, validation rules, completion and recommendations.
    | RulesCategory

type Severity =
    | Blocker
    | Warning

/// A finding with a stable reason code (ARX-012 "stable reason codes").
type Finding =
    { Code: string
      Category: Category
      Severity: Severity
      /// The id the finding is about ("" for the template as a whole).
      Subject: string
      Message: string }

let finding category severity code subject message =
    { Code = code
      Category = category
      Severity = severity
      Subject = subject
      Message = message }

let duplicates (ids: string list) =
    ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst
