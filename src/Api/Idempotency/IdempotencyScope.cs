namespace Idempotency;

/// <summary>
/// Composes the identifier an idempotent request is stored under.
/// </summary>
public static class IdempotencyScope
{
    /// <summary>
    /// Scopes a caller-supplied idempotency key to the principal and route it was sent against.
    /// </summary>
    /// <remarks>
    /// <paramref name="principalId" /> must come from the authenticated principal's claims, never a route value —
    /// it is the only segment that keeps two tenants sending the same supplied key apart. A caller who names
    /// another tenant's identifier in <paramref name="routeKey" /> only reserves a key in their own namespace; the
    /// handler still 404s against the real resource.
    ///
    /// Each segment carries its own length rather than a delimiter, because <paramref name="suppliedKey" /> is
    /// caller-controlled: any character a delimiter could use is one a caller can type to compose a key that reads
    /// as another principal's.
    /// </remarks>
    /// <param name="principalId">The authenticated principal the request was authorized against.</param>
    /// <param name="routeKey">The route pattern and its values, identifying which endpoint and resource the key applies to.</param>
    /// <param name="suppliedKey">The caller's <c>Idempotency-Key</c> header value.</param>
    public static string Key(string principalId, string routeKey, string suppliedKey) =>
        string.Concat(Segment(principalId), Segment(routeKey), Segment(suppliedKey));

    private static string Segment(string value) => $"{value.Length}:{value}";
}
