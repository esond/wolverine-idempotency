using Alba;
using Idempotency.Sample;
using Marten;

namespace Idempotency.Tests;

[ClassDataSource<TestHost>(Shared = SharedType.PerTestSession)]
public abstract class IntegrationTestBase(TestHost fixture)
{
    protected const string Tenant = "tenant-a";

    protected IAlbaHost Host { get; } = fixture.Host;

    protected IDocumentStore Store => Host.DocumentStore();

    protected static string NewKey() => Guid.CreateVersion7().ToString();

    /// <summary>
    /// Runs a scenario authenticated as <paramref name="tenant" />, optionally carrying an idempotency key.
    /// </summary>
    protected Task<IScenarioResult> Scenario(Action<Scenario> configure, string? key = null,
        string tenant = Tenant) =>
        Host.Scenario(scenario =>
        {
            scenario.WithRequestHeader(TenantAuthenticationHandler.HeaderName, tenant);

            if (key is not null)
                scenario.WithRequestHeader(IdempotencyHeaderNames.IdempotencyKey, key);

            configure(scenario);
        });

    protected static bool WasReplayed(IScenarioResult result) =>
        result.Context.Response.Headers.ContainsKey(IdempotencyHeaderNames.IdempotentReplayed);

    protected static async Task<T> Body<T>(Task<IScenarioResult> scenario) =>
        (await (await scenario).ReadAsJsonAsync<T>())!;
}
