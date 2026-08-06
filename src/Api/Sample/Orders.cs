using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace Idempotency.Sample;

public record OrderPlaced(string Reference, decimal Total);

public record OrderApproved;

public enum OrderStatus
{
    Placed,
    Approved
}

public record Order
{
    public Guid Id { get; init; }

    public string Reference { get; init; } = "";

    public decimal Total { get; init; }

    public OrderStatus Status { get; init; }

    /// <summary>
    /// The timestamp of the last event folded into this aggregate.
    /// </summary>
    /// <remarks>
    /// Marten stamps an event as it saves, so a projection running ahead of the commit reads the default value
    /// unless something stamps the pending events first.
    /// <see cref="CommittedAggregate{T}" /> is what does.
    /// </remarks>
    public DateTimeOffset LastModified { get; init; }

    public static Order Create(IEvent<OrderPlaced> placed) => new()
    {
        Id = placed.StreamId,
        Reference = placed.Data.Reference,
        Total = placed.Data.Total,
        Status = OrderStatus.Placed,
        LastModified = placed.Timestamp
    };

    public Order Apply(IEvent<OrderApproved> approved) =>
        this with { Status = OrderStatus.Approved, LastModified = approved.Timestamp };
}

public record PlaceOrder(string Reference, decimal Total);

public record OrderPlacedResponse(Guid Id);

public static class OrderEndpoints
{
    /// <summary>
    /// Starts an order stream and answers with the identifier the caller polls.
    /// </summary>
    [WolverinePost("/orders")]
    public static (Created<OrderPlacedResponse>, IStartStream) Place(PlaceOrder command)
    {
        var id = Guid.CreateVersion7();

        return (TypedResults.Created($"/orders/{id}", new OrderPlacedResponse(id)),
            MartenOps.StartStream<Order>(id, new OrderPlaced(command.Reference, command.Total)));
    }

    [WolverineGet("/orders/{orderId}")]
    public static Order Get([ReadAggregate] Order order) => order;

    /// <summary>
    /// Approves an order, answering with the aggregate as this request's own transaction leaves it.
    /// </summary>
    /// <remarks>
    /// Returning <see cref="Wolverine.Marten.UpdatedAggregate" /> here would write the body by re-reading the
    /// aggregate after the commit — after the frame that stores the idempotency record has already run. There would
    /// be nothing to store.
    ///
    /// The session parameter is unused, and load-bearing anyway: <c>chain.IsTransactional</c> is still false at the
    /// point an <see cref="Wolverine.Http.IHttpPolicy" /> runs for a chain whose only Marten shape is
    /// <c>[WriteAggregate]</c>, so without it the policy's own guard rejects this endpoint.
    /// </remarks>
    [IdempotencyRequired]
    [WolverinePost("/orders/{orderId}/approve")]
    public static (CommittedAggregate<Order>, OrderApproved) Approve([WriteAggregate] Order order,
        IDocumentSession session) =>
        (new CommittedAggregate<Order>(), new OrderApproved());
}
