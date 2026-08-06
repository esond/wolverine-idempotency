using System.ComponentModel.DataAnnotations;

namespace Idempotency;

public record IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>
    /// How long a completed request's response is replayed for before its idempotency key is free to be reused.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an in-flight request holds its idempotency key before a later request may take it over.
    /// </summary>
    /// <remarks>
    /// This must exceed the worst-case duration of every endpoint that reserves a key. Set below it, a slow but
    /// healthy request loses its reservation mid-flight, and its own completion then fails and rolls back the work
    /// it was about to commit.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan ReservationTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
