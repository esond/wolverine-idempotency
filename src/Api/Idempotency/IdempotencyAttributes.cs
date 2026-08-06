namespace Idempotency;

/// <summary>
/// Marks an endpoint that requires an idempotency key header to be supplied.
/// </summary>
/// <remarks>
/// Use for endpoints whose work is irreversible enough that duplicates are unacceptable.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotencyRequiredAttribute : Attribute;

/// <summary>
/// Marks an endpoint whose response body must not be kept for replay.
/// </summary>
/// <remarks>
/// Use for endpoints where storage of the response to replay would not be possible (e.g.: a freshly-minted
/// secret that we store hashed and can't recover). A repeat request replays the status and <c>Location</c> with no
/// body.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotencyOmitsResponseBodyAttribute : Attribute;

/// <summary>
/// Marks an endpoint that opts out of the default idempotency behaviour entirely.
/// </summary>
/// <remarks>
/// Use for POST endpoints that for some reason cannot support default idempotency behaviours.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotencyOptOutAttribute : Attribute;
