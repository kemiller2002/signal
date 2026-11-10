/// Asynchronous clients inside the Limen request/reply loop (the pattern
/// Chrona established).
///
/// Fides' sign-in client and Arca's storage provider are asynchronous: they
/// ask their host for browser services and continue with the answers. Here
/// every such service is a kernel request. A call parks its continuation
/// under a fresh correlation id; the request goes out with the engine's next
/// reply (`Drain`); when the kernel answers, `Answer` resumes the
/// continuation, which runs until the client needs the browser again or
/// finishes. A finished operation yields engine messages. The browser
/// runtime has one thread, so all of this runs synchronously inside one
/// step; nothing here touches the browser itself.
module Echelon.Signal.Application.Bridge

open System.Collections.Generic

/// What a client asked of the browser.
type KernelCall =
    /// An Http request; its body is read as text and the named response headers returned.
    | Http of method: string * url: string * headers: (string * string) list * body: string option * timeoutMs: int * responseHeaders: string list
    | DeviceGet of key: string
    | DeviceSet of key: string * value: string
    | DeviceRemove of key: string
    | TabGet of key: string
    | TabSet of key: string * value: string
    | TabRemove of key: string
    /// Leave the page for the identity provider's sign-in page.
    | Leave of url: string
    | ReplaceAddress of url: string
    | Announce of message: string
    /// Wait this long (a back-off).
    | Sleep of milliseconds: int

/// What came back for an Http request.
type HttpResult =
    | HttpSucceeded of status: int * headers: (string * string) list * body: string
    | HttpFailed of reason: string
    | HttpCancelled
    /// The request may or may not have reached the server.
    | HttpUnknown of reason: string

/// What the kernel answered.
type KernelAnswer =
    | Answered of HttpResult
    | Read of value: string option
    | Done
    /// The browser refused a storage request.
    | Refused of reason: string

/// Whose work ended in an exception: the engine operation the page is
/// running (its pending state settles as unknown), or background work such
/// as a sign-out or a cross-tab receive, which must never settle the running
/// operation (SIG-VIS-PENDING-002).
[<RequireQualifiedAccess>]
type Work =
    | Operation
    | Background

/// Work that ended in an exception, tagged with whose work it was.
[<NoComparison; NoEquality>]
type WorkFailure = { Work: Work; Error: exn }

/// One page's in-flight work, producing messages of type 'msg.
[<Sealed>]
type Bridge<'msg>() =
    let waiting = Dictionary<string, KernelAnswer -> unit>()
    let outbox = List<string * KernelCall>()
    let finished = List<'msg>()
    let failures = List<WorkFailure>()
    let mutable sequence = 0

    let run (tag: Work) (work: Async<'msg list>) =
        Async.StartImmediate(
            async {
                match! Async.Catch work with
                | Choice1Of2 messages -> finished.AddRange messages
                | Choice2Of2 error -> failures.Add { Work = tag; Error = error }
            }
        )

    /// Asks the browser for something and continues with its answer.
    member _.Call(request: KernelCall) : Async<KernelAnswer> =
        Async.FromContinuations(fun (resume, _, _) ->
            sequence <- sequence + 1
            let id = $"bridge-{sequence}"
            waiting[id] <- resume
            outbox.Add(id, request))

    /// Runs background work to its first browser call (or its end). Its
    /// failure is reported, but never as the running operation's.
    member _.Start(work: Async<'msg list>) = run Work.Background work

    /// Runs the engine operation's own work; only its failure settles the
    /// running operation.
    member _.StartOperation(work: Async<'msg list>) = run Work.Operation work

    /// Resumes the operation waiting on this correlation id; false when none is.
    member _.Answer (id: string) (answer: KernelAnswer) =
        match waiting.TryGetValue id with
        | true, resume ->
            waiting.Remove id |> ignore
            resume answer
            true
        | _ -> false

    /// The browser calls made, the messages produced and the operations that
    /// ended in an exception since the last drain. All three are returned
    /// together: one failed operation never discards another's calls or
    /// messages (SIG-VIS-PENDING-002).
    member _.Drain() : (string * KernelCall) list * 'msg list * WorkFailure list =
        let calls = List.ofSeq outbox
        let messages = List.ofSeq finished
        let failed = List.ofSeq failures
        outbox.Clear()
        finished.Clear()
        failures.Clear()
        calls, messages, failed
