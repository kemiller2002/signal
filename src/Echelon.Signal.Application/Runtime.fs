/// The composition root the WebAssembly shim calls into: the one place with
/// effects (the clock, fresh keys, the browser's cryptographic random
/// source and the mutable page state).
///
/// The shim cannot thread state between calls, so each page's state lives
/// here, behind one string-in/string-out function. Aegis is configured once,
/// at first use.
module Echelon.Signal.Application.Runtime

open Aegis
open Echelon.Signal.Engine

let private aegis = lazy (Boundary.configure [ Sinks.standardError ])

let mutable private state = Wire.initial

/// One kernel message for the assessment page (web/).
let dispatch (messageJson: string) =
    let next, reply = Wire.handle aegis.Value state messageJson
    state <- next
    reply

// ---- The administrator page (web/admin/) ---------------------------------------------------

/// The templates the administrator page can start groups from, until the
/// stored catalog (WI-0057) is wired into the page (WI-0073): the pilot.
let private catalog: Echelon.Signal.Admin.AdminApp.CatalogEntry list =
    [ { Hash = Canonical.templateHash Pilot.assessment
        SurveyIdentifier = Pilot.assessment.Id
        Version = Pilot.assessment.Version
        Title = Pilot.assessment.Title
        Content = Pilot.assessment } ]

let private resolve (hash: string) =
    if hash = Canonical.templateHash Pilot.assessment then Some Pilot.assessment else None

let private bridge = Bridge.Bridge<AdminWork.Outcome>()
let private now () = System.DateTimeOffset.UtcNow
let private random (count: int) = System.Security.Cryptography.RandomNumberGenerator.GetBytes count

let private adminEnv: AdminWork.Env =
    { Now = now
      NewKey = fun prefix -> $"{prefix}-{System.Guid.NewGuid():N}"
      RandomBytes = random
      Bridge = bridge
      // Arca's GitHub adapter, its requests through the kernel, its tokens from Fides.
      Backend = fun tokens unauthorized -> Store.gitHub (AdminPorts.arcaHost bridge tokens unauthorized)
      Identity = Identity.create
      Resolve = resolve
      Catalog = catalog
      ApplicationVersion = "signal-admin/1" }

let mutable private adminState = AdminWire.initial

/// One kernel message for the administrator page (web/admin/index.html).
let dispatchAdmin (messageJson: string) =
    let next, reply = AdminWire.handle aegis.Value adminEnv adminState messageJson
    adminState <- next
    reply
