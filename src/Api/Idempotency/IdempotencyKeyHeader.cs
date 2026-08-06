using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Idempotency;

/// <summary>
/// Checks the shape of a caller-supplied <c>Idempotency-Key</c> header.
/// </summary>
public static class IdempotencyKeyHeader
{
    /// <summary>
    /// The longest key accepted, in characters.
    /// </summary>
    public const int MaxLength = 255;

    /// <summary>
    /// Returns the problem to reject a missing or malformed key with, or
    /// <see cref="WolverineContinue.NoProblems" /> when the key is usable.
    /// </summary>
    public static ProblemDetails Validate(string? suppliedKey)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey))
            return Problems.BadRequest($"The {IdempotencyHeaderNames.IdempotencyKey} header is required.");

        if (suppliedKey.Length > MaxLength)
        {
            return Problems.BadRequest(
                $"The {IdempotencyHeaderNames.IdempotencyKey} header must be {MaxLength} characters or fewer.");
        }

        if (!suppliedKey.All(IsPrintableAscii))
        {
            return Problems.BadRequest(
                $"The {IdempotencyHeaderNames.IdempotencyKey} header must contain printable ASCII characters " +
                "only. UUIDs are recommended.");
        }

        return WolverineContinue.NoProblems;
    }

    private static bool IsPrintableAscii(char character) => character is >= ' ' and <= '~';
}
