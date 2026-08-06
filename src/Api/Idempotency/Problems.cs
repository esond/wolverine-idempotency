using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Idempotency;

/// <summary>
/// The RFC 9457 problem documents this mechanism refuses a request with.
/// </summary>
public static class Problems
{
    public static ProblemDetails BadRequest(string detail) => new()
    {
        Title = "Bad Request",
        Detail = detail,
        Status = StatusCodes.Status400BadRequest,
        Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
    };

    public static ProblemDetails Conflict(string detail) => new()
    {
        Title = "Conflict",
        Detail = detail,
        Status = StatusCodes.Status409Conflict,
        Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10"
    };

    public static ProblemDetails UnprocessableEntity(string detail) => new()
    {
        Title = "Unprocessable Entity",
        Detail = detail,
        Status = StatusCodes.Status422UnprocessableEntity,
        Type = "https://tools.ietf.org/html/rfc4918#section-11.2"
    };
}
