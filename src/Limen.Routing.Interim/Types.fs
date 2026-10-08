// Limen routing: the F# reference implementation of the language-neutral
// semantics in conformance/routing/README.md (kemiller2002/limen#20, LCP-005,
// and the URL-state cluster LCP-088..112).
//
// Routing is application meaning, so it lives in the engine. This module
// resolves a location the browser reported, builds the canonical location
// for a destination, and decides the one Navigation effect (push, replace or
// none) that keeps the browser's history consistent with the engine. It
// never touches the browser; the kernel performs the effect.
//
// Every function is total: malformed input is a value, never an exception.
// The API shape is F#'s own; only the semantics are shared across languages.
//
// Interim copy in Signal: see Route.fs for provenance (DF-SIGNAL-2026-0003).
namespace Limen.Routing

open System
open System.Globalization
open System.Text

[<RequireQualifiedAccess>]
type ParamType =
    | String
    | Int
    | Bool
    /// An ISO-8601 calendar date, YYYY-MM-DD.
    | Date
    /// A year and month, YYYY-MM: a period.
    | Month
    /// One of the declared values.
    | Enum of values: string list
    /// Members sorted and unique, joined by ",". An empty list allows any
    /// non-empty strings.
    | Set of values: string list

[<RequireQualifiedAccess>]
type Segment =
    | Literal of string
    | Param of name: string * ParamType
    | Wildcard of name: string

[<RequireQualifiedAccess>]
type Value =
    | Text of string
    | Integer of int64
    | Boolean of bool
    | Date of DateOnly
    | Month of year: int * month: int
    | Members of string list

type QueryParam =
    { Name: string
      Type: ParamType
      Required: bool
      /// Reported when the key is absent, and omitted from the canonical form.
      Default: Value option }

/// A value in a route template: a source parameter to copy, or a literal.
[<RequireQualifiedAccess>]
type Template =
    | FromParam of string
    | Literal of string

type Route =
    { Name: string
      Path: Segment list
      Query: QueryParam list
      Children: Route list
      Redirect: (string * (string * Template) list) option
      Guard: string option
      Requires: string list
      /// Whether a sign-in may return here (LCP-101).
      ReturnTarget: bool }

type Level = { Route: string; Params: Map<string, Value> }

type Match =
    { Route: string
      Chain: Level list
      Query: Map<string, Value>
      Requires: string list
      RedirectedFrom: string list }

[<RequireQualifiedAccess>]
type GuardDecision =
    | Allow
    | Deny
    | Redirect of route: string * parameters: Map<string, Value> * query: Map<string, Value>

[<RequireQualifiedAccess>]
type Resolution =
    | Matched of Match
    | NotFound
    | MalformedPath
    | MalformedQuery
    /// Longer than 8,192 characters: refused before decoding.
    | TooLong
    | Invalid of route: string * parameter: string * value: string * expected: string
    | RedirectLoop of chain: string list
    | Denied of route: string

[<RequireQualifiedAccess>]
type BuildError =
    | UnknownRoute
    | MissingParameter of string
    | InvalidParameter of string

[<RequireQualifiedAccess>]
type NavigationEffect =
    | Push of string
    | Replace of string

[<RequireQualifiedAccess>]
type DefinitionError =
    | InvalidSegment of route: string * segment: string
    | DuplicateName of route: string
    | DuplicateParameter of route: string * parameter: string
    | ReservedName of route: string * parameter: string
    | InvalidValues of route: string * parameter: string
    | InvalidDefault of route: string * parameter: string
    | RequiredWithDefault of route: string * parameter: string
    | UnknownTarget of route: string * target: string
    | UnknownParameter of route: string * parameter: string
    | UnknownRole of role: string * route: string

/// The routes that play a part the module knows about.
type Roles =
    { Home: string
      SignIn: string option
      NotFound: string option }

/// An old URL pattern that now lives elsewhere (LCP-105).
type LegacyRoute =
    { Path: string
      To: string
      Params: (string * Template) list }

/// A validated table: built only by RouteTable.define.
type RouteTable =
    internal
        { routes: Route list
          legacy: LegacyRoute list
          roles: Roles
          matching: Route list }
