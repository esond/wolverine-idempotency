using Idempotency;
using Idempotency.Sample;
using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(TenantAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TenantAuthenticationHandler>(
        TenantAuthenticationHandler.SchemeName, configureOptions: null);

builder.Services.AddAuthorization();

builder.Services
    .AddMarten(options =>
    {
        options.Connection(builder.Configuration.GetConnectionString("postgres")!);
        options.DatabaseSchemaName = "idempotency";
        options.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);
    })
    .UseLightweightSessions()
    .IntegrateWithWolverine();

builder.Services.AddIdempotency();
builder.Services.AddWolverineHttp();

builder.Host.UseWolverine(options =>
{
    // Named outright because a test host runs this file from the test assembly, and Wolverine would otherwise
    // discover that assembly's handlers and endpoints instead of this one's.
    options.ApplicationAssembly = typeof(Program).Assembly;

    options.Policies.AutoApplyTransactions();
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapWolverineEndpoints(options =>
{
    // Registered last on purpose. A Wolverine middleware frame is inserted at the head of the chain, so the last
    // registration runs first — and the fingerprint has to read the raw request body before anything deserializes it.
    options.AddPolicy<IdempotencyPolicy>();
});

return await app.RunJasperFxCommands(args);

public partial class Program;
