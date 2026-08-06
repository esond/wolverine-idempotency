namespace Idempotency;

/// <summary>
/// One caller-supplied idempotency key: the request holding it, and the response replayed for repeats of it.
/// </summary>
public record IdempotencyRecord
{
    /// <summary>
    /// The supplied key, scoped to the resource it was sent against.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Identifies the request that holds this key.
    /// </summary>
    /// <remarks>
    /// A fresh value is minted at each reservation and each takeover, and every write after a reservation is
    /// conditioned on it. That is what stops a request which stalled past
    /// <see cref="IdempotencyOptions.ReservationTimeout" /> from completing or releasing the record of the request
    /// that took over from it.
    /// </remarks>
    public required Guid OwnerToken { get; init; }

    /// <summary>
    /// The fingerprint of the request body this key was reserved for.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// When this record stops being honoured, after which any request may take the key over.
    /// </summary>
    /// <remarks>
    /// A reservation carries the reservation timeout and a completed record carries the retention window, so the
    /// same field bounds how long a stall is tolerated and how long a response is replayed. Expiry is read from this
    /// value rather than enforced by deletion, which leaves the purge as housekeeping instead of correctness.
    /// </remarks>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// When the request holding this key stored its response, or null while that request is still in flight.
    /// </summary>
    /// <remarks>
    /// Completion is marked here rather than inferred from <see cref="Response" />, because a successful response
    /// may legitimately carry no body. Inferred from the body, a stored 204 would read as in flight, and a later
    /// request would take the key over and repeat work that already succeeded.
    /// </remarks>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// The body the original request's response carried, or null when it carried none.
    /// </summary>
    public string? Response { get; init; }

    /// <summary>
    /// The status code the original request's response carried.
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// The content type the original request's response carried.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The <c>Location</c> header the original request's response carried.
    /// </summary>
    public string? Location { get; init; }
}
