using ApiGatewayPay.Api.Middleware;
using ApiGatewayPay.Api.Security;
using ApiGatewayPay.Infrastructure.Security;

namespace ApiGatewayPay.Api.Endpoints;

/// <summary>Endpoints OAuth2 del emisor local (solo con Auth:Mode=Local). Formato RFC 6749 / RFC 7009.</summary>
public static class AuthEndpoints
{
    public static void MapLocalAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/v1/auth").WithTags("Auth (solo desarrollo)").AllowAnonymous().RequireRateLimiting("auth").DisableAntiforgery();

        auth.MapPost("/token", async (HttpContext http, LocalTokenService tokens, CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return await Error(http, "invalid_request");
            var form = await http.Request.ReadFormAsync(ct);
            TokenResponse? response = form["grant_type"].ToString() switch
            {
                "password" => await tokens.PasswordGrantAsync(form["username"].ToString(), form["password"].ToString(),
                    form["otp"].ToString(), form["scope"].ToString(), ct),
                "refresh_token" => await tokens.RefreshGrantAsync(form["refresh_token"].ToString(), ct),
                _ => null,
            };
            // Mensaje idéntico para usuario inexistente, contraseña u OTP errados o token revocado (sin enumeración).
            return response is null ? await Error(http, "invalid_grant") : Results.Ok(response);
        });

        auth.MapPost("/revoke", async (HttpContext http, RefreshTokenService refresh, CancellationToken ct) =>
        {
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(ct);
                if (form["token"].ToString() is { Length: > 0 and < 200 } token) await refresh.RevokeAsync(token, ct);
            }
            return Results.Ok(); // RFC 7009: siempre 200
        });
    }

    private static async Task<IResult> Error(HttpContext http, string code)
    {
        await ProblemWriter.WriteAsync(http, 400, code, "Credenciales o parámetros inválidos.");
        return Results.Empty;
    }
}
