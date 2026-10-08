// Limen.Routing (interim copy in Signal): location modes, share links and return targets
//
// Copied from kemiller2002/limen libraries/fsharp/Limen.Routing/Routing.fs at
// e935da7 (PR #101, WI-0168), the F# reference library Limen 0.9.0 ships as
// EchelonFoundry.Limen.Routing. It is split into files below Ordo's
// structural-review size, and the cross-file `private` members are
// `internal` (with ModuleSuffix where a module now sits in another file
// from its type, the name the compiler gave it); nothing else differs. When
// Limen 0.9.0 is released, delete this project and reference the package:
// the namespace, modules and signatures are the package's own
// (DF-SIGNAL-2026-0003, WI-0066).
namespace Limen.Routing

open System
open System.Globalization
open System.Text

/// Where the routed location lives in the browser's URL (LCP-102).
[<RequireQualifiedAccess>]
type LocationMode =
    /// The fragment: "#/invoices/42?tab=history". Static hosts need nothing.
    | Hash
    /// The path and query: for hosts that serve the application at every path.
    | Path

/// The location the kernel reports (Initialize.location, LocationChanged).
type PageLocation =
    { Origin: string
      Path: string
      Query: string
      Hash: string }

module Location =
    /// The routed location ("/path?query") of the page's URL.
    let ofBrowser (mode: LocationMode) (page: PageLocation) =
        match mode with
        | LocationMode.Hash ->
            let body = if page.Hash.StartsWith "#" then page.Hash.Substring 1 else page.Hash
            if body = "" then "/" elif body.StartsWith "/" then body else "/" + body
        | LocationMode.Path -> (if page.Path = "" then "/" else page.Path) + page.Query

    /// The relative URL to request with Navigation, or to render as a link's
    /// href: "#/x" in hash mode, "/x" in path mode.
    let href (mode: LocationMode) (location: string) =
        match mode with
        | LocationMode.Hash -> "#" + location
        | LocationMode.Path -> location

module Link =
    /// The absolute URL of a canonical location, for "copy link" (LCP-106).
    /// The engine writes it with the Core Clipboard effect.
    let share (mode: LocationMode) (page: PageLocation) (location: string) =
        match mode with
        | LocationMode.Hash -> page.Origin + page.Path + page.Query + "#" + location
        | LocationMode.Path -> page.Origin + location

module ReturnTo =
    /// The query parameter the sign-in route declares for the target.
    let parameter = "returnTo"

    /// A single-slash relative location with no backslash or control
    /// character: the only shape a return target may have (LCP-101).
    let isRelative (location: string) =
        location.Length > 0
        && location.Length <= Router.maxLength
        && location[0] = '/'
        && not (location.StartsWith "//")
        && not (location.Contains '\\')
        && location |> Seq.forall (fun c -> c >= ' ' && c <> '\u007f')

    let private eligible (table: RouteTable) (matched: Match) =
        let roles = RouteTable.roles table
        Some matched.Route <> roles.SignIn
        && Some matched.Route <> roles.NotFound
        && (Router.chainOf (RouteTable.routes table) matched.Route |> Option.map (fun chain -> (chain |> List.last |> snd).ReturnTarget)) = Some true

    /// The target to keep for a location that needs sign-in: its canonical
    /// form, or None when it may not be returned to. Guards are not consulted
    /// here; resume consults them after sign-in.
    let capture (table: RouteTable) (location: string) : string option =
        if not (isRelative location) then None
        else
            match Router.resolveLocation (RouteTable.routes table) Router.allowAll location with
            | Resolution.Matched matched when eligible table matched -> Router.canonical (RouteTable.routes table) matched |> Result.toOption
            | _ -> None

    /// The sign-in location carrying the target, or None without a sign-in route.
    let signIn (table: RouteTable) (target: string option) : Result<string, BuildError> =
        match (RouteTable.roles table).SignIn with
        | None -> Error BuildError.UnknownRoute
        | Some route ->
            let query = target |> Option.map (fun t -> Map [ parameter, Value.Text t ]) |> Option.defaultValue Map.empty
            Router.build (RouteTable.routes table) route Map.empty query

    let private home (table: RouteTable) =
        Router.build (RouteTable.routes table) (RouteTable.roles table).Home Map.empty Map.empty |> Result.defaultValue "/"

    /// Where to go after sign-in: the target's canonical location when it is
    /// still eligible and its guards allow it now; otherwise home. Replace,
    /// so Back does not return to the sign-in page.
    let resume (table: RouteTable) guard (target: string option) : string =
        match target |> Option.filter isRelative with
        | None -> home table
        | Some location ->
            match Router.resolveLocation (RouteTable.routes table) guard location with
            | Resolution.Matched matched when eligible table matched -> Router.canonical (RouteTable.routes table) matched |> Result.defaultValue (home table)
            | _ -> home table
