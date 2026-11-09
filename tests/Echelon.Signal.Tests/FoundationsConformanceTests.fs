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

/// The Echelon brand for Forma 1.x (EDS-ADR-002), from kemiller2002/echelon-design-system.
let private themeRelease =
    "https://github.com/kemiller2002/echelon-design-system/releases/download/v1.0.0/echelon-foundry-echelon-theme-1.0.0.tgz"

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
          "@echelon-foundry/echelon-theme", "1.0.0"
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

[<Fact>]
let ``every page is branded Echelon, loading the theme after Forma`` () =
    for page, stylesheet in pages do
        let html = readRepoFile page
        Assert.True(Regex.IsMatch(html, "<html\\b[^>]*\\sdata-forma-brand=\"echelon\""), $"{page}: <html> lacks data-forma-brand=\"echelon\"")

        let imports =
            Regex.Matches(readRepoFile stylesheet, "@import \"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Seq.toList

        let position (suffix: string) = imports |> List.tryFindIndex (fun i -> i.EndsWith suffix)
        let forma = position "/@echelon-foundry/design-system/dist/all.css"
        let theme = position "/@echelon-foundry/echelon-theme/dist/echelon.css"
        Assert.True(forma.IsSome, $"{stylesheet} does not import Forma")
        Assert.True(theme.IsSome, $"{stylesheet} does not import the Echelon theme")
        Assert.True(forma < theme, $"{stylesheet} imports the Echelon theme before Forma")

        for imported in imports do
            let dir = Path.GetDirectoryName(repoFile stylesheet)
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(dir, imported))), $"{stylesheet}: {imported} is not installed")

    // The installed theme is the Forma 1.x Echelon brand.
    let theme = readRepoFile "node_modules/@echelon-foundry/echelon-theme/dist/echelon.css"
    Assert.Contains("[data-forma-brand=\"echelon\"]", theme)

[<Fact>]
let ``Signal keeps its own mark: the theme's Echelon Foundry logos are not used`` () =
    for page, _ in pages do
        let html = readRepoFile page
        Assert.Contains("class=\"forma-app-identity__name\">Signal<", html)
        Assert.Contains("brand/signal-mark.svg", html)
        Assert.DoesNotContain("logos/echelon-foundry/", html)

/// A Forma 0.x name (FORMA-NS-001): an `ef-` class, tag or id, a `--ef-`
/// property, a `data-ef-` attribute or an `efX` dataset key. Folio owns the
/// `ef-print-*` print surface (Folio 0.4.1), so those names are not Forma's.
let private formaZeroName =
    Regex(@"(?<![A-Za-z0-9_$/@-])(?:--|data-)?ef-(?!print-)|dataset\??\.ef[A-Z]|dataset\[[""']ef[A-Z]")

[<Fact>]
let ``no Forma 0.x name remains in Signal's pages, stylesheets or kernel`` () =
    let sources =
        [ "web"; "web-kernel" ]
        |> List.collect (fun dir ->
            Directory.EnumerateFiles(repoFile dir, "*.*", SearchOption.AllDirectories)
            |> Seq.filter (fun f -> [ ".html"; ".css"; ".js"; ".mjs" ] |> List.contains (Path.GetExtension f))
            |> Seq.toList)

    Assert.NotEmpty sources

    for source in sources do
        let hits = formaZeroName.Matches(File.ReadAllText source) |> Seq.map (fun m -> m.Value) |> Seq.toList
        Assert.True(hits.IsEmpty, $"{source} still uses Forma 0.x names: {hits}")

    // The guard recognises the 0.x forms and leaves Folio's names alone.
    for zero in [ "class=\"ef-question\""; "<ef-alert>"; "var(--ef-space-2)"; "data-ef-state"; "el.dataset.efState" ] do
        Assert.True(formaZeroName.IsMatch zero, zero)

    for kept in [ "<ef-print-table>"; "var(--ef-print-accent)"; "class=\"forma-question\""; "ref-count" ] do
        Assert.False(formaZeroName.IsMatch kept, kept)

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
          "class=\"forma-record-header"
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
