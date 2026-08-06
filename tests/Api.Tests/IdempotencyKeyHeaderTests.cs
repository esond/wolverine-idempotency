using Wolverine.Http;

namespace Idempotency.Tests;

public class IdempotencyKeyHeaderTests
{
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Validate_refuses_a_missing_key(string? suppliedKey)
    {
        await Assert.That(IdempotencyKeyHeader.Validate(suppliedKey).Status).IsEqualTo(400);
    }

    [Test]
    public async Task Validate_refuses_a_key_past_the_length_limit()
    {
        var suppliedKey = new string('k', IdempotencyKeyHeader.MaxLength + 1);

        await Assert.That(IdempotencyKeyHeader.Validate(suppliedKey).Status).IsEqualTo(400);
    }

    [Test]
    public async Task Validate_refuses_a_key_carrying_characters_outside_printable_ascii()
    {
        await Assert.That(IdempotencyKeyHeader.Validate("keyé").Status).IsEqualTo(400);
    }

    [Test]
    public async Task Validate_accepts_a_uuid()
    {
        await Assert.That(IdempotencyKeyHeader.Validate(Guid.CreateVersion7().ToString()))
            .IsSameReferenceAs(WolverineContinue.NoProblems);
    }
}
