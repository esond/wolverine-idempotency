using System.Text;
using Microsoft.AspNetCore.Http;

namespace Idempotency.Tests;

public class ReplayedResultTests
{
    private const string StoredResponse = """{"id":"0198f2a1-0000-7000-8000-000000000000","size":42}""";

    [Test]
    public async Task ExecuteAsync_writes_the_stored_body_unchanged()
    {
        var httpContext = NewContext();

        await new ReplayedResult(Completed(StoredResponse, StatusCodes.Status202Accepted, "application/json"))
            .ExecuteAsync(httpContext);

        await Assert.That(await ReadBody(httpContext)).IsEqualTo(StoredResponse);
    }

    [Test]
    public async Task ExecuteAsync_writes_the_given_status_code_and_content_type_unchanged()
    {
        var httpContext = NewContext();

        await new ReplayedResult(Completed(StoredResponse, StatusCodes.Status201Created, "application/vnd.demo+json"))
            .ExecuteAsync(httpContext);

        using (Assert.Multiple())
        {
            await Assert.That(httpContext.Response.StatusCode).IsEqualTo(StatusCodes.Status201Created);
            await Assert.That(httpContext.Response.ContentType).IsEqualTo("application/vnd.demo+json");
        }
    }

    [Test]
    public async Task ExecuteAsync_flags_the_response_as_a_replay()
    {
        var httpContext = NewContext();

        await new ReplayedResult(Completed(StoredResponse, StatusCodes.Status202Accepted, "application/json"))
            .ExecuteAsync(httpContext);

        // Spelled Idempotent-Replayed, matching Stripe. Idempotency-Replayed is the plausible typo, and a client
        // reading for it would silently treat every replay as new work.
        await Assert.That(httpContext.Response.Headers).ContainsKey(IdempotencyHeaderNames.IdempotentReplayed);
    }

    [Test]
    public async Task ExecuteAsync_writes_neither_body_nor_content_type_for_a_stored_response_without_one()
    {
        var httpContext = NewContext();

        await new ReplayedResult(Completed(body: null, StatusCodes.Status204NoContent, contentType: null))
            .ExecuteAsync(httpContext);

        using (Assert.Multiple())
        {
            await Assert.That(httpContext.Response.StatusCode).IsEqualTo(StatusCodes.Status204NoContent);
            await Assert.That(httpContext.Response.ContentType).IsNull();
            await Assert.That(await ReadBody(httpContext)).IsEmpty();
        }
    }

    [Test]
    public async Task ExecuteAsync_writes_the_stored_location()
    {
        var httpContext = NewContext();
        var record = Completed(StoredResponse, StatusCodes.Status201Created, "application/json") with
        {
            Location = "/widgets/42"
        };

        await new ReplayedResult(record).ExecuteAsync(httpContext);

        await Assert.That(httpContext.Response.Headers.Location.ToString()).IsEqualTo("/widgets/42");
    }

    private static IdempotencyRecord Completed(string? body, int statusCode, string? contentType) => new()
    {
        Id = "key",
        OwnerToken = Guid.CreateVersion7(),
        Fingerprint = "e3b0c44298fc1c149afbf4c8996fb924",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        CompletedAt = DateTimeOffset.UtcNow,
        Response = body,
        StatusCode = statusCode,
        ContentType = contentType
    };

    private static async Task<string> ReadBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;

        return await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();
    }

    private static DefaultHttpContext NewContext() => new() { Response = { Body = new MemoryStream() } };
}
