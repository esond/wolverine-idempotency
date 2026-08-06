using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

namespace Idempotency.Tests;

/// <summary>
/// The build-time guard: a POST that cannot commit a transaction fails code generation rather than accept a header
/// it would silently ignore.
/// </summary>
/// <remarks>
/// One offending endpoint only. Wolverine discovers HTTP endpoints by scanning the whole assembly and ignores
/// <c>Discovery.IncludeType</c>, so a second offender here would race this one to throw first.
/// </remarks>
public class IdempotencyPolicyGuardTests
{
    [Test]
    public async Task A_post_without_a_transaction_fails_code_generation()
    {
        await using var app = BuildHost();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            app.MapWolverineEndpoints(options => options.AddPolicy<IdempotencyPolicy>()));

        using (Assert.Multiple())
        {
            await Assert.That(exception!.Message).Contains("needs a Marten transaction");
            await Assert.That(exception.Message).Contains(nameof(NonTransactionalEndpoint));
        }
    }

    private static WebApplication BuildHost()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(TestHost.ConnectionString);
                options.DatabaseSchemaName = "idempotency_guard";
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

        builder.Services.AddIdempotency();
        builder.Services.AddWolverineHttp();

        builder.Host.UseWolverine(options => options.Policies.AutoApplyTransactions());

        return builder.Build();
    }
}

public static class NonTransactionalEndpoint
{
    [WolverinePost("/guard/no-transaction")]
    public static string Post() => "done";
}
