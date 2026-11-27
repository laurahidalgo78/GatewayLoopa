using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ApiGatewayPay.Api.Security;

/// <summary>
/// Clave de firma del emisor local (ES256). Se carga del gestor de secretos; si no existe (solo desarrollo)
/// se genera una efímera: los tokens dejan de valer al reiniciar, lo cual es aceptable fuera de producción.
/// </summary>
public sealed class LocalSigningKey
{
    public LocalSigningKey(IOptions<AuthOptions> options, ISecretProvider secrets)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (options.Value.Local.SigningKeySecretName is { Length: > 0 } name)
            ecdsa.ImportFromPem(secrets.GetAsync(name, CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Key = new ECDsaSecurityKey(ecdsa) { KeyId = Convert.ToHexStringLower(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))[..16] };
    }

    public ECDsaSecurityKey Key { get; }
}

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>
/// Emisor OAuth2 integrado para desarrollo/sandbox (Auth:Mode=Local). En producción el IdP externo (OIDC)
/// cumple este rol con Authorization Code + PKCE; este servicio existe para poder probar de punta a punta,
/// incluyendo MFA (TOTP), step-up y rotación de refresh tokens.
/// </summary>
public sealed class LocalTokenService(
    IOptionsMonitor<AuthOptions> options, LocalSigningKey signingKey, ISecretProvider secrets,
    RefreshTokenService refreshTokens, IMemoryCache cache, TimeProvider time, ILogger<LocalTokenService> logger)
{
    public async Task<TokenResponse?> PasswordGrantAsync(string username, string password, string? otp, string? scope, CancellationToken ct)
    {
        var o = options.CurrentValue.Local;
        var lockKey = "login-fail:" + username.ToLowerInvariant();
        if (cache.TryGetValue(lockKey, out int failures) && failures >= o.MaxFailedLogins)
        {
            logger.LogWarning("Inicio de sesión bloqueado temporalmente por intentos fallidos");
            return null;
        }

        var user = o.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        var passwordOk = PasswordHasher.Verify(password, user?.PasswordHash ?? PasswordHasher.Dummy) && user is not null;
        var amr = new List<string> { "pwd" };
        if (passwordOk && !string.IsNullOrEmpty(otp))
        {
            var key = Totp.DecodeBase32(await secrets.GetAsync(user!.TotpSecretName, ct));
            var step = Totp.Verify(key, otp, time.GetUtcNow());
            // Un código TOTP solo sirve una vez (anti-replay dentro de su ventana de 30 s).
            var usedKey = $"totp-used:{user.Username}:{step}";
            if (step is null || cache.TryGetValue(usedKey, out _)) passwordOk = false;
            else
            {
                cache.Set(usedKey, true, TimeSpan.FromMinutes(2));
                amr.Add("otp");
                amr.Add("mfa");
            }
        }

        if (!passwordOk)
        {
            cache.Set(lockKey, failures + 1, TimeSpan.FromMinutes(o.LockoutMinutes));
            Infrastructure.Telemetry.AppMetrics.AuthFailure("invalid_grant");
            return null;
        }
        cache.Remove(lockKey);

        var requested = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var granted = requested.Length == 0 ? user!.Scopes : user!.Scopes.Intersect(requested).ToList();
        var acr = amr.Contains("mfa") ? options.CurrentValue.StepUp.RequiredAcr : "urn:agp:loa:pwd";
        var grant = new SessionGrant(user.Username, user.TenantId, string.Join(' ', granted), string.Join(' ', amr), acr, time.GetUtcNow().UtcDateTime);
        var refresh = await refreshTokens.IssueAsync(grant, TimeSpan.FromMinutes(o.RefreshTokenMinutes), TimeSpan.FromHours(o.SessionHours), ct);
        return Issue(grant, refresh);
    }

    public async Task<TokenResponse?> RefreshGrantAsync(string refreshToken, CancellationToken ct)
    {
        var rotated = await refreshTokens.RotateAsync(refreshToken, TimeSpan.FromMinutes(options.CurrentValue.Local.RefreshTokenMinutes), ct);
        return rotated is null ? null : Issue(rotated.Value.Grant, rotated.Value.NewToken);
    }

    private TokenResponse Issue(SessionGrant grant, string refreshToken)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(o.Local.AccessTokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Local.Issuer,
            Audience = o.Local.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.EcdsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = grant.Subject,
                [o.Claims.Tenant] = grant.TenantId,
                [o.Claims.Scope] = grant.Scopes,
                ["amr"] = grant.Amr.Split(' '),
                ["acr"] = grant.Acr,
                ["auth_time"] = new DateTimeOffset(grant.AuthTime).ToUnixTimeSeconds(),
                ["jti"] = Guid.NewGuid().ToString("N"),
                [ClaimTypesEx.ActorType] = "user",
            },
        });
        return new TokenResponse(token, "Bearer", (int)lifetime.TotalSeconds, refreshToken, grant.Scopes);
    }
}
