/// Localization as a presentation transformation over canonical data
/// (ADM-069): numbers, percentages, dates, plurals, direction and label
/// order differ by locale; scoring, aggregation, privacy, comparison,
/// sorting and hashes never do.
///
/// The browser runtime has no culture data (invariant globalization), so
/// each supported locale's conventions are declared here, not looked up.
///
/// Pure.
module Echelon.Signal.Admin.Locale

open System
open System.Globalization

type Direction =
    | LeftToRight
    | RightToLeft

/// CLDR plural categories.
type PluralCategory =
    | Zero
    | One
    | Two
    | Few
    | Many
    | Other

[<NoComparison; NoEquality>]
type Locale =
    { Tag: string
      Direction: Direction
      Decimal: string
      Group: string
      /// Where the percent sign goes, and the space before it.
      PercentSuffix: string
      /// Day, month and year order, with the separator.
      DatePattern: string
      Plural: int -> PluralCategory }

let private onePlural n = if n = 1 then One else Other

/// The CLDR rule for Arabic integers.
let private arabicPlural n =
    let mod100 = n % 100

    if n = 0 then Zero
    elif n = 1 then One
    elif n = 2 then Two
    elif mod100 >= 3 && mod100 <= 10 then Few
    elif mod100 >= 11 && mod100 <= 99 then Many
    else Other

let english =
    { Tag = "en-US"; Direction = LeftToRight; Decimal = "."; Group = ","; PercentSuffix = "%"; DatePattern = "MM/dd/yyyy"; Plural = onePlural }

let german =
    { Tag = "de-DE"; Direction = LeftToRight; Decimal = ","; Group = "."; PercentSuffix = " %"; DatePattern = "dd.MM.yyyy"; Plural = onePlural }

let french =
    { Tag = "fr-FR"; Direction = LeftToRight; Decimal = ","; Group = " "; PercentSuffix = " %"; DatePattern = "dd/MM/yyyy"; Plural = (fun n -> if n = 0 || n = 1 then One else Other) }

let arabic =
    { Tag = "ar-EG"; Direction = RightToLeft; Decimal = "٫"; Group = "٬"; PercentSuffix = "٪"; DatePattern = "dd/MM/yyyy"; Plural = arabicPlural }

let supported = [ english; german; french; arabic ]

let find (tag: string) = supported |> List.tryFind (fun l -> l.Tag = tag) |> Option.defaultValue english

/// A number with fixed decimals in the locale's separators.
let number (locale: Locale) (decimals: int) (value: float) =
    let invariant = Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("N" + string decimals, CultureInfo.InvariantCulture)
    invariant.Replace(",", "\u0001").Replace(".", locale.Decimal).Replace("\u0001", locale.Group)

/// A proportion (0-1) as a percentage.
let percent (locale: Locale) (decimals: int) (proportion: float) = number locale decimals (proportion * 100.0) + locale.PercentSuffix

/// A date in the locale's order (the canonical value stays an ISO date).
let date (locale: Locale) (value: DateOnly) = value.ToString(locale.DatePattern, CultureInfo.InvariantCulture)

/// The order labels are laid out in: mirrored for right-to-left locales; the
/// data's own order (its sort) is unchanged.
let layout (locale: Locale) (labels: 'a list) =
    match locale.Direction with
    | LeftToRight -> labels
    | RightToLeft -> List.rev labels

/// The HTML `dir` value.
let dir (locale: Locale) =
    match locale.Direction with
    | LeftToRight -> "ltr"
    | RightToLeft -> "rtl"

/// Isolates embedded text (a group id, a label in another script) so
/// bidirectional runs cannot reorder the sentence around it.
let isolate (text: string) = "⁨" + text + "⁩"
