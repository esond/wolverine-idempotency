using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Idempotency.Sample;

/// <summary>
/// Authenticates a request as whatever tenant its <c>X-Tenant</c> header names.
/// </summary>
/// <remarks>
/// Sample only. It stands in for real authentication so the scoping the mechanism depends on stays visible: an
/// idempotency key belongs to a principal, and two tenants sending the same key must not reach each other's stored
/// response.
/// </remarks>
public sealed class TenantAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Tenant";

    public const string HeaderName = "X-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenant = Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(tenant))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, tenant)], SchemeName);

        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
