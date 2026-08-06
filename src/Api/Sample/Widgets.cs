using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;

namespace Idempotency.Sample;

public record Widget
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Size { get; init; }

    public bool Archived { get; init; }

    public string? TokenHash { get; init; }
}

public record CreateWidget(string Name, int Size);

public record WidgetPreview(string Name, int Size);

public record WidgetToken(string Value);

public static class WidgetEndpoints
{
    /// <summary>
    /// A plain document write: the simplest chain the mechanism covers.
    /// </summary>
    /// <remarks>
    /// The refusing arm is the interesting one. A non-2xx response is never stored, and the key is released as the
    /// response starts, so the caller can correct the body and retry on the same key.
    /// </remarks>
    [WolverinePost("/widgets")]
    public static Results<Created<Widget>, BadRequest<string>> Create(CreateWidget command, IDocumentSession session)
    {
        if (command.Size <= 0)
            return TypedResults.BadRequest("Size must be greater than zero.");

        var widget = new Widget { Id = Guid.CreateVersion7(), Name = command.Name, Size = command.Size };

        session.Store(widget);

        return TypedResults.Created($"/widgets/{widget.Id}", widget);
    }

    [WolverineGet("/widgets/{id}")]
    public static Task<Widget?> Get(Guid id, IQuerySession session) => session.LoadAsync<Widget>(id);

    /// <summary>
    /// A success that carries no body, which is what separates a stored 204 from a request still in flight.
    /// </summary>
    [WolverinePost("/widgets/{id}/archive")]
    public static async Task<Results<NoContent, NotFound>> Archive(Guid id, IDocumentSession session)
    {
        if (await session.LoadAsync<Widget>(id) is not { } widget)
            return TypedResults.NotFound();

        session.Store(widget with { Archived = true });

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Mints a token the server keeps only a hash of.
    /// </summary>
    /// <remarks>
    /// Storing the response would put the plaintext this endpoint deliberately declines to keep into the
    /// idempotency record for the whole retention window. The repeat replays the status and <c>Location</c> alone.
    /// </remarks>
    [IdempotencyOmitsResponseBody]
    [WolverinePost("/widgets/{id}/tokens")]
    public static async Task<Results<Created<WidgetToken>, NotFound>> MintToken(Guid id, IDocumentSession session)
    {
        if (await session.LoadAsync<Widget>(id) is not { } widget)
            return TypedResults.NotFound();

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        session.Store(widget with { TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))) });

        return TypedResults.Created($"/widgets/{id}/tokens", new WidgetToken(token));
    }

    /// <summary>
    /// Computes an answer and stores nothing.
    /// </summary>
    /// <remarks>
    /// There is no transaction here for a completion record to ride, so the endpoint opts out. Without the marking
    /// the policy fails code generation rather than let the header read as honoured on a chain that stores nothing.
    /// </remarks>
    [IdempotencyOptOut]
    [WolverinePost("/widgets/preview")]
    public static WidgetPreview Preview(CreateWidget command) => new(command.Name.ToUpperInvariant(), command.Size * 2);
}
