using System.Security.Claims;
using System.Security.Cryptography;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Wolverine.Http;

namespace Idempotency;

/// <summary>
/// Reserves a caller-supplied <c>Idempotency-Key</c> before the endpoint runs, replaying or refusing a repeat
/// instead of invoking it, and frees the key again for any request that does not complete.
/// </summary>
/// <remarks>
/// Wolverine builds one of these per request and calls every hook on that same instance, so the reservation travels
/// from the reserving hook to the completing one on a field.
///
/// A subclass supplies the completion, because the response it stores is the chain's own resource type and there is
/// no way to name "whatever this endpoint returns" in a single signature.
///
/// The fingerprint reads the raw request body, so this has to run before anything deserializes it. Middleware is
/// inserted at the head of a chain, which makes that the last registration rather than the first.
/// </remarks>
public abstract class IdempotencyMiddleware(IdempotencyStore store, ILogger<IdempotencyMiddleware> logger)
{
    private const string MultipartPrefix = "multipart/";

    // Stands in for a body hash where hashing the bytes would be meaningless. Distinct from any hex digest, so it
    // can never collide with a real fingerprint.
    private const string UnfingerprintedBody = "multipart";

    private IdempotencyRecord? _reservation;

    private bool _completed;

    private bool _released;

    public async Task<IResult> Before(HttpContext httpContext)
    {
        var suppliedKey = httpContext.Request.Headers[IdempotencyHeaderNames.IdempotencyKey].ToString();

        if (string.IsNullOrWhiteSpace(suppliedKey))
        {
            return httpContext.GetEndpoint()?.Metadata.GetMetadata<IdempotencyRequiredAttribute>() is not null
                ? TypedResults.Problem(
                    Problems.BadRequest(
                        $"This endpoint requires an {IdempotencyHeaderNames.IdempotencyKey} header."))
                : WolverineContinue.Result();
        }

        var headerProblem = IdempotencyKeyHeader.Validate(suppliedKey);

        if (!ReferenceEquals(headerProblem, WolverineContinue.NoProblems))
            return TypedResults.Problem(headerProblem);

        // Without a principal there is no tenant segment to scope the key under, and every anonymous caller would
        // share one namespace — one caller's key would replay another's response. Refusing beats ignoring: a caller
        // who sent a key believes they are protected against duplicates.
        if (PrincipalId(httpContext) is not { } principalId)
        {
            return TypedResults.Problem(
                Problems.BadRequest(
                    $"The {IdempotencyHeaderNames.IdempotencyKey} header requires an authenticated caller."));
        }

        var fingerprint = await Fingerprint(httpContext.Request, httpContext.RequestAborted);
        var scopeKey = IdempotencyScope.Key(principalId, RouteKey(httpContext), suppliedKey);
        var outcome = await store.TryReserve(scopeKey, fingerprint, httpContext.RequestAborted);

        if (outcome is not IdempotencyOutcome.Reserved reserved)
            return outcome.ToResult();

        _reservation = reserved.Record;

        // Freeing the key as the response starts, rather than only once the chain unwinds, is what lets a caller fix
        // a rejected request and retry it on the same key. The unwind runs after the response has been written, so a
        // client that already holds the refusal can beat the release to the retry and be told the key is still in
        // flight — or, once the corrected body changes the fingerprint, that the key belongs to a different request,
        // which no amount of retrying resolves. Removing this and leaving Finally to carry the release on its own
        // fails A_request_that_fails_validation_frees_its_key_for_a_corrected_retry.
        httpContext.Response.OnStarting(Release);

        return WolverineContinue.Result();
    }

    /// <summary>
    /// Enrolls <paramref name="response" /> against this request's reservation, in the session that commits the work
    /// the response describes.
    /// </summary>
    /// <remarks>
    /// The reservation counts as completed once the record is queued, not once it is confirmed committed — this runs
    /// ahead of the frame that saves. A save that then fails for a reason unrelated to this key rolls the record back
    /// and leaves the reservation in place rather than released. That reservation is bounded the same way an outright
    /// process crash mid-request is: it self-heals once <see cref="IdempotencyOptions.ReservationTimeout" /> elapses.
    /// </remarks>
    protected void Complete(IDocumentSession session, object? response, bool storeBody = true)
    {
        if (_reservation is not { } reservation)
            return;

        _completed = store.Complete(session, reservation, response, storeBody);
    }

    /// <summary>
    /// Records that the work succeeded, for a chain that returns no response object to store.
    /// </summary>
    protected void CompleteWithoutBody(IDocumentSession session)
    {
        if (_reservation is not { } reservation)
            return;

        store.CompleteWithoutBody(session, reservation);
        _completed = true;
    }

    /// <summary>
    /// Frees the key of a request that left the chain without the release above ever running.
    /// </summary>
    /// <remarks>
    /// The generated chain calls this from a <c>finally</c> wrapping everything after <see cref="Before" />, which is
    /// the one place that sees a request the host never answered — a client that disconnected mid-flight, a
    /// cancellation that unwound the chain before anything was written. <c>OnStarting</c> alone leaves that request's
    /// key held until its reservation expires. This is a backstop, not the release: for every request that does
    /// answer, <c>OnStarting</c> has already run and this finds nothing to do.
    ///
    /// Wolverine could not generate this pairing at all before 6.25.5. A <c>Finally</c> alongside a <c>Before</c>
    /// returning <see cref="IResult" /> failed code generation with a <see cref="NullReferenceException" /> in
    /// <c>MaybeEndWithResultFrame</c>, which is what left <c>OnStarting</c> carrying the release by itself —
    /// JasperFx/wolverine#3892, fixed by JasperFx/wolverine#3895.
    /// </remarks>
    public Task Finally() => Release();

    // Throwing out of an OnStarting callback aborts a response the pipeline had already decided, so a database fault
    // here would turn a plain validation refusal into a failed request. A key this leaves held still expires on its
    // own — and leaving _released unset gives the Finally backstop one more attempt at it.
    private async Task Release()
    {
        if (_completed || _released || _reservation is not { } reservation)
            return;

        try
        {
            // Not the request's own cancellation token: an abandoned request is exactly the one whose key must come
            // back, and a canceled release would hold it until the reservation times out instead.
            await store.Release(reservation, CancellationToken.None);

            _released = true;
        }
        catch (Exception exception)
        {
            logger.CouldNotRelease(exception, reservation.Id);
        }
    }

    private static string? PrincipalId(HttpContext httpContext) =>
        httpContext.User.Identity?.IsAuthenticated is true
            ? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;

    private static async Task<string> Fingerprint(HttpRequest request, CancellationToken cancellationToken)
    {
        // A multipart body carries a randomly generated boundary, so the same logical upload sent twice is different
        // bytes. Hashing it would refuse the retry as a different request, which is the one case this endpoint most
        // needs to survive. The key alone identifies the request there, so a caller who reuses one for a genuinely
        // different file is replayed the first upload's response rather than refused for the mismatch.
        if (request.HasFormContentType &&
            request.ContentType?.StartsWith(MultipartPrefix, StringComparison.OrdinalIgnoreCase) is true)
            return UnfingerprintedBody;

        request.EnableBuffering();

        // A body that some earlier frame both buffered and read is seekable already, so EnableBuffering leaves it
        // wrapped as it found it — sitting at its end. Hashing from there digests nothing while the position below
        // still reports the whole payload, which is the one drained body the guard cannot recognise.
        request.Body.Position = 0;

        var hash = await SHA256.HashDataAsync(request.Body, cancellationToken);
        var hashedBytes = request.Body.Position;

        request.Body.Position = 0;

        // Reading nothing from a rewound request that declared a body means something upstream drained the stream
        // past rewinding, which leaves every request with the digest of zero bytes and every fingerprint comparison
        // passing. The frame order that prevents it is set by registration order and nothing checks it, so this does.
        if (hashedBytes == 0 && request.ContentLength > 0)
            throw new InvalidOperationException(
                $"The request body for {request.Path} was already read before its idempotency fingerprint was " +
                "taken. Register the idempotency policy after every other Wolverine HTTP middleware, so its frame " +
                "runs ahead of the body deserialization.");

        return Convert.ToHexStringLower(hash);
    }

    // The query string is part of what a request asks for, and several endpoints authorize on it. Left out, a key
    // reserved with one value replays its response to a request carrying another, and the authorization check that
    // would have refused the second one never runs.
    private static string RouteKey(HttpContext httpContext)
    {
        var pattern = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
            ?? httpContext.Request.Path.Value
            ?? "";

        var values = string.Join('&', httpContext.Request.RouteValues
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => $"{value.Key}={value.Value}"));

        return $"{pattern}|{values}|{httpContext.Request.QueryString.Value}";
    }
}

/// <summary>
/// Stores the response of a chain returning <typeparamref name="TResponse" />.
/// </summary>
public sealed class IdempotencyResponseMiddleware<TResponse>(
    IdempotencyStore store,
    ILogger<IdempotencyMiddleware> logger) : IdempotencyMiddleware(store, logger)
{
    public void After(TResponse response, IDocumentSession session) => Complete(session, response);
}

/// <summary>
/// Stores everything but the body of a response that must not be kept.
/// </summary>
public sealed class IdempotencyBodylessResponseMiddleware<TResponse>(
    IdempotencyStore store,
    ILogger<IdempotencyMiddleware> logger) : IdempotencyMiddleware(store, logger)
{
    public void After(TResponse response, IDocumentSession session) => Complete(session, response, storeBody: false);
}

/// <summary>
/// Stores the success of a chain that returns nothing to store.
/// </summary>
public sealed class IdempotencyStatusMiddleware(IdempotencyStore store, ILogger<IdempotencyMiddleware> logger)
    : IdempotencyMiddleware(store, logger)
{
    public void After(IDocumentSession session) => CompleteWithoutBody(session);
}

internal static partial class IdempotencyMiddlewareLogging
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release idempotency key {IdempotencyKey}. It stays " +
        "held until its reservation expires.")]
    public static partial void CouldNotRelease(this ILogger logger, Exception exception, string idempotencyKey);
}
