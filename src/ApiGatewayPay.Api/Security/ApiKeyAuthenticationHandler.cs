using System.Security.Claims;
using System.Text.Encodings.Web;
using ApiGatewayPay.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Api.Security;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptionsMonitor<AuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values)) return AuthenticateResult.NoResult();
        if (values.Count != 1 || values[0] is not { Length: > 20 and < 200 } raw) return AuthenticateResult.Fail("invalid_api_key");

        var principal = await Context.RequestServices.GetRequiredService<ApiKeyService>().ValidateAsync(raw, Context.RequestAborted);
        if (principal is null)
        {
            Infrastructure.Telemetry.AppMetrics.AuthFailure("invalid_api_key");
            return AuthenticateResult.Fail("invalid_api_key");
        }

        var claimNames = auth.CurrentValue.Claims;
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "apikey:" + principal.KeyId),
            new Claim(claimNames.Tenant, principal.TenantId),
            new Claim(claimNames.Scope, string.Join(' ', principal.Scopes)),
            new Claim(ClaimTypesEx.ActorType, "api_key"),
        ], SchemeName, "sub", null);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "ApiKey";
        return Task.CompletedTask;
    }
}
