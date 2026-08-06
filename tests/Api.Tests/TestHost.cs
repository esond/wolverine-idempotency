using Alba;
using JasperFx;
using JasperFx.CommandLine;
using Marten;
using Microsoft.AspNetCore.Hosting;
using TUnit.Core.Interfaces;

[assembly: NotInParallel]

namespace Idempotency.Tests;

/// <summary>
/// The sample API, started once for the whole run against the database <c>docker-compose.yml</c> brings up.
/// </summary>
public sealed class TestHost : IAsyncInitializer, IAsyncDisposable
{
    public const string ConnectionString =
        "Host=localhost;Port=5433;Database=idempotency;Username=postgres;Password=postgres";

    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();
    }

    public async Task InitializeAsync()
    {
        JasperFxEnvironment.AutoStartHost = true;

        Host = await AlbaHost.For<Program>(builder =>
            builder.UseSetting("ConnectionStrings:postgres", ConnectionString));

        await Host.DocumentStore().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
}
