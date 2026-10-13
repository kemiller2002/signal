/// Exact template resolution (ADM-055, WI-0081): an explicit subsystem, not
/// an incidental lookup. Templates come from registries in priority order
/// (the built-in catalog, then the dataset's stored catalog), each read and
/// verified, with a verified local copy kept for when a registry cannot be
/// read. A request names an exact template (its canonical hash, or survey
/// and version) and gets one of the outcomes the requirement names; nothing
/// is ever replaced by "latest" or a merely compatible version, and "not
/// found" is scoped to the registries and revisions actually searched.
///
/// Pure.
module Echelon.Signal.Admin.TemplateResolution

open Echelon.Signal.Engine
open Echelon.Signal.Engine.Publication

/// Whether a registry could be read when it was asked.
type RegistryState =
    | Available
    | Unavailable of reason: string
    | Unauthorized of reason: string
    /// Read only in part (an incomplete listing): absence proves nothing.
    | Incomplete of reason: string

/// One source of published templates, as read.
[<NoComparison; NoEquality>]
type Registry =
    { Name: string
      /// The revision read, when the registry has one.
      Revision: string option
      State: RegistryState
      Templates: Published list
      /// Whether a template's content hashes to the hash it is recorded
      /// under: re-hashed for a stored catalog, true by construction for
      /// templates compiled into the application.
      Verify: Published -> bool }

/// What is asked for: an exact artifact.
type Request =
    | ByHash of hash: string
    | ByVersion of surveyId: string * version: string

/// What resolution found (ADM-055's outcomes).
[<NoComparison>]
type Outcome =
    | ResolvedExact of Published * registry: string
    /// Searched these registries, at these revisions, and it is not there.
    | NotFoundInRegistry of searched: (string * string option) list
    | RegistryUnavailable of registry: string * reason: string
    | RegistryUnauthorized of registry: string * reason: string
    /// A registry holds the identity, but its content does not hash to it.
    | HashMismatch of registry: string * claimed: string
    /// More than one artifact claims the same survey and version.
    | ConflictingArtifacts of claims: (string * string) list
    | UnsupportedTemplateVersion of Published * reason: string
    /// The registry could not be read, but a copy verified earlier is held.
    | CachedVerifiedCopyAvailable of Published * reason: string
    | ResolutionUnknown of reason: string

let private matches (request: Request) (t: Published) =
    match request with
    | ByHash h -> t.Hash = h
    | ByVersion(s, v) -> t.SurveyId = s && t.Version = v

let private requested (request: Request) =
    match request with
    | ByHash h -> h
    | ByVersion(s, v) -> $"{s} {v}"

/// Resolves an exact template. `cache` holds copies verified earlier;
/// `unsupported` says why a template cannot be used here, if it cannot.
let resolve (registries: Registry list) (cache: Published list) (unsupported: Published -> string option) (request: Request) : Outcome =
    let available = registries |> List.filter (fun r -> match r.State with Available -> true | _ -> false)
    let found = available |> List.collect (fun r -> r.Templates |> List.filter (matches request) |> List.map (fun t -> r.Name, t))

    // Every artifact claiming the identity of what was found, in any available
    // registry: two different hashes for one survey and version conflict.
    let identities = found |> List.map (fun (_, t) -> t.SurveyId, t.Version) |> List.distinct

    let claims =
        available
        |> List.collect (fun r -> r.Templates |> List.filter (fun t -> identities |> List.contains (t.SurveyId, t.Version)) |> List.map (fun t -> r.Name, t.Hash))
        |> List.distinct

    let cached () = cache |> List.tryFind (fun t -> matches request t && verify t)

    let verifiedIn name t =
        registries |> List.exists (fun r -> r.Name = name && r.Verify t)

    match found |> List.tryFind (fun (registry, t) -> not (verifiedIn registry t)) with
    | Some(registry, t) -> HashMismatch(registry, t.Hash)
    | None when (claims |> List.map snd |> List.distinct).Length > 1 -> ConflictingArtifacts claims
    | None ->
        match found with
        | (registry, t) :: _ ->
            match unsupported t with
            | Some reason -> UnsupportedTemplateVersion(t, reason)
            | None -> ResolvedExact(t, registry)
        | [] ->
            let blocked = registries |> List.tryFind (fun r -> match r.State with Available -> false | _ -> true)

            match blocked, cached () with
            | Some r, Some copy ->
                let reason =
                    match r.State with
                    | Unavailable why
                    | Unauthorized why
                    | Incomplete why -> why
                    | Available -> ""

                CachedVerifiedCopyAvailable(copy, $"{r.Name} could not be read ({reason})")
            | Some { Name = name; State = Unavailable why }, None -> RegistryUnavailable(name, why)
            | Some { Name = name; State = Unauthorized why }, None -> RegistryUnauthorized(name, why)
            | Some { Name = name; State = Incomplete why }, None -> ResolutionUnknown $"{name} was read only in part ({why}), so {requested request} may still be there"
            | _ -> NotFoundInRegistry(registries |> List.map (fun r -> r.Name, r.Revision))

/// The template to use, only when resolution established it exactly.
let usable (outcome: Outcome) =
    match outcome with
    | ResolvedExact(t, _)
    | CachedVerifiedCopyAvailable(t, _) -> Some t
    | _ -> None

/// What the outcome means, in one sentence for a person.
let describe (request: Request) (outcome: Outcome) =
    let what = requested request

    match outcome with
    | ResolvedExact(t, registry) -> $"{t.SurveyId} {t.Version} ({t.Hash}) was found in {registry}."
    | NotFoundInRegistry searched ->
        let scope =
            searched
            |> List.map (fun (name, revision) -> match revision with Some r -> $"{name} at {r}" | None -> name)
            |> String.concat " and "

        $"{what} was searched for in {scope} and is not there."
    | RegistryUnavailable(registry, reason) -> $"{registry} could not be read ({reason}), so whether it holds {what} is not known."
    | RegistryUnauthorized(registry, reason) -> $"{registry} refused access ({reason}), so whether it holds {what} is not known."
    | HashMismatch(registry, claimed) -> $"{registry} holds a template claiming {claimed} whose content does not hash to it; it was not used."
    | ConflictingArtifacts claims ->
        let listed = claims |> List.map (fun (registry, hash) -> $"{hash} in {registry}") |> String.concat ", "
        $"More than one template claims the same survey and version ({listed}); none was used."
    | UnsupportedTemplateVersion(t, reason) -> $"{t.SurveyId} {t.Version} was found, but it cannot be used here: {reason}."
    | CachedVerifiedCopyAvailable(t, reason) -> $"{t.SurveyId} {t.Version} is used from a copy verified earlier, because {reason}."
    | ResolutionUnknown reason -> $"Whether {what} exists is not known: {reason}."
