using Microsoft.Extensions.DependencyInjection;

namespace Idempotency;

public static class IdempotencyServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotency(this IServiceCollection services)
    {
        services.AddOptionsWithValidateOnStart<IdempotencyOptions>()
            .BindConfiguration(IdempotencyOptions.SectionName)
            .ValidateDataAnnotations();

        // Both are stateless once constructed: IdempotencyMetrics creates its instruments in the constructor, and
        // IdempotencyStore takes the caller's IDocumentSession as a method parameter, never as a dependency.
        return services
            .AddSingleton<IdempotencyMetrics>()
            .AddSingleton<IdempotencyStore>();
    }
}
