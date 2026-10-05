/// An assessment and its scoring, as data and total functions.
///
/// The rules are the Software Delivery Reality Assessment's (item bank
/// `SDRA` 0.1.0-draft, `scoring`): a dimension score is the mean of its
/// numeric answers divided by 4, times 100, and a dimension with fewer than
/// the minimum number of numeric answers is not scored. Missing data is never
/// zero by implication: "don't know", "not observed" and "not applicable" are
/// answers, but not numbers, and an unscored dimension says so rather than
/// reporting 0.
module Echelon.Signal.Engine.Assessment

open System

/// The five-point ordinal frequency scale (`OrdinalFrequency5`).
type Frequency =
    | Never
    | Rarely
    | Sometimes
    | Often
    | AlmostAlways

/// An answer that is deliberately not a number.
type NonNumeric =
    | DontKnow
    | NotObserved
    | NotApplicable

type Answer =
    | Rated of Frequency
    | Withheld of NonNumeric

type Dimension = { Id: string; Label: string }

type Item =
    { Id: string
      DimensionId: string
      Prompt: string }

type Assessment =
    { Id: string
      Title: string
      Version: string
      Dimensions: Dimension list
      Items: Item list
      MinimumNumericAnswers: int }

/// Answers by item id. An item absent from the map is unanswered.
type Answers = Map<string, Answer>

/// What one dimension's answers support. Coverage (how many numeric answers
/// of how many items) travels with the result: it is not performance.
type DimensionResult =
    | Scored of score: float * numeric: int * items: int
    | Unscored of numeric: int * items: int * minimum: int

let frequencyValue =
    function
    | Never -> 0
    | Rarely -> 1
    | Sometimes -> 2
    | Often -> 3
    | AlmostAlways -> 4

let frequencyLabel =
    function
    | Never -> "Never or almost never"
    | Rarely -> "Rarely"
    | Sometimes -> "Sometimes"
    | Often -> "Often"
    | AlmostAlways -> "Almost always"

let private codes =
    [ "0", Rated Never
      "1", Rated Rarely
      "2", Rated Sometimes
      "3", Rated Often
      "4", Rated AlmostAlways
      "dont-know", Withheld DontKnow
      "not-observed", Withheld NotObserved
      "not-applicable", Withheld NotApplicable ]

/// The answer a page code names, or None for a code that names none
/// (including the empty "not answered yet" choice).
let parseAnswer (code: string) : Answer option =
    codes |> List.tryFind (fst >> (=) code) |> Option.map snd

/// The page code for an answer; `parseAnswer (answerCode a) = Some a`.
let answerCode (answer: Answer) =
    codes |> List.find (snd >> (=) answer) |> fst

let answerLabel =
    function
    | Rated frequency -> frequencyLabel frequency
    | Withheld DontKnow -> "Don't know"
    | Withheld NotObserved -> "Not observed"
    | Withheld NotApplicable -> "Not applicable"

let numericValue =
    function
    | Rated frequency -> Some(frequencyValue frequency)
    | Withheld _ -> None

let itemsOf (assessment: Assessment) (dimension: Dimension) =
    assessment.Items |> List.filter (fun item -> item.DimensionId = dimension.Id)

/// The items that still have no answer, in assessment order.
let unanswered (assessment: Assessment) (answers: Answers) =
    assessment.Items |> List.filter (fun item -> not (answers.ContainsKey item.Id))

/// Scores one dimension. Rounded to one decimal place, half away from zero,
/// so the same answers always give the same printed number.
let scoreDimension (assessment: Assessment) (answers: Answers) (dimension: Dimension) =
    let items = itemsOf assessment dimension

    let values =
        items
        |> List.choose (fun item -> answers.TryFind item.Id |> Option.bind numericValue)

    if values.Length < assessment.MinimumNumericAnswers then
        Unscored(values.Length, items.Length, assessment.MinimumNumericAnswers)
    else
        let mean = float (List.sum values) / float values.Length
        Scored(Math.Round(mean / 4.0 * 100.0, 1, MidpointRounding.AwayFromZero), values.Length, items.Length)

let score (assessment: Assessment) (answers: Answers) =
    assessment.Dimensions
    |> List.map (fun dimension -> dimension, scoreDimension assessment answers dimension)
