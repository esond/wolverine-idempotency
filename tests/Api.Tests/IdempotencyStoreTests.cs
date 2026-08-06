using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Idempotency.Tests;

/// <summary>
/// The reservation machinery, sequentially. Every case a concurrent duplicate produces is reachable by driving the
/// store directly, so none of these needs threads.
/// </summary>
public class IdempotencyStoreTests(TestHost fixture) : IntegrationTestBase(fixture)
{
    private const string Fingerprint = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string OtherFingerprint = "0000000000000000000000000000000000000000000000000000000000000000";

    private IdempotencyStore Keys => Host.Services.GetRequiredService<IdempotencyStore>();

    [Test]
    public async Task TryReserve_claims_an_unheld_key()
    {
        var outcome = await Reserve(NewId());

        await Assert.That(outcome).IsTypeOf<IdempotencyOutcome.Reserved>();
    }

    [Test]
    public async Task TryReserve_refuses_a_key_whose_first_request_is_still_in_flight()
    {
        var id = NewId();

        await Reserve(id);

        var outcome = await Reserve(id);

        using (Assert.Multiple())
        {
            await Assert.That(outcome).IsTypeOf<IdempotencyOutcome.Rejected>();
            await Assert.That(((IdempotencyOutcome.Rejected)outcome).Problem.Status)
                .IsEqualTo(StatusCodes.Status409Conflict);
        }
    }

    [Test]
    public async Task TryReserve_refuses_a_key_reused_for_a_different_body()
    {
        var id = NewId();

        await Reserve(id);

        var outcome = await Reserve(id, OtherFingerprint);

        using (Assert.Multiple())
        {
            await Assert.That(outcome).IsTypeOf<IdempotencyOutcome.Rejected>();
            await Assert.That(((IdempotencyOutcome.Rejected)outcome).Problem.Status)
                .IsEqualTo(StatusCodes.Status422UnprocessableEntity);
        }
    }

    [Test]
    public async Task TryReserve_replays_a_completed_record()
    {
        var id = NewId();
        var reservation = await Reserved(id);

        await Complete(reservation, TypedResults.Ok(new { size = 7 }));

        var outcome = await Reserve(id);

        using (Assert.Multiple())
        {
            await Assert.That(outcome).IsTypeOf<IdempotencyOutcome.Replayed>();
            await Assert.That(((IdempotencyOutcome.Replayed)outcome).Record.Response).IsEqualTo("""{"size":7}""");
        }
    }

    [Test]
    public async Task TryReserve_reports_the_mismatch_ahead_of_the_conflict()
    {
        // A mismatched body is a caller bug whatever the timing. Reported as a conflict, the caller retries forever.
        var id = NewId();

        await Reserve(id);

        var outcome = await Reserve(id, OtherFingerprint);

        await Assert.That(((IdempotencyOutcome.Rejected)outcome).Problem.Status)
            .IsEqualTo(StatusCodes.Status422UnprocessableEntity);
    }

    [Test]
    public async Task TryReserve_takes_over_a_reservation_that_outlived_its_hold()
    {
        var id = NewId();
        var stalled = await Reserved(id);

        await Expire(id);

        var outcome = await Reserve(id);

        using (Assert.Multiple())
        {
            await Assert.That(outcome).IsTypeOf<IdempotencyOutcome.Reserved>();
            await Assert.That(((IdempotencyOutcome.Reserved)outcome).Record.OwnerToken)
                .IsNotEqualTo(stalled.OwnerToken);
        }
    }

    [Test]
    public async Task Complete_from_a_reservation_that_was_taken_over_fails_the_whole_transaction()
    {
        // The single most load-bearing line in the store. Deleting by id alone would let the stalled request clear
        // its successor's record and insert its own. Conditioned on the ownership token the delete matches nothing,
        // the insert behind it collides, and the stalled request's work rolls back with it.
        var id = NewId();
        var stalled = await Reserved(id);

        await Expire(id);
        await Reserved(id);

        await using var session = Store.LightweightSession();

        Keys.Complete(session, stalled, TypedResults.Ok(new { size = 7 }));

        await Assert.That(async () => await session.SaveChangesAsync()).ThrowsException();
    }

    [Test]
    public async Task Release_from_a_reservation_that_was_taken_over_leaves_the_successor_holding_the_key()
    {
        var id = NewId();
        var stalled = await Reserved(id);

        await Expire(id);
        var successor = await Reserved(id);

        await Keys.Release(stalled, CancellationToken.None);

        await Assert.That((await Load(id))?.OwnerToken).IsEqualTo(successor.OwnerToken);
    }

    [Test]
    public async Task Release_frees_a_key_the_caller_still_holds()
    {
        var id = NewId();

        await Keys.Release(await Reserved(id), CancellationToken.None);

        await Assert.That(await Load(id)).IsNull();
    }

    [Test]
    public async Task Complete_declines_a_response_that_is_not_a_success()
    {
        var id = NewId();
        var reservation = await Reserved(id);

        await using var session = Store.LightweightSession();

        var stored = Keys.Complete(session, reservation, TypedResults.NotFound());

        using (Assert.Multiple())
        {
            await Assert.That(stored).IsFalse();
            await Assert.That((await Load(id))!.CompletedAt).IsNull();
        }
    }

    [Test]
    public async Task CompleteWithoutBody_marks_the_record_complete_with_no_response_to_replay()
    {
        var id = NewId();
        var reservation = await Reserved(id);

        await using (var session = Store.LightweightSession())
        {
            Keys.CompleteWithoutBody(session, reservation);

            await session.SaveChangesAsync();
        }

        var record = await Load(id);

        using (Assert.Multiple())
        {
            await Assert.That(record!.CompletedAt).IsNotNull();
            await Assert.That(record.Response).IsNull();
        }
    }

    private Task<IdempotencyOutcome> Reserve(string id, string fingerprint = Fingerprint) =>
        Keys.TryReserve(id, fingerprint, CancellationToken.None);

    private async Task<IdempotencyRecord> Reserved(string id, string fingerprint = Fingerprint) =>
        ((IdempotencyOutcome.Reserved)await Reserve(id, fingerprint)).Record;

    private async Task Complete(IdempotencyRecord reservation, object response)
    {
        await using var session = Store.LightweightSession();

        Keys.Complete(session, reservation, response);

        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Backdates a record's expiry, standing in for a request that stalled past its hold.
    /// </summary>
    private async Task Expire(string id)
    {
        await using var session = Store.LightweightSession();

        var record = (await session.LoadAsync<IdempotencyRecord>(id))!;

        session.Store(record with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });

        await session.SaveChangesAsync();
    }

    private async Task<IdempotencyRecord?> Load(string id)
    {
        await using var session = Store.QuerySession();

        return await session.LoadAsync<IdempotencyRecord>(id);
    }

    private static string NewId() => IdempotencyScope.Key("tenant-a", "/widgets||", Guid.CreateVersion7().ToString());
}
