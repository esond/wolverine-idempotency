using Marten;

namespace Idempotency;

/// <summary>
/// Removes every idempotency record whose expiry has passed.
/// </summary>
/// <remarks>
/// Housekeeping, not correctness: expiry is read when a key is looked up, so a purge that never runs costs space
/// alone. Publish this on whatever schedule the host already has.
/// </remarks>
public record PurgeExpiredIdempotencyRecords;

public static class PurgeExpiredIdempotencyRecordsHandler
{
    public static void Handle(PurgeExpiredIdempotencyRecords command, IDocumentSession session)
    {
        var now = DateTimeOffset.UtcNow;

        session.DeleteWhere<IdempotencyRecord>(record => record.ExpiresAt < now);
    }
}
