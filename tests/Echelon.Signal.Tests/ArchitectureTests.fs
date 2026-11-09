/// The four-tier dependency rules (.sde/architecture/FOUR-TIER-ARCHITECTURE.md),
/// enforced on the compiled assemblies and their sources rather than left to
/// convention (WI-0100).
///
/// - Engine (Tier 1/2, survey semantics) references nothing of Echelon's or
///   Signal's: only FSharp.Core and the deterministic parts of the runtime.
/// - Admin (Tier 1/2, administrator semantics) may add only Engine and the
///   pure contracts it decides over (Arca.Core records, Limen.Routing codecs):
///   never a provider, identity, fault-handling or network assembly.
/// - Neither reads an ambient effect (clock, entropy, file system, network,
///   console, JS interop): effects are requested as data and performed by the
///   Application and Browser hosts.
/// - The Browser project is the Tier 4 WASM shim: every export forwards to
///   Application.Runtime and decides nothing.
module Echelon.Signal.Tests.ArchitectureTests

open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Xunit
open Echelon.Signal.Tests.Support

let private referencesOf (assembly: Assembly) =
    assembly.GetReferencedAssemblies() |> Array.map _.Name |> Set.ofArray

let private runtime =
    set [ "FSharp.Core"; "netstandard"; "System.Runtime"; "System.Memory"; "System.Collections"
          "System.Text.Json"; "System.Text.RegularExpressions"; "System.Security.Cryptography" ]

let private engineAssembly = typeof<Echelon.Signal.Engine.Assessment.Dimension>.Assembly
let private adminAssembly = typeof<Echelon.Signal.Admin.Routes.Sort>.Assembly

let private outside (allowed: Set<string>) (assembly: Assembly) =
    Set.difference (referencesOf assembly) allowed |> Set.toList

[<Fact>]
let ``the engine references only FSharp.Core and the deterministic runtime`` () =
    Assert.Equal<string list>([], outside runtime engineAssembly)

[<Fact>]
let ``the administrator domain references only the engine and pure Echelon contracts`` () =
    let allowed = runtime + set [ "Echelon.Signal.Engine"; "Arca.Core"; "Limen.Routing" ]
    Assert.Equal<string list>([], outside allowed adminAssembly)

/// APIs that read or change the world. A match in a domain source is an
/// architecture violation, not a style issue.
let private ambientEffects =
    [ "network", @"System\.Net|HttpClient"
      "file system", @"System\.IO\.(File|Directory)\b|\bFile\.(Read|Write|Open|Exists)"
      "clock", @"DateTime(Offset)?\.(Now|UtcNow|Today)|Stopwatch"
      "entropy", @"Guid\.NewGuid|RandomNumberGenerator|\bRandom\s*\("
      "console", @"\bConsole\."
      "JS interop", @"JSImport|JSExport|JSHost"
      "environment", @"Environment\.(GetEnvironmentVariable|CurrentDirectory|TickCount)" ]

let private sourcesOf (project: string) =
    Directory.EnumerateFiles(repoFile $"src/{project}", "*.fs") |> Seq.sort |> Seq.toList

let private stripComments (text: string) =
    Regex.Replace(text, @"//.*$|\(\*[\s\S]*?\*\)", "", RegexOptions.Multiline)

let private violations (project: string) =
    sourcesOf project
    |> List.collect (fun file ->
        let code = stripComments (File.ReadAllText file)
        ambientEffects
        |> List.filter (fun (_, pattern) -> Regex.IsMatch(code, pattern))
        |> List.map (fun (effect, _) -> $"{Path.GetFileName file}: {effect}"))

[<Theory>]
[<InlineData("Echelon.Signal.Engine")>]
[<InlineData("Echelon.Signal.Admin")>]
let ``a domain project reads no ambient effect`` (project: string) =
    Assert.NotEmpty(sourcesOf project)
    Assert.Equal<string list>([], violations project)

[<Fact>]
let ``the ambient-effect scan finds a planted clock read`` () =
    // The scan must not pass vacuously: the same rules flag a violation.
    let planted = stripComments "let now () = System.DateTime.UtcNow // a comment"
    Assert.Contains("clock", ambientEffects |> List.filter (fun (_, p) -> Regex.IsMatch(planted, p)) |> List.map fst)

[<Fact>]
let ``every WASM export forwards to Application.Runtime and decides nothing`` () =
    let shim = readRepoFile "src/Echelon.Signal.Browser/Program.cs"
    let exports = Regex.Matches(shim, @"\[JSExport\]\s*internal static string (\w+)\(string (\w+)\)\s*=>\s*([^;]+);")
    Assert.Equal(Regex.Matches(shim, @"(?m)^\s*\[JSExport\]").Count, exports.Count)
    Assert.NotEqual(0, exports.Count)
    exports
    |> Seq.iter (fun m ->
        let body = Regex.Replace(m.Groups[3].Value, @"\s+", "")
        Assert.Matches(@"^Echelon\.Signal\.Application\.Runtime\.dispatch\w*\(" + m.Groups[2].Value + @"\)$", body))
    Assert.Equal(1, Directory.EnumerateFiles(repoFile "src/Echelon.Signal.Browser", "*.cs") |> Seq.length)

/// SEC-01 (ADM-025, ADM-046): every record Signal stores carries the hash of
/// its body, and every record Signal reads is checked against it. Both happen
/// in `RecordIntegrity` alone, so no other source encodes, decodes or
/// validates an Arca record directly and skips the check.
let private rawRecordAccess = @"\bRecord\.(encode|decode)\b|\bIntegrity\.validate\b"

let private rawRecordViolations () =
    [ "Echelon.Signal.Admin"; "Echelon.Signal.Application" ]
    |> List.collect sourcesOf
    |> List.filter (fun file -> Path.GetFileName file <> "RecordIntegrity.fs")
    |> List.collect (fun file ->
        Regex.Matches(stripComments (File.ReadAllText file), rawRecordAccess)
        |> Seq.map (fun m -> $"{Path.GetFileName file}: {m.Value}")
        |> List.ofSeq)

[<Fact>]
let ``records are stored and read only through RecordIntegrity, which seals and verifies their bodies (SEC-01)`` () =
    Assert.Equal<string list>([], rawRecordViolations ())

[<Fact>]
let ``the raw record scan finds a planted decode`` () =
    // The scan must not pass vacuously, nor flag a domain module's own encoder.
    Assert.Matches(rawRecordAccess, stripComments "let r = Arca.Record.decode Record.DefaultMaxBytes text")
    Assert.DoesNotMatch(rawRecordAccess, "GovernanceRecord.encodeAudit id key r |> Result.map ignore")
