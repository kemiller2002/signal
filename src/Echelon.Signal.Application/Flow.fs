/// Asynchronous steps that can fail, composed without exceptions: an
/// `Async<Result<_, _>>` computation expression for the store's sequences.
module Echelon.Signal.Application.Flow

type AsyncResult<'a, 'e> = Async<Result<'a, 'e>>

/// Builds `Async<Result<_, _>>` sequences: `let!` binds an async result,
/// stopping at the first error.
type AsyncResultBuilder() =
    member _.Return(value: 'a) : AsyncResult<'a, 'e> = async.Return(Ok value)
    member _.ReturnFrom(work: AsyncResult<'a, 'e>) = work

    member _.Bind(work: AsyncResult<'a, 'e>, next: 'a -> AsyncResult<'b, 'e>) : AsyncResult<'b, 'e> =
        async {
            match! work with
            | Ok value -> return! next value
            | Error error -> return Error error
        }

    member _.Zero() : AsyncResult<unit, 'e> = async.Return(Ok())
    member _.Delay(work: unit -> AsyncResult<'a, 'e>) = async.Delay work

let asyncResult = AsyncResultBuilder()

/// A plain result as an async result, to bind it in `asyncResult`.
let lift (result: Result<'a, 'e>) : AsyncResult<'a, 'e> = async.Return result

/// The outcome of an async call that cannot fail, as a value to inspect.
let attempt (work: Async<'a>) : AsyncResult<'a, 'e> =
    async {
        let! value = work
        return Ok value
    }

/// Maps the error of an async result.
let mapError (f: 'e -> 'f) (work: AsyncResult<'a, 'e>) : AsyncResult<'a, 'f> =
    async {
        let! result = work
        return Result.mapError f result
    }

/// Runs the steps in order, collecting every value, stopping at the first error.
let sequence (steps: AsyncResult<'a, 'e> list) : AsyncResult<'a list, 'e> =
    List.foldBack
        (fun step rest ->
            asyncResult {
                let! value = step
                let! others = rest
                return value :: others
            })
        steps
        (async.Return(Ok []))
