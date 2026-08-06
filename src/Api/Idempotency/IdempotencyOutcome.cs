using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Idempotency;

/// <summary>
/// What an attempt to reserve an idempotency key decided about the request making it.
/// </summary>
public abstract record IdempotencyOutcome
{
    private IdempotencyOutcome()
    {
    }

    /// <summary>
    /// The key is held by this request, which owns the work and must go on to do it.
    /// </summary>
    public sealed record Reserved(IdempotencyRecord Record) : IdempotencyOutcome;

    /// <summary>
    /// An earlier request under this key already completed, and its response stands in for this one.
    /// </summary>
    public sealed record Replayed(IdempotencyRecord Record) : IdempotencyOutcome;

    /// <summary>
    /// The request must not proceed, and the caller gets this problem instead.
    /// </summary>
    public sealed record Rejected(ProblemDetails Problem) : IdempotencyOutcome;

    /// <summary>
    /// Converts an outcome that ends the request into the caller's response.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown for a <see cref="Reserved" /> outcome, which is the caller's own work to perform and has no response
    /// of its own.
    /// </exception>
    public IResult ToResult() => this switch
    {
        Replayed replayed => new ReplayedResult(replayed.Record),
        Rejected rejected => TypedResults.Problem(rejected.Problem),
        _ => throw new InvalidOperationException($"{GetType().Name} does not describe a response.")
    };
}
