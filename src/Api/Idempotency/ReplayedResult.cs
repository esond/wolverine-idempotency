using System.Text;
using Microsoft.AspNetCore.Http;

namespace Idempotency;

/// <summary>
/// Returns a stored success response to a repeat of the request that produced it.
/// </summary>
/// <remarks>
/// The stored body is written as-is instead of being deserialized and re-serialized, so a replay is byte-identical
/// to the response the first request received. A response that carried no body replays with neither a body nor a
/// content type, rather than an empty one — a client reading Content-Length or Content-Type would otherwise see a
/// replayed 204 differ from the 204 it originally got.
/// </remarks>
public sealed class ReplayedResult(IdempotencyRecord record) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;

        response.StatusCode = record.StatusCode ?? StatusCodes.Status200OK;
        response.Headers[IdempotencyHeaderNames.IdempotentReplayed] = "true";

        if (record.Location is { } location)
            response.Headers.Location = location;

        if (record.Response is not { } body)
            return;

        response.ContentType = record.ContentType;

        await response.WriteAsync(body, Encoding.UTF8, httpContext.RequestAborted);
    }
}
