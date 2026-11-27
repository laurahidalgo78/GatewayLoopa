using System.Security.Cryptography;
using System.Text;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Ledger;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Crypto;
using ApiGatewayPay.Infrastructure.Persistence;
using ApiGatewayPay.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Security;

public sealed record ApiKeyPrincipal(string KeyId, string TenantId, string Name, IReadOnlyList<string> Scopes);

public sealed record CreatedApiKey(string KeyId, string Key, IReadOnlyList<string> Scopes, DateTime ExpiresAt);

public sealed record ApiKeySummary(string KeyId, string Name, IReadOnlyList<string> Scopes, DateTime CreatedAt, DateTime ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt);

/// <summary>
/// API keys para clientes máquina. Formato: agp_test_{id16}_{secreto} (sandbox) / agp_live_... (producción).
/// Solo se guarda HMAC-SHA256(pepper, secreto): un volcado de la BD no permite usar las claves.
/// El prefijo de entorno hace imposible usar una clave sandbox en producción y viceversa.
/// </summary>
public sealed class ApiKeyService(
    AppDbContext db, ISecretProvider secrets, IOptions<PlatformOptions> platform, ILedger ledger,
    IMemoryCache cache, TimeProvider time, ILogger<ApiKeyService> logger)
{
    /// <summary>Las máquinas nunca obtienen scopes de aprobación ni de administración (requieren humano + MFA).</summary>
    public static readonly string[] AllowedMachineScopes = ["payments:read", "payments:write", "accounts:read", "beneficiaries:read"];
    private const string PepperSecret = "auth/api-key-pepper";
    private const int IdLength = 16;

    public async Task<CreatedApiKey> CreateAsync(string name, IReadOnlyList<string> scopes, int expiresInDays, AuditActor actor, CancellationToken ct)
    {
        var invalid = scopes.Except(AllowedMachineScopes).ToList();
        if (invalid.Count > 0 || scopes.Count == 0)
            throw AppException.Validation("invalid_scopes", "Scopes no permitidos para API keys.");
        if (expiresInDays is < 1 or > 365)
            throw AppException.Validation("invalid_expiration", "La expiración debe estar entre 1 y 365 días.");

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdLength / 2));
        var secret = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow().UtcDateTime;
        var entity = new ApiKeyEntity
        {
            Id = id, TenantId = actor.TenantId, Name = name, SecretHash = await HashAsync(secret, ct),
            Scopes = string.Join(' ', scopes.Distinct()), Environment = platform.Value.Environment.ToString(),
            CreatedBy = actor.ActorId, CreatedAt = now, ExpiresAt = now.AddDays(expiresInDays),
        };
        db.ApiKeys.Add(entity);
        await ledger.AppendAsync(new LedgerRecord("apikey", id, 1, "apikey.created", actor, now, Detail: $"scopes={entity.Scopes};expires={entity.ExpiresAt:yyyy-MM-dd}"), ct);
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(id, $"{platform.Value.ApiKeyPrefix}{id}_{secret}", scopes, entity.ExpiresAt);
    }

    public async Task<ApiKeyPrincipal?> ValidateAsync(string rawKey, CancellationToken ct)
    {
        var expectedPrefix = platform.Value.ApiKeyPrefix;
        if (!rawKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            if (rawKey.StartsWith("agp_", StringComparison.Ordinal))
                logger.LogWarning("API key de otro entorno rechazada");
            return null;
        }
        var rest = rawKey[expectedPrefix.Length..];
        if (rest.Length < IdLength + 2 || rest[IdLength] != '_') return null;
        var id = rest[..IdLength];
        var secret = rest[(IdLength + 1)..];
        if (secret.Length > 64) return null;

        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
        var hash = await HashAsync(secret, ct); // se calcula siempre: tiempo similar exista o no la clave
        if (key is null || key.RevokedAt is not null || key.ExpiresAt <= time.GetUtcNow().UtcDateTime ||
            key.Environment != platform.Value.Environment.ToString() ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(key.SecretHash)))
            return null;

        if (!cache.TryGetValue("apikey-used:" + id, out _))
        {
            cache.Set("apikey-used:" + id, true, TimeSpan.FromMinutes(5));
            var now = time.GetUtcNow().UtcDateTime;
            await db.ApiKeys.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }
        return new ApiKeyPrincipal(key.Id, key.TenantId, key.Name, key.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task RevokeAsync(string keyId, AuditActor actor, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.TenantId == actor.TenantId, ct)
            ?? throw AppException.NotFound("api_key_not_found", "API key no encontrada.");
        if (key.RevokedAt is not null) return;
        var now = time.GetUtcNow().UtcDateTime;
        key.RevokedAt = now;
        key.RevokedBy = actor.ActorId;
        await ledger.AppendAsync(new LedgerRecord("apikey", keyId, 2, "apikey.revoked", actor, now), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKeySummary>> ListAsync(string tenantId, CancellationToken ct) =>
        (await db.ApiKeys.AsNoTracking().Where(k => k.TenantId == tenantId).OrderByDescending(k => k.CreatedAt).ToListAsync(ct))
        .Select(k => new ApiKeySummary(k.Id, k.Name, k.Scopes.Split(' '), k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt)).ToList();

    private async Task<string> HashAsync(string secret, CancellationToken ct)
    {
        var pepper = Encoding.UTF8.GetBytes(await secrets.GetAsync(PepperSecret, ct));
        return Convert.ToHexStringLower(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(secret)));
    }
}
