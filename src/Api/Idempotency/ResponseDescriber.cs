using System.Collections.Concurrent;
using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Idempotency;

/// <summary>
/// The parts of a response a replay reproduces.
/// </summary>
public sealed record CompletedResponse(string? Body, int StatusCode, string? ContentType, string? Location);

/// <summary>
/// Reads what a handler returned into the response a replay reproduces, or decides the request is not one to
/// remember.
/// </summary>
public static class ResponseDescriber
{
    // Location has no interface to match on the way IStatusCodeHttpResult and IValueHttpResult do, and it is spread
    // across eight unrelated result types (Created/Accepted, their generic and AtRoute forms). Matching the property
    // by name covers all of them, and any later addition, without an enumeration that silently drops a header.
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> LocationProperties = new();

    /// <summary>
    /// Describes <paramref name="response" /> for storage, or returns null when the request must not be remembered.
    /// </summary>
    /// <remarks>
    /// A null description releases the key, which is what makes a failed arm of a <c>Results&lt;…&gt;</c> union
    /// retryable on the same key. Only a 2xx is stored: a 404 or a validation failure describes the request that was
    /// sent, not work that happened, and a caller who fixes it deserves the same key back.
    /// </remarks>
    public static CompletedResponse? Describe(object? response, JsonSerializerOptions serializerOptions)
    {
        var unwrapped = Unwrap(response);

        if (unwrapped is not IResult result)
        {
            // Wolverine's own writer turns a null resource into a 404, so a null here is a failed request the writer
            // has yet to describe, not a body-less success.
            return unwrapped is null
                ? null
                : new CompletedResponse(JsonSerializer.Serialize(unwrapped, serializerOptions),
                    StatusCodes.Status200OK, MediaTypeNames.Application.Json, null);
        }

        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;

        if (statusCode is < StatusCodes.Status200OK or > 299)
            return null;

        var value = (result as IValueHttpResult)?.Value;
        var body = value is null ? null : JsonSerializer.Serialize(value, serializerOptions);

        return new CompletedResponse(body, statusCode, body is null ? null : MediaTypeNames.Application.Json,
            LocationOf(result));
    }

    // Loops rather than unwrapping once: a Results<…> union nested inside another resolves through several layers.
    private static object? Unwrap(object? response)
    {
        while (response is INestedHttpResult nested)
            response = nested.Result;

        return response;
    }

    private static string? LocationOf(IResult result) =>
        LocationProperties.GetOrAdd(result.GetType(), static type =>
                type.GetProperty("Location", BindingFlags.Public | BindingFlags.Instance) is { } property &&
                property.PropertyType == typeof(string)
                    ? property
                    : null)
            ?.GetValue(result) as string;
}
