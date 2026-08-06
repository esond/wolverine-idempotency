namespace Idempotency.Tests;

public class IdempotencyScopeTests
{
    [Test]
    public async Task Key_separates_two_principals_sending_the_same_supplied_key()
    {
        var first = IdempotencyScope.Key("tenant-a", "/widgets||", "key");
        var second = IdempotencyScope.Key("tenant-b", "/widgets||", "key");

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task Key_separates_two_routes_sharing_a_supplied_key()
    {
        var first = IdempotencyScope.Key("tenant-a", "/widgets||", "key");
        var second = IdempotencyScope.Key("tenant-a", "/orders||", "key");

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task Key_is_stable_for_the_same_segments()
    {
        await Assert.That(IdempotencyScope.Key("tenant-a", "/widgets||", "key"))
            .IsEqualTo(IdempotencyScope.Key("tenant-a", "/widgets||", "key"));
    }

    [Test]
    public async Task Key_cannot_be_forged_by_a_caller_shifting_a_segment_boundary()
    {
        // The supplied key is caller-controlled. Delimiter-joined, "a" + "b:c" and "a:b" + "c" would compose the
        // same identifier, and a caller could name a principal or route that is not theirs.
        var honest = IdempotencyScope.Key("tenant-a", "/widgets||", "key");
        var forged = IdempotencyScope.Key("tenant-a", "/widgets||key", "");

        await Assert.That(honest).IsNotEqualTo(forged);
    }
}
