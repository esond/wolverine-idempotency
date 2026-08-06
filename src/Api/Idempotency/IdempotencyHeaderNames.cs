namespace Idempotency;

public static class IdempotencyHeaderNames
{
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>
    /// Marks a response as the stored replay of an earlier request rather than the result of new work.
    /// </summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";
}
