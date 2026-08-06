using System.Diagnostics.Metrics;

namespace Idempotency;

/// <summary>
/// Counts how attempts to reserve an idempotency key resolve.
/// </summary>
/// <remarks>
/// A rising <see cref="Takeover" /> count means requests are outliving
/// <see cref="IdempotencyOptions.ReservationTimeout" />, which is the signal that the timeout sits below the real
/// worst case of an endpoint reserving against this store. Untracked, that surfaces only as conflicts with no
/// narrative behind them.
/// </remarks>
public sealed class IdempotencyMetrics
{
    public const string MeterName = "Idempotency";

    public const string Reserved = "reserved";

    // A reservation claimed from a request that outlived its own, rather than from an unused key.
    public const string Takeover = "takeover";

    // A key reused after its prior completed record's retention window elapsed, rather than a stalled reservation.
    public const string Reused = "reused";

    public const string Replayed = "replayed";

    public const string InFlight = "in-flight";

    public const string Mismatch = "mismatch";

    // The key changed hands often enough that an attempt gave up rather than settling either way.
    public const string Contended = "contended";

    private readonly Counter<long> _reservations;

    public IdempotencyMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _reservations = meter.CreateCounter<long>("idempotency.reservations");
    }

    public void Record(string outcome) => _reservations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
