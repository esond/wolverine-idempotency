using System.Text.Json;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Idempotency;

/// <summary>
/// Deduplicates repeated requests that carry the same caller-supplied <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// A reservation commits on its own session so concurrent duplicates can see it, but a completion enrolls in the
/// caller's session and commits with the work it guards. That is what makes a stored response and the work it
/// describes all-or-nothing: no response is ever stored for work that rolled back, and no work commits without
/// its response being stored alongside it.
/// </remarks>
public class IdempotencyStore(
    IDocumentStore store,
    IdempotencyMetrics metrics,
    IOptions<IdempotencyOptions> options,
    IOptions<JsonOptions> jsonOptions)
{
    private readonly IdempotencyOptions _options = options.Value;

    // The HTTP pipeline's own options, so a stored body is spelled exactly as the pipeline spelled the original.
    // Marten's serializer and the System.Text.Json defaults are both configured differently, and either would make
    // a "byte-identical" replay differ in every property name.
    private readonly JsonSerializerOptions _serializerOptions = jsonOptions.Value.SerializerOptions;

    /// <summary>
    /// Claims an idempotency key for the calling request, or reports what the caller gets instead.
    /// </summary>
    /// <remarks>
    /// A returned <see cref="IdempotencyOutcome.Reserved" /> carries the record forward, because
    /// <see cref="Complete" /> and <see cref="Release" /> both need the ownership token it was reserved under.
    /// </remarks>
    public async Task<IdempotencyOutcome> TryReserve(
        string id, string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();

        // One restart covers the winner of a race releasing its key before we reload. A second empty reload means
        // the key is being churned by other requests, which the 409 below correctly tells the caller to retry.
        var restartsRemaining = 1;

        while (true)
        {
            var existing = await session.LoadAsync<IdempotencyRecord>(id, cancellationToken);

            if (existing is not null && existing.ExpiresAt > DateTimeOffset.UtcNow)
                return Decide(existing, fingerprint);

            var reservation = NewReservation(id, fingerprint);

            if (existing is not null)
                DeleteOwned(session, existing);

            session.Insert(reservation);

            try
            {
                await session.SaveChangesAsync(cancellationToken);

                metrics.Record(
                    existing switch
                    {
                        null => IdempotencyMetrics.Reserved,
                        { CompletedAt: null } => IdempotencyMetrics.Takeover,
                        _ => IdempotencyMetrics.Reused
                    });

                return new IdempotencyOutcome.Reserved(reservation);
            }
            catch (DocumentAlreadyExistsException)
            {
                // Marten's insert absorbs the primary-key conflict server-side and raises this rather than a unique
                // violation, so a Postgres-level unique-violation check cannot catch a race here. The failed insert
                // also stays queued: SaveChangesAsync clears the unit of work only when it succeeds, so any later
                // save on this session would re-run the operation that just failed.
                session.EjectAllPendingChanges();
            }

            var winner = await session.LoadAsync<IdempotencyRecord>(id, cancellationToken);

            if (winner is not null && winner.ExpiresAt > DateTimeOffset.UtcNow)
                return Decide(winner, fingerprint);

            if (restartsRemaining-- != 0)
                continue;

            metrics.Record(IdempotencyMetrics.Contended);

            return new IdempotencyOutcome.Rejected(
                Problems.Conflict(
                    $"The {IdempotencyHeaderNames.IdempotencyKey} is contended by other requests. Retry shortly."));
        }
    }

    /// <summary>
    /// Enrolls the response <paramref name="response" /> describes in the caller's session, to commit with the work
    /// it describes, and reports whether there was one to store.
    /// </summary>
    /// <remarks>
    /// This does not save. The caller's own commit carries both the work and the record, or neither. A false return
    /// leaves the reservation untouched for the caller to release.
    /// </remarks>
    public bool Complete(IDocumentSession session, IdempotencyRecord reservation, object? response,
        bool storeBody = true)
    {
        if (ResponseDescriber.Describe(response, _serializerOptions) is not { } description)
            return false;

        Store(session, reservation,
            storeBody ? description : description with { Body = null, ContentType = null });

        return true;
    }

    /// <summary>
    /// Enrolls a completion that records only that the work succeeded, for a chain whose real response body is
    /// written after the transaction this completion commits in.
    /// </summary>
    /// <remarks>
    /// A repeat of such a request replays the status alone. That is deliberately not byte-identical to the original:
    /// the guarantee these chains buy is that the work does not run twice, not that the second caller sees the first
    /// caller's body.
    /// </remarks>
    public void CompleteWithoutBody(IDocumentSession session, IdempotencyRecord reservation) =>
        Store(session, reservation,
            new CompletedResponse(Body: null, StatusCodes.Status200OK, ContentType: null, Location: null));

    /// <summary>
    /// Frees a reservation whose request failed before doing any work, so the caller can fix the request and retry
    /// on the same key.
    /// </summary>
    public async Task Release(IdempotencyRecord reservation, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();

        DeleteOwned(session, reservation);

        await session.SaveChangesAsync(cancellationToken);
    }

    private void Store(IDocumentSession session, IdempotencyRecord reservation, CompletedResponse description)
    {
        DeleteOwned(session, reservation);

        session.Insert(
            reservation with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                Response = description.Body,
                StatusCode = description.StatusCode,
                ContentType = description.ContentType,
                Location = description.Location,
                ExpiresAt = DateTimeOffset.UtcNow + _options.RetentionWindow
            });
    }

    /// <summary>
    /// Queues the removal of a record only while <paramref name="owned" /> still holds it.
    /// </summary>
    /// <remarks>
    /// The token predicate is what makes a lost reservation harmless. Deleting by id alone would let a request that
    /// was taken over remove its successor's record — freeing the key for a second real dispatch on release, or
    /// clearing the way for its own stale insert on completion. Conditioned on the token, the delete matches
    /// nothing, the insert that follows collides, and the whole transaction rolls back with the work inside it.
    /// </remarks>
    private static void DeleteOwned(IDocumentOperations session, IdempotencyRecord owned)
    {
        var id = owned.Id;
        var token = owned.OwnerToken;

        session.DeleteWhere<IdempotencyRecord>(record => record.Id == id && record.OwnerToken == token);
    }

    private IdempotencyOutcome Decide(IdempotencyRecord existing, string fingerprint)
    {
        // A mismatched body outranks an in-flight original: it is a caller bug that stays a bug whatever the timing,
        // and a 409 would send the caller into a retry loop that can never succeed.
        if (existing.Fingerprint != fingerprint)
        {
            metrics.Record(IdempotencyMetrics.Mismatch);

            return new IdempotencyOutcome.Rejected(
                Problems.UnprocessableEntity(
                    $"This {IdempotencyHeaderNames.IdempotencyKey} was used for a different request. Use a new key."));
        }

        if (existing.CompletedAt is null)
        {
            metrics.Record(IdempotencyMetrics.InFlight);

            return new IdempotencyOutcome.Rejected(
                Problems.Conflict(
                    $"A request with this {IdempotencyHeaderNames.IdempotencyKey} is in flight. Retry shortly."));
        }

        metrics.Record(IdempotencyMetrics.Replayed);

        return new IdempotencyOutcome.Replayed(existing);
    }

    private IdempotencyRecord NewReservation(string id, string fingerprint)
        => new()
        {
            Id = id,
            OwnerToken = Guid.NewGuid(),
            Fingerprint = fingerprint,
            ExpiresAt = DateTimeOffset.UtcNow + _options.ReservationTimeout
        };
}
