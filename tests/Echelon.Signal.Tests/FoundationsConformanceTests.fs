/// Conformance: Signal's web surface is built on the Echelon foundations the
/// way the other Echelon applications are, and the repository says so
/// truthfully. Each test fails if a foundation stops being consumed: pinned
/// to the release the `echelon-current` registry channel selects, installed,
/// and actually used (not merely listed), with no local copy standing in.
module Echelon.Signal.Tests.FoundationsConformanceTests

open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Tests.Support

let private json (relative: string) = JsonNode.Parse(readRepoFile relative)

let private str (node: JsonNode) = node.GetValue<string>()

let private formaRelease =
    "https://github.com/kemiller2002/forma/releases/download/v1.0.1/echelon-foundry-design-system-1.0.1.tgz"

/// The Echelon brand for Forma 1.x (EDS-ADR-002), from kemiller2002/echelon-design-system;
/// 1.1.0 adds the Echelon application layer and the self-hosted fonts.
let private themeRelease =
    "https://github.com/kemiller2002/echelon-design-system/releases/download/v1.1.0/echelon-foundry-echelon-theme-1.1.0.tgz"

let private folioRelease =
    "https://github.com/kemiller2002/folio/releases/download/v0.4.1/echelon-foundry-print-components-0.4.1.tgz"

[<Fact>]
let ``foundations.json requires every foundation, at the echelon-current versions`` () =
    let capabilities = (json ".echelon/foundations.json").["capabilities"]

    for name, version in [ "aegis", "1.0.0"; "forma", "1.0.1"; "folio", "0.4.1"; "limen", "0.9.1" ] do
        Assert.True(capabilities.[name].["required"].GetValue<bool>(), $"{name} must be required")
        Assert.Equal(version, str capabilities.[name].["version"])

    Assert.Equal("aegis-boundaries.json", str capabilities.["aegis"].["boundaryManifest"])

[<Fact>]
let ``the npm foundations are pinned to immutable releases and locked`` () =
    let dependencies = (json "package.json").["dependencies"]
    Assert.Equal(formaRelease, str dependencies.["@echelon-foundry/design-system"])
    Assert.Equal(themeRelease, str dependencies.["@echelon-foundry/echelon-theme"])
    Assert.Equal(folioRelease, str dependencies.["@echelon-foundry/print-components"])
    Assert.Equal("0.9.1", str dependencies.["@echelon-foundry/limen"])

    let packages = (json "package-lock.json").["packages"]

    for name, version in
        [ "@echelon-foundry/design-system", "1.0.1"
          "@echelon-foundry/echelon-theme", "1.1.0"
          "@echelon-foundry/print-components", "0.4.1"
          "@echelon-foundry/limen", "0.9.1" ] do
        let locked = packages.[$"node_modules/{name}"]
        Assert.Equal(version, str locked.["version"])
        Assert.StartsWith("sha512-", str locked.["integrity"])
        // What is installed is what was pinned.
        Assert.Equal(version, str (json $"node_modules/{name}/package.json").["version"])

[<Fact>]
let ``the page takes Forma and Folio from the installed packages`` () =
    let css = readRepoFile "web/styles.css"

    let imports =
        Regex.Matches(css, "@import \"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toList

    Assert.Equal<string list>(
        [ "../node_modules/@echelon-foundry/design-system/dist/all.css"
          "../node_modules/@echelon-foundry/echelon-theme/dist/echelon.css"
          "../node_modules/@echelon-foundry/echelon-theme/dist/echelon-fonts.css"
          "../node_modules/@echelon-foundry/echelon-theme/dist/echelon-app.css"
          "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
          "../web-kernel/page.css" ],
        imports
    )

    // Folio's print stylesheet applies to print only.
    Assert.Contains("print.css\" print;", css)

    for imported in imports do
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, "web", imported))), $"{imported} is not installed")

    let html = readRepoFile "web/index.html"
    Assert.Contains("<link rel=\"stylesheet\" href=\"./styles.css\" />", html)
    Assert.Contains("<script type=\"module\" src=\"./main.js\"></script>", html)
    Assert.Contains("from \"../web-kernel/limen-wasm.js\"", readRepoFile "web/main.js")

/// Every page and its stylesheet, as the Pages site publishes them.
let private pages =
    [ "web/index.html", "web/styles.css"
      "web/survey/index.html", "web/styles.css"
      "web/admin/index.html", "web/admin/admin.css"
      "web/home/index.html", "web/home/home.css" ]

/// The theme's documented load order (echelon-theme 1.1.0, Echelon
/// application-layer contract): Forma, the brand, its fonts, the application
/// layer. Each comes once, and in this order.
let private brandOrder =
    [ "/@echelon-foundry/design-system/dist/all.css"
      "/@echelon-foundry/echelon-theme/dist/echelon.css"
      "/@echelon-foundry/echelon-theme/dist/echelon-fonts.css"
      "/@echelon-foundry/echelon-theme/dist/echelon-app.css" ]

[<Fact>]
let ``every page is branded Echelon, loading the theme after Forma`` () =
    for page, stylesheet in pages do
        let html = readRepoFile page
        Assert.True(Regex.IsMatch(html, "<html\\b[^>]*\\sdata-forma-brand=\"echelon\""), $"{page}: <html> lacks data-forma-brand=\"echelon\"")

        let imports =
            Regex.Matches(readRepoFile stylesheet, "@import \"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toList

        let positions (suffix: string) = imports |> List.indexed |> List.filter (fun (_, i) -> i.EndsWith suffix) |> List.map fst

        let found = brandOrder |> List.map (fun suffix -> suffix, positions suffix)

        for suffix, at in found do
            Assert.True((List.length at = 1), $"{stylesheet} must import {suffix} exactly once, not {List.length at} times")

        let order = found |> List.map (snd >> List.head)
        Assert.True((order = List.sort order), $"{stylesheet} must import Forma, echelon.css, echelon-fonts.css, then echelon-app.css; found positions {order}")

        for imported in imports do
            let dir = Path.GetDirectoryName(repoFile stylesheet)
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(dir, imported))), $"{stylesheet}: {imported} is not installed")

    // The installed theme is the Forma 1.x Echelon brand.
    let theme = readRepoFile "node_modules/@echelon-foundry/echelon-theme/dist/echelon.css"
    Assert.Contains("[data-forma-brand=\"echelon\"]", theme)

/// Each page preloads the Echelon body and display faces, the same files the
/// fonts stylesheet names (same address, so nothing is fetched twice), from
/// the site itself: the font no longer waits for the stylesheet's @import chain.
[<Fact>]
let ``every page preloads the Echelon body and display faces from the site`` () =
    for page, _ in pages do
        let html = readRepoFile page

        for font in [ "manrope-latin-wght-normal.woff2"; "newsreader-latin-wght-normal.woff2" ] do
            let preload =
                Regex.Match(html, $"<link rel=\"preload\" href=\"([^\"]*/node_modules/@echelon-foundry/echelon-theme/dist/fonts/{Regex.Escape font})\" as=\"font\" type=\"font/woff2\" crossorigin />")

            Assert.True(preload.Success, $"{page} does not preload {font}")
            let dir = Path.GetDirectoryName(repoFile page)
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(dir, preload.Groups[1].Value))), $"{page}: {font} is not installed at the preloaded address")

/// The Echelon fonts are self-hosted (owner decision 2026-11-09): every font
/// the theme's fonts stylesheet names is a relative address that resolves to
/// an installed WOFF2 file beside it, so the pages' `font-src 'self'` holds
/// and the Pages site (which copies the theme's whole dist/) serves them.
[<Fact>]
let ``the Echelon fonts are self-hosted WOFF2 files installed beside the fonts stylesheet`` () =
    let fontsCss = "node_modules/@echelon-foundry/echelon-theme/dist/echelon-fonts.css"

    let urls =
        Regex.Matches(readRepoFile fontsCss, "url\\(\"?([^\")]+)\"?\\)") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toList

    Assert.NotEmpty(urls)

    for url in urls do
        Assert.False(Regex.IsMatch(url, "^(?:[a-z][a-z0-9+.-]*:|//|/)", RegexOptions.IgnoreCase), $"{url} is not a relative, same-origin address")
        Assert.EndsWith(".woff2", url)
        let dir = Path.GetDirectoryName(repoFile fontsCss)
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(dir, url))), $"{fontsCss}: {url} is not installed")

    // Each family's SIL Open Font License text ships with it.
    for family in [ "manrope"; "newsreader"; "ibm-plex-mono" ] do
        Assert.True(
            File.Exists(repoFile $"node_modules/@echelon-foundry/echelon-theme/dist/fonts/licenses/{family}-OFL.txt"),
            $"the {family} licence is not installed"
        )

    // The application layer is the Echelon brand's, scoped to it.
    let app = readRepoFile "node_modules/@echelon-foundry/echelon-theme/dist/echelon-app.css"
    Assert.Contains("@layer echelon.app", app)
    Assert.Contains("[data-forma-brand=\"echelon\"] .echelon-panel", app)

[<Fact>]
let ``Signal keeps its own mark: the theme's Echelon Foundry logos are not used`` () =
    for page, _ in pages do
        let html = readRepoFile page
        Assert.Contains("class=\"forma-app-identity__name\">Signal<", html)
        Assert.Contains("brand/signal-mark.svg", html)
        Assert.DoesNotContain("logos/echelon-foundry/", html)

/// The Forma 0.x prefix (FORMA-NS-001). It is assembled here rather than
/// written out, so this file holds no Forma 0.x name and the guard below can
/// scan it like every other source.
let private ns = "ef"

/// The elements Folio 0.4.1 registers (its `src/components/register.js`).
/// Folio still owns this print surface under the old prefix; these exact
/// names, and no others, are not Forma's. Checked against the installed Folio
/// below.
let private folioElements =
    set
        [ "ef-print-document"; "ef-print-title-page"; "ef-print-section"; "ef-print-back-page"
          "ef-print-header"; "ef-print-footer"; "ef-print-page-number"; "ef-print-columns"
          "ef-print-sidebar"; "ef-print-layer"; "ef-print-break"; "ef-print-keep"
          "ef-print-metric"; "ef-print-integrity"; "ef-print-finding"; "ef-print-callout"
          "ef-print-figure"; "ef-print-table"; "ef-print-code"; "ef-print-toc"; "ef-print-note"
          "ef-print-security-record"; "ef-print-security-posture"; "ef-print-security-invariant"
          "ef-print-security-evidence"; "ef-print-security-unknown"; "ef-print-security-exception" ]

/// A name in the Forma 0.x namespace (prefix `ns`): a class, tag (also a
/// closing tag) or id, a custom property, a data attribute, or a DOM dataset
/// key.
let private zeroNamespace =
    Regex($@"(?:(?<![A-Za-z0-9_$/@-])|(?<=</))(?:--|data-)?{ns}-[A-Za-z0-9_-]*|dataset\??\.{ns}[A-Z]|dataset\[[""']{ns}[A-Z]")

/// The Forma 0.x names in a text: every name in that namespace except a
/// Folio element on the allow-list.
let private formaZeroNames (text: string) =
    zeroNamespace.Matches text
    |> Seq.map (fun m -> m.Value)
    |> Seq.filter (fun name -> not (folioElements.Contains name))
    |> Seq.toList

/// The one file allowed to hold deliberate Forma 0.x names: the guards' own
/// fixtures (see HANDOFF.md, WI-0109).
let private fixtures = "tests/fixtures/forma-zero-names.json"

let private skippedDirectories = set [ "bin"; "obj"; "node_modules" ]

let rec private sourceFiles (dir: string) : string list =
    let here =
        Directory.EnumerateFiles dir
        |> Seq.filter (fun f -> [ ".html"; ".css"; ".js"; ".mjs"; ".cjs"; ".fs"; ".fsx"; ".fsi"; ".json"; ".ts" ] |> List.contains (Path.GetExtension f))
        |> Seq.toList

    let nested =
        Directory.EnumerateDirectories dir
        |> Seq.filter (fun d -> not (skippedDirectories.Contains(Path.GetFileName d)))
        |> Seq.toList
        |> List.collect sourceFiles

    here @ nested

[<Fact>]
let ``no Forma 0.x name remains in Signal's pages, kernel, tests, tools or sources`` () =
    let relative (file: string) = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/')

    let sources =
        [ "web"; "web-kernel"; "tests"; "tools"; "src" ]
        |> List.collect (fun dir -> sourceFiles (repoFile dir))
        |> List.filter (fun file -> relative file <> fixtures)

    // The scan covers this file and the browser and Pages tests, not just the pages.
    let scanned = sources |> List.map relative |> Set.ofList

    for expected in
        [ "web/index.html"
          "web-kernel/limen-wasm.js"
          "tests/Echelon.Signal.Tests/FoundationsConformanceTests.fs"
          "tests/browser/assessment.spec.js"
          "tools/pages/verify-deployed.test.mjs" ] do
        Assert.True(scanned.Contains expected, $"the guard does not scan {expected}")

    for source in sources do
        let hits = formaZeroNames (File.ReadAllText source)
        Assert.True(hits.IsEmpty, $"{relative source} still uses Forma 0.x names: {hits}")

[<Fact>]
let ``the Forma 0.x guard recognises every 0.x form and exempts only Folio's elements`` () =
    let fixture = json fixtures

    for zero in fixture.["formaZero"].AsArray() |> Seq.map str do
        Assert.False((formaZeroNames zero).IsEmpty, $"not recognised as Forma 0.x: {zero}")

    for kept in fixture.["notFormaZero"].AsArray() |> Seq.map str do
        Assert.True((formaZeroNames kept).IsEmpty, $"wrongly reported as Forma 0.x: {kept}")

    // The allow-list is exactly what the installed Folio registers.
    let registered =
        Regex.Matches(readRepoFile "node_modules/@echelon-foundry/print-components/src/components/register.js", $"\"({ns}-print-[a-z-]+)\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Set.ofSeq

    Assert.Equal<Set<string>>(registered, folioElements)

[<Fact>]
let ``the kernel registers Folio and starts Limen from the installed packages`` () =
    let kernel = readRepoFile "web-kernel/limen-wasm.js"

    for specifier in
        [ "../node_modules/@echelon-foundry/print-components/src/components/register.js"
          "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js" ] do
        Assert.Contains($"\"{specifier}\"", kernel)
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(repositoryRoot, "web-kernel", specifier))), specifier)

[<Fact>]
let ``the page composes Forma's components and Folio's document primitives`` () =
    let html = readRepoFile "web/index.html"

    for marker in
        [ "<forma-fault-inline class=\"forma-component-tag\">"
          "class=\"forma-fault forma-fault--inline"
          "<forma-alert class=\"forma-component-tag\">"
          "<forma-data-grid class=\"forma-component-tag\">"
          // Signal's mark, as on the survey page (the record header gave way to it, signal#140).
          "class=\"forma-app-identity\""
          "class=\"forma-survey-progress\""
          "class=\"forma-question\""
          "class=\"forma-ordinal-scale forma-ordinal-scale--5\""
          "class=\"forma-special-choices\""
          "class=\"forma-status-lozenge\""
          "<ef-print-document"
          "<ef-print-table>"
          "<ef-print-page-number>" ] do
        Assert.True(html.Contains marker, $"web/index.html lacks {marker}")

[<Fact>]
let ``nothing forks Forma or bypasses Limen's binding rules`` () =
    let sources = [ "web/index.html"; "web/styles.css"; "web/main.js"; "web-kernel/page.css"; "web-kernel/limen-wasm.js" ]

    for source in sources do
        let text = readRepoFile source
        // No local design tokens and no restyled Forma components.
        Assert.False(Regex.IsMatch(text, @"--forma-[\w-]+\s*:"), $"{source} defines a Forma token")
        Assert.False(Regex.IsMatch(text, @"(^|[\s,}])\.forma-[\w-]+[^{;]*\{", RegexOptions.Multiline), $"{source} restyles a Forma component")
        // Forma's wrappers are inert; only Folio registers elements.
        Assert.DoesNotContain("customElements.define", text)
        // State cues are data-* attributes plus CSS: never inline or bound styles.
        Assert.DoesNotContain("data-bind-style", text)
        Assert.False(Regex.IsMatch(text, @"\sstyle="""), $"{source} has an inline style")

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

          for selector in rule.Groups[1].Value.Split(',') do
              let last = Regex.Split(selector.Trim(), @"[\s>+~]+") |> Array.last
              let classes = Regex.Matches(last, @"\.([\w-]+)") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq
              yield selector.Trim(), classes, properties ]

let private ownedByFoundations (name: string) = name.StartsWith "forma-" || name.StartsWith "echelon-"

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

    let stylesheets =
        [ "web/index.html", [ "web/styles.css"; "web-kernel/page.css" ]
          "web/survey/index.html", [ "web/styles.css"; "web-kernel/page.css" ]
          "web/admin/index.html", [ "web/admin/admin.css"; "web-kernel/page.css" ]
          "web/home/index.html", [ "web/home/home.css" ] ]

    // The check sees the Forma components it must: their declarations are read.
    Assert.Contains("grid-template", declared |> Map.find "forma-best-worst__row")
    Assert.Contains("padding", declared |> Map.find "echelon-panel")

    let clashes =
        [ for page, sheets in stylesheets do
              let rules = sheets |> List.collect (readRepoFile >> rulesOf)

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

    Assert.Empty clashes

[<Fact>]
let ``Aegis is referenced, pinned and its boundary codes are declared`` () =
    let project = readRepoFile "src/Echelon.Signal.Application/Echelon.Signal.Application.fsproj"
    Assert.Contains("<PackageReference Include=\"EchelonFoundry.Aegis.Core\" />", project)
    Assert.Contains("<PackageVersion Include=\"EchelonFoundry.Aegis.Core\" Version=\"1.0.0\" />", readRepoFile "Directory.Packages.props")

    let manifest = json "aegis-boundaries.json"
    Assert.Equal("aegis/boundaries/v1", str manifest.["schema"])
    Assert.Equal("Signal", str manifest.["application"])

    let declared =
        manifest.["boundaries"].AsArray()
        |> Seq.collect (fun boundary -> boundary.["codes"].AsArray() |> Seq.map str)
        |> Set.ofSeq

    let used =
        Regex.Matches(readRepoFile "src/Echelon.Signal.Application/Boundary.fs", "\"(SIGNAL\\.[A-Z_.]+)\"")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Set.ofSeq

    Assert.NotEmpty used
    Assert.Equal<Set<string>>(used, declared)

[<Fact>]
let ``the Limen boundary names the F# engine and the browser kernel`` () =
    let boundary = (json "limen.config.json").["boundary"]
    let paths (name: string) = boundary.[name].AsArray() |> Seq.map str |> Seq.toList

    Assert.Null(boundary.["notApplicable"])
    Assert.Equal<string list>([ "src/Echelon.Signal.Engine"; "src/Echelon.Signal.Admin"; "src/Echelon.Signal.Application" ], paths "engine")
    Assert.Equal<string list>([ "src/Echelon.Signal.Browser"; "web"; "web-kernel" ], paths "kernel")

    for path in paths "engine" @ paths "kernel" do
        Assert.True(Directory.Exists(repoFile path), path)

[<Fact>]
let ``the engine answers the Limen contract the installed kernel offers`` () =
    let text = readRepoFile "node_modules/@echelon-foundry/limen/dist/generated/core.js"
    Assert.Contains($"\"{Echelon.Signal.Application.Limen.core.Id}\"", text)
    Assert.Contains($"\"{Echelon.Signal.Application.Limen.core.Fingerprint}\"", text)
