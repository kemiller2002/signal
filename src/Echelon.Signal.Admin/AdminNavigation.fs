/// Where the administrator page is (SIG-LINK-001..007): the view the URL
/// names, the router's state and the page's own address, and the moves
/// between places. Each move gives the next place and at most one Navigation
/// effect: adopt answers a reported location with a replace or nothing,
/// navigate pushes, refine replaces, and resume replaces after sign-in.
///
/// Pure: locations arrive as text and leave as effects for the edge.
module Echelon.Signal.Admin.AdminNavigation

open Limen.Routing
open Echelon.Signal.Admin.Routes

/// The routed state of the page.
[<NoComparison>]
type Place =
    { /// The view the location names, or why it names none.
      View: Result<AdminRoute, RouteError>
      Router: RouterState
      /// The page's own address, for share links: origin, path and query.
      Page: PageLocation }

let initial =
    { View = Ok Overview
      Router = Navigation.initial
      Page = { Origin = ""; Path = "/"; Query = ""; Hash = "" } }

/// The Navigation effect as the page requests it: a relative `#/…` URL.
type Move =
    | Push of url: string
    | Replace of url: string

let private relative (effect: NavigationEffect option) =
    effect
    |> Option.map (function
        | NavigationEffect.Push location -> Push(Location.href mode location)
        | NavigationEffect.Replace location -> Replace(Location.href mode location))

/// The routed location the place is at.
let current (place: Place) =
    place.Router.Current |> Option.defaultValue (Location.ofBrowser mode place.Page)

/// Adopts a location the browser reported (a deep link, Back, Forward or a
/// link the person followed): never a push (SIG-LINK-004).
let adopt (access: Access) (place: Place) (location: string) =
    let router, view, effect = RouteCodec.adopt codec (guard access) place.Router location
    { place with View = view; Router = router }, relative effect

/// Adopts the page's whole address, as Initialize or LocationChanged report it.
let arrive (access: Access) (place: Place) (page: PageLocation) =
    adopt access { place with Page = page } (Location.ofBrowser mode page)

/// Adopts the current location again because access changed: a signed-out
/// person is sent to sign-in with the view as the return target.
let reconsider (access: Access) (place: Place) = adopt access place (current place)

let private move operation (place: Place) (route: AdminRoute) =
    match operation codec place.Router route with
    | Ok(router, effect) -> { place with View = Ok route; Router = router }, relative effect
    | Error _ -> place, None

/// Goes to another place: a push (SIG-LINK-004).
let navigate place route = move RouteCodec.navigate place route

/// Refines the current view (a filter, sort, display or section): a replace.
let refine place route = move RouteCodec.refine place route

/// After sign-in: the target when it is still a view the person may see,
/// otherwise home, with a replace so Back does not return to sign-in
/// (SIG-LINK-006).
let resume (place: Place) (target: string option) =
    let location = ReturnTo.resume table (guard Open) target
    let router, effect = Navigation.replace place.Router location
    let view = RouteCodec.parse codec (guard Open) location
    { place with View = view; Router = router }, relative effect

/// The canonical return target of the sign-in view, if it names one.
let returnTarget (place: Place) =
    match place.View with
    | Ok(SignIn target) -> target |> Option.bind (ReturnTo.capture table)
    | _ -> None

/// The absolute URL of the current view, for "Copy link" (SIG-LINK-005):
/// the canonical location in the page's document, with no return target.
let shareLink (place: Place) =
    match place.View with
    | Ok(SignIn _) -> Link.share mode place.Page (location Overview)
    | Ok route -> Link.share mode place.Page (location route)
    | Error _ -> Link.share mode place.Page (current place)

/// The page's query once the identity provider's callback parameters are
/// consumed: Fides removes them from the address (LCP-109), so a share link
/// must not carry them.
let withoutCallback (place: Place) =
    { place with Page = { place.Page with Query = "" } }
