/// Conformance (#139's review): Signal composes Forma and the Echelon theme
/// without restyling them. A Signal class on the same element as a Forma or
/// theme class, or a Signal id selector on a Forma-styled element, must not
/// set what Forma or the theme sets there. Split from
/// FoundationsConformanceTests, whose regex sees only selectors that name a
/// Forma class.
module Echelon.Signal.Tests.FormaOverrideGuardTests

open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Tests.Support

/// A CSS property's family, so a longhand (padding-inline, column-gap,
/// grid-template-columns) meets the shorthand a component sets.
let private family (property: string) =
    let p = property.Trim().ToLowerInvariant()

    match [ "padding"; "margin"; "inset"; "border" ] |> List.tryFind (fun f -> p = f || p.StartsWith(f + "-")) with
    | Some f -> f
    | None when List.contains p [ "gap"; "column-gap"; "row-gap"; "grid-gap" ] -> "gap"
    | None when p.StartsWith "grid-template" -> "grid-template"
    | None -> p

/// Each selector of a stylesheet, the classes its last compound selects (the
/// element it styles) and the property families its rule sets.
let private rulesOf (css: string) =
    let plain = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline)

    [ for rule in Regex.Matches(plain, @"([^{}]+)\{([^{}]*)\}") do
          let properties =
              rule.Groups[2].Value.Split(';')
              |> Array.filter (fun declaration -> declaration.Contains ':')
              |> Array.map (fun declaration -> family (declaration.Split(':')[0]))
              |> Set.ofArray

          // The rule's own selector: after the last statement before it, so an
          // `@import "….css";` line is not read as part of the first selector
          // (its ".css" counted as a class and left that rule unchecked).
          let prelude = rule.Groups[1].Value.Split(';') |> Array.last

          for selector in prelude.Split(',') do
              let last = Regex.Split(selector.Trim(), @"[\s>+~]+") |> Array.last
              let classes = Regex.Matches(last, @"\.([\w-]+)") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
              yield selector.Trim(), classes, properties ]

let private ownedByFoundations (name: string) = name.StartsWith "forma-" || name.StartsWith "echelon-"

/// The classes a page's markup names. A selector naming a class the page never
/// carries cannot match there (".respondent-progress #progress" is the survey
/// page's, not the assessment page's #progress), so it is not held against
/// that page's elements. Classes bound at run time (data-bind-class) are not
/// in the source; neither guard sees them.
let private pageClasses (html: string) =
    Regex.Matches(html, "\\sclass=\"([^\"]+)\"")
    |> Seq.collect (fun m -> m.Groups[1].Value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
    |> Set.ofSeq

/// A selector without its `:not(…)` arguments (nested parentheses included):
/// a class a rule excludes need not be on the page for the rule to match.
let private withoutNegations (selector: string) =
    let rec skip (depth: int) (rest: char list) =
        match rest with
        | [] -> []
        | ')' :: tail when depth = 1 -> tail
        | ')' :: tail -> skip (depth - 1) tail
        | '(' :: tail -> skip (depth + 1) tail
        | _ :: tail -> skip depth tail

    let rec keep (rest: char list) =
        match rest with
        | ':' :: 'n' :: 'o' :: 't' :: '(' :: tail -> keep (skip 1 tail)
        | c :: tail -> c :: keep tail
        | [] -> []

    selector |> List.ofSeq |> keep |> Array.ofList |> System.String

/// Whether every class a selector requires (outside `:not(…)`) is on the page.
let private canMatch (classes: Set<string>) (selector: string) =
    Regex.Matches(withoutNegations selector, @"\.([\w-]+)") |> Seq.forall (fun m -> classes.Contains m.Groups[1].Value)

/// The full list on failure (xUnit's collection message truncates it).
let private noneOf (clashes: string list) =
    Assert.True(List.isEmpty clashes, $"{List.length clashes} found:\n" + String.concat "\n" clashes)

/// Each page and the stylesheets that style it.
let private stylesheets =
    [ "web/index.html", [ "web/styles.css"; "web-kernel/page.css" ]
      "web/survey/index.html", [ "web/styles.css"; "web-kernel/page.css" ]
      "web/admin/index.html", [ "web/admin/admin.css"; "web-kernel/page.css" ]
      "web/home/index.html", [ "web/home/home.css" ] ]

/// #139's review: a Signal class on the same element as a Forma or theme
/// class must not set what that class sets (the respondent page's best-worst
/// row and card did: grid columns, gap and padding). The regex above sees
/// only selectors that name a Forma class; this sees a Signal class that
/// reaches the component through the element. Classes bound at run time
/// (data-bind-class) are not in the source and are not checked here.
[<Fact>]
let ``no Signal class sets what the Forma or theme class on the same element sets`` () =
    let declared =
        rulesOf (
            readRepoFile "node_modules/@echelon-foundry/design-system/dist/all.css"
            + readRepoFile "node_modules/@echelon-foundry/echelon-theme/dist/echelon-app.css"
        )
        |> List.collect (fun (_, classes, properties) -> classes |> Set.toList |> List.filter ownedByFoundations |> List.map (fun c -> c, properties))
        |> List.groupBy fst
        |> List.map (fun (name, found) -> name, found |> List.map snd |> Set.unionMany)
        |> Map.ofList

    // The check sees the Forma components it must: their declarations are read.
    Assert.Contains("grid-template", declared |> Map.find "forma-best-worst__row")
    Assert.Contains("padding", declared |> Map.find "echelon-panel")

    let clashes =
        [ for page, sheets in stylesheets do
              let classes = pageClasses (readRepoFile page)
              let rules = sheets |> List.collect (readRepoFile >> rulesOf) |> List.filter (fun (selector, _, _) -> canMatch classes selector)

              for element in Regex.Matches(readRepoFile page, "\\sclass=\"([^\"]+)\"") do
                  let theirs, mine =
                      element.Groups[1].Value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                      |> List.ofArray
                      |> List.partition ownedByFoundations

                  for selector, selected, properties in rules do
                      for name in mine |> List.filter selected.Contains do
                          for owner in theirs do
                              let clash = Set.intersect properties (declared |> Map.tryFind owner |> Option.defaultValue Set.empty)
                              let listed = String.concat ", " clash

                              if not clash.IsEmpty then
                                  yield $"{page}: '{selector}' sets {listed}, which .{owner} on the same element (.{name}) sets" ]

    noneOf clashes

/// The form controls Forma's foundations style by element, not by class.
let private formControls = [ "input"; "textarea"; "select"; "button" ]

/// #139's review (LOW): an id selector reaches a Forma-styled element the
/// class check cannot see (the submission link's read-only border). A Signal
/// rule whose subject is an element's id must not set what Forma sets on that
/// element: by its form-control tag (Forma's foundations) or by its Forma or
/// theme classes.
[<Fact>]
let ``no Signal id selector sets what Forma sets on that element`` () =
    let formaRules =
        rulesOf (
            readRepoFile "node_modules/@echelon-foundry/design-system/dist/all.css"
            + readRepoFile "node_modules/@echelon-foundry/echelon-theme/dist/echelon-app.css"
        )

    let byClass (name: string) =
        formaRules |> List.filter (fun (_, classes, _) -> classes.Contains name) |> List.map (fun (_, _, properties) -> properties) |> Set.unionMany

    let byTag (tag: string) =
        formaRules
        |> List.filter (fun (selector, classes, _) -> classes.IsEmpty && Regex.IsMatch(selector, $@"(^|[(,\s]){tag}(?![\w-])"))
        |> List.map (fun (_, _, properties) -> properties)
        |> Set.unionMany

    let controls = formControls |> List.map (fun tag -> tag, byTag tag) |> Map.ofList
    // The check sees what it must: Forma's foundations set an input's border.
    Assert.Contains("border", controls |> Map.find "input")

    let clashes =
        [ for page, sheets in stylesheets do
              let classes = pageClasses (readRepoFile page)
              let rules = sheets |> List.collect (readRepoFile >> rulesOf) |> List.filter (fun (selector, _, _) -> canMatch classes selector)

              for element in Regex.Matches(readRepoFile page, @"<(\w+)\b([^>]*)>") do
                  let tag = element.Groups[1].Value.ToLowerInvariant()
                  let attributes = element.Groups[2].Value
                  let id = Regex.Match(attributes, "\\sid=\"([^\"]+)\"")

                  if id.Success then
                      let owned =
                          Regex.Match(attributes, "\\sclass=\"([^\"]+)\"").Groups[1].Value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                          |> List.ofArray
                          |> List.filter ownedByFoundations

                      let formaSets = Set.unionMany ((controls |> Map.tryFind tag |> Option.defaultValue Set.empty) :: (owned |> List.map byClass))
                      let name = id.Groups[1].Value

                      for selector, _, properties in rules do
                          let subject = Regex.Split(selector, @"[\s>+~]+") |> Array.last

                          if Regex.IsMatch(subject, $@"#{Regex.Escape name}(?![\w-])") then
                              let clash = Set.intersect properties formaSets
                              let listed = String.concat ", " clash

                              if not clash.IsEmpty then
                                  yield $"{page}: '{selector}' sets {listed}, which Forma sets on <{tag} id=\"{name}\">" ]

    noneOf clashes

/// signal#141 review: the first rule after a sheet's `@import` lines is
/// checked like any other (its selector is its own, with no ".css" class).
[<Fact>]
let ``the guards read the first rule after @import lines as its own selector`` () =
    let rules = rulesOf "@import \"../node_modules/x/dist/all.css\";\n@import \"./page.css\" print;\n.first-rule { padding: 0; }\n.second { gap: 1rem; }"
    let first, classes, properties = rules.Head
    Assert.Equal(".first-rule", first)
    Assert.Equal<Set<string>>(set [ "first-rule" ], classes)
    Assert.Contains("padding", properties)
    Assert.True(canMatch (set [ "first-rule" ]) first)
    // The sheets the guards read begin with @import lines, and their first
    // rule's selector is read without them.
    for sheet in [ "web/styles.css"; "web/admin/admin.css"; "web/home/home.css" ] do
        let text = readRepoFile sheet
        Assert.True(text.IndexOf "@import" >= 0 && text.IndexOf "@import" < text.IndexOf "{", $"{sheet} starts with @import")
        let selector, _, _ = (rulesOf text).Head
        Assert.DoesNotContain("@import", selector)
        Assert.DoesNotContain(".css", selector)

/// signal#141 review: a class a rule only excludes (`:not(.x)`) need not be
/// on the page; the rule is still checked.
[<Fact>]
let ``a rule's :not() classes do not stop the guards checking it`` () =
    let page = set [ "respondent"; "activity" ]
    Assert.True(canMatch page ".respondent > .activity:not(:has(#loading))")
    Assert.True(canMatch page ".respondent:not(.never-on-the-page) .activity")
    Assert.True(canMatch page ".activity:not(:is(.a, .b), .c)")
    Assert.False(canMatch page ".respondent .missing:not(.activity)")
    Assert.Equal(".a .b", withoutNegations ".a:not(.x) .b:not(:has(.y, .z))")
