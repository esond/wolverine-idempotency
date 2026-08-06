using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http;
using Wolverine.Middleware;

namespace Idempotency;

/// <summary>
/// Attaches idempotency handling to every documented POST chain that has not opted out, closed over the response
/// type that chain returns.
/// </summary>
/// <remarks>
/// A generic middleware closed over a runtime type can only be added to a chain from code, not from the
/// compile-time <c>[Middleware(typeof(X))]</c> attribute — this policy is that code.
///
/// <c>chain.IsTransactional</c> is checked here rather than after this policy wires the middleware in, because
/// Wolverine decides it during its own transaction-detection pass over <c>chain.Middleware</c> — a pass that
/// completes, chain by chain, before any <see cref="IHttpPolicy" /> runs. It never sees
/// <see cref="IdempotencyMiddleware" />'s own <see cref="Marten.IDocumentSession" /> parameter, so a handler that
/// doesn't itself trigger the detection gets no save frame at all: the completion record inserts into a session
/// nothing ever flushes.
///
/// Nothing here can assert the registration order <see cref="IdempotencyMiddleware" /> needs, because a policy runs
/// before the frames it competes with exist. That check lives at runtime instead.
/// </remarks>
public sealed class IdempotencyPolicy : IHttpPolicy
{
    private const string CompletionMethod = "After";

    private const string ResponseParameter = "response";

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // Test-support and other internal endpoints are absent from the published API, so no integrator can send
            // them a key. Exempting them also keeps the transaction requirement below from failing a host over an
            // endpoint that deliberately works outside a session.
            if (!chain.HttpMethods.Contains(HttpMethods.Post) ||
                chain.HasAttribute<ExcludeFromDescriptionAttribute>())
                continue;

            // Ahead of the transaction requirement below, which exists only to persist a completion record this
            // chain never writes.
            if (chain.HasAttribute<IdempotencyOptOutAttribute>())
            {
                if (chain.HasAttribute<IdempotencyRequiredAttribute>())
                    throw new InvalidOperationException(
                        $"POST {chain.OperationId} is marked both [IdempotencyOptOut] and [IdempotencyRequired]. " +
                        "An opted-out endpoint ignores the header, so the requirement would never be enforced.");

                continue;
            }

            if (!chain.IsTransactional)
                throw new InvalidOperationException(
                    $"POST {chain.OperationId} needs a Marten transaction — its idempotency completion record only " +
                    "persists by riding the handler's own commit. Give the handler an " +
                    "IDocumentSession/IQuerySession/IDocumentOperations parameter, an [AggregateHandler] or " +
                    "[ReadAggregate]/[WriteAggregate] shape, or an IMartenOp return.");

            var middlewareType = MiddlewareFor(chain);

            var middleware = new MiddlewarePolicy();
            middleware.AddType(middlewareType);
            middleware.Apply([chain], rules, container);

            if (middlewareType.IsGenericType)
                BindResponse(chain, middlewareType);

            CommittedAggregate.HoistProjection(chain);
        }
    }

    /// <remarks>
    /// Wolverine fills a middleware parameter with whichever variable matches its type, and several chains hold two
    /// of the same type — an aggregate loaded before the handler runs, and the handler's own return. Type matching
    /// picks the earlier one, which is the state before this request changed anything. Naming the resource variable
    /// outright is what keeps a replay from serving a body the original request never sent.
    /// </remarks>
    private static void BindResponse(HttpChain chain, Type middlewareType)
    {
        var response = chain.ResourceVariable
            ?? chain.Method.Creates.FirstOrDefault(variable => variable.VariableType == chain.ResourceType)
            ?? throw new InvalidOperationException(
                $"POST {chain.OperationId} returns {chain.ResourceType?.Name} but creates no variable of that type " +
                "for its idempotency completion to store. A handler returning an UpdatedAggregate marker lands here, " +
                "because Wolverine rewrites the marker to the aggregate's own type and then writes the body by " +
                $"re-fetching it after the commit; return CommittedAggregate<{chain.ResourceType?.Name}> instead.");

        chain.Postprocessors
            .OfType<MethodCall>()
            .Single(call => call.Method.DeclaringType == middlewareType && call.Method.Name == CompletionMethod)
            .TrySetArgument(ResponseParameter, response);
    }

    private static Type MiddlewareFor(HttpChain chain)
    {
        if (chain.ResourceType is not { } resourceType || resourceType == typeof(void))
            return typeof(IdempotencyStatusMiddleware);

        var middleware = chain.HasAttribute<IdempotencyOmitsResponseBodyAttribute>()
            ? typeof(IdempotencyBodylessResponseMiddleware<>)
            : typeof(IdempotencyResponseMiddleware<>);

        return middleware.MakeGenericType(resourceType);
    }
}
