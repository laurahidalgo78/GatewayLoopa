using System.Security.Cryptography;
using System.Text;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Infrastructure.Crypto;
using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ApiGatewayPay.Infrastructure.Security;

/// <summary>Datos de la sesión original. auth_time y amr NO se renuevan al refrescar: un refresh no equivale a MFA.</summary>
public sealed record SessionGrant(string Subject, string TenantId, string Scopes, string Amr, string Acr, DateTime AuthTime);

/// <summary>
/// Refresh tokens opacos con rotación en cada uso y detección de reutilización: presentar un token ya
/// usado revoca toda la familia (indicio de robo, RFC 9700 §4.14). Solo se guarda su SHA-256.
/// </summary>
public sealed class RefreshTokenService(AppDbContext db, IAlertNotifier alerts, TimeProvider time)
{
    public async Task<string> IssueAsync(SessionGrant grant, TimeSpan lifetime, TimeSpan familyLifetime, CancellationToken ct, Guid? familyId = null, DateTime? familyExpiresAt = null)
    {
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow().UtcDateTime;
        var familyEnd = familyExpiresAt ?? now + familyLifetime;
        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.CreateVersion7(), TokenHash = Hash(token), FamilyId = familyId ?? Guid.NewGuid(),
            Subject = grant.Subject, TenantId = grant.TenantId, Scopes = grant.Scopes, Amr = grant.Amr, Acr = grant.Acr,
            AuthTime = grant.AuthTime, CreatedAt = now,
            ExpiresAt = now + lifetime < familyEnd ? now + lifetime : familyEnd, FamilyExpiresAt = familyEnd,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<(SessionGrant Grant, string NewToken)?> RotateAsync(string token, TimeSpan lifetime, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = Hash(token);
        var entity = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (entity is null || entity.RevokedAt is not null || entity.ExpiresAt <= now) return null;

        if (entity.UsedAt is not null)
        {
            await RevokeFamilyAsync(entity.FamilyId, ct);
            await alerts.NotifyAsync(new Alert(AlertSeverity.High, "refresh_token_reuse", "Reutilización de refresh token: familia revocada.",
                new Dictionary<string, string> { ["subject"] = entity.Subject }), ct);
            return null;
        }

        // Marcado condicional: si dos peticiones concurrentes usan el mismo token, solo una gana.
        var claimed = await db.RefreshTokens.Where(t => t.Id == entity.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed != 1)
        {
            await RevokeFamilyAsync(entity.FamilyId, ct);
            return null;
        }

        var grant = new SessionGrant(entity.Subject, entity.TenantId, entity.Scopes, entity.Amr, entity.Acr, entity.AuthTime);
        var newToken = await IssueAsync(grant, lifetime, TimeSpan.Zero, ct, entity.FamilyId, entity.FamilyExpiresAt);
        return (grant, newToken);
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        var entity = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (entity is not null) await RevokeFamilyAsync(entity.FamilyId, ct);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
