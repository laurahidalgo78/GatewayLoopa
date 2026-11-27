using System.Globalization;
using System.Security.Claims;
using ApiGatewayPay.Api.Middleware;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Infrastructure.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Api.Security;

public static class Policies
{
    public const string StepUp = "step-up";

    public static string Scope(string scope) => "scope:" + scope;

    /// <summary>Una política por scope + step-up. La política por defecto exige autenticación (deny by default).</summary>
    public static void Register(AuthorizationOptions options)
    {
        foreach (var scope in Scopes.All)
            options.AddPolicy(Scope(scope), p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(scope)));
        options.AddPolicy(StepUp, p => p.RequireAuthenticatedUser().AddRequirements(new StepUpRequirement()));
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}

public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;

public sealed class ScopeHandler(IOptionsMonitor<AuthOptions> options) : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (context.User.GetScopes(options.CurrentValue.Claims.Scope).Contains(requirement.Scope)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class StepUpRequirement : IAuthorizationRequirement;

public sealed class StepUpHandler(IOptionsMonitor<AuthOptions> options, TimeProvider time) : AuthorizationHandler<StepUpRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        if (StepUp.IsSatisfied(context.User, options.CurrentValue.StepUp, time)) context.Succeed(requirement);
        else context.Fail(new AuthorizationFailureReason(this, "step_up_required"));
        return Task.CompletedTask;
    }
}

public static class StepUp
{
    /// <summary>
    /// MFA reciente: acr o amr aceptados y auth_time dentro de MaxAgeSeconds. Una API key nunca cumple:
    /// las operaciones sensibles requieren siempre un humano.
    /// </summary>
    public static bool IsSatisfied(ClaimsPrincipal user, StepUpOptions o, TimeProvider time)
    {
        if (user.FindFirst(ClaimTypesEx.ActorType)?.Value != "user") return false;
        var acr = user.FindFirst("acr")?.Value;
        var strong = (acr is not null && o.AcceptedAcr.Contains(acr)) || user.FindAll("amr").Any(c => o.AcceptedAmr.Contains(c.Value));
        if (!strong) return false;
        if (!long.TryParse(user.FindFirst("auth_time")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var authTime)) return false;
        return time.GetUtcNow().ToUnixTimeSeconds() - authTime <= o.MaxAgeSeconds;
    }
}

public static class ClaimTypesEx
{
    public const string ActorType = "actor_type";
}

public static class PrincipalExtensions
{
    public static HashSet<string> GetScopes(this ClaimsPrincipal user, string claimName) =>
        user.FindAll(claimName).Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Construye el actor auditado desde el token. Los casos de uso nunca leen claims directamente.</summary>
    public static AuditActor GetActor(this HttpContext http)
    {
        var options = http.RequestServices.GetRequiredService<IOptionsMonitor<AuthOptions>>().CurrentValue;
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        var user = http.User;
        var subject = user.FindFirst("sub")?.Value ?? throw new AppException(ErrorKind.Unauthorized, "unauthenticated", "Autenticación requerida.");
        var tenant = user.FindFirst(options.Claims.Tenant)?.Value;
        if (tenant is not { Length: > 0 and <= 64 } || !tenant.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            throw AppException.Forbidden("tenant_required", "El token no contiene un tenant válido.");
        var type = user.FindFirst(ClaimTypesEx.ActorType)?.Value switch
        {
            "user" => ActorType.User,
            "api_key" => ActorType.ApiKey,
            _ => ActorType.Machine,
        };
        var ua = http.Request.Headers.UserAgent.ToString();
        return new AuditActor(subject, type, tenant, http.Connection.RemoteIpAddress?.ToString(),
            ua.Length > 200 ? ua[..200] : ua, CorrelationContext.Id ?? http.TraceIdentifier, StepUp.IsSatisfied(user, options.StepUp, time));
    }
}

/// <summary>
/// Respuestas 401/403 uniformes en problem+json. Si falta step-up, responde 401 con el desafío de RFC 9470
/// (insufficient_user_authentication + acr_values + max_age) para que el cliente re-autentique con MFA.
/// </summary>
public sealed class ProblemAuthorizationResultHandler(IOptionsMonitor<AuthOptions> options) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Succeeded)
        {
            await _default.HandleAsync(next, context, policy, result);
            return;
        }

        if (result.Challenged)
        {
            await _default.HandleAsync(next, context, policy, result);
            Infrastructure.Telemetry.AppMetrics.AuthFailure("unauthenticated");
            await ProblemWriter.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthenticated", "Autenticación requerida o credenciales inválidas.");
            return;
        }

        if (result.AuthorizationFailure?.FailureReasons.Any(r => r.Handler is StepUpHandler) == true)
        {
            var o = options.CurrentValue.StepUp;
            context.Response.Headers.WWWAuthenticate =
                $"Bearer error=\"insufficient_user_authentication\", error_description=\"Se requiere MFA reciente\", acr_values=\"{o.RequiredAcr}\", max_age={o.MaxAgeSeconds}";
            Infrastructure.Telemetry.AppMetrics.AuthFailure("step_up_required");
            await ProblemWriter.WriteAsync(context, StatusCodes.Status401Unauthorized, "step_up_required", "Esta operación requiere autenticación multifactor reciente.");
            return;
        }

        Infrastructure.Telemetry.AppMetrics.AuthFailure("forbidden");
        await ProblemWriter.WriteAsync(context, StatusCodes.Status403Forbidden, "insufficient_scope", "No tiene permisos para esta operación.");
    }
}
