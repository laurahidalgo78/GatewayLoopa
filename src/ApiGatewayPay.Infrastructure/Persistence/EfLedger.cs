using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiGatewayPay.Core.Domain.Ledger;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using Microsoft.EntityFrameworkCore;

namespace ApiGatewayPay.Infrastructure.Persistence;

public sealed class LedgerOptions
{
    public const string Section = "Ledger";

    /// <summary>Clave HMAC de la cadena. Vive fuera de la BD: quien tenga acceso de escritura a la BD no puede recalcular la cadena.</summary>
    public string HmacKeySecretName { get; set; } = "ledger/hmac-key";
}

/// <summary>
/// Libro append-only con encadenamiento HMAC por agregado. Cada entrada incluye el hash de la anterior
/// del mismo agregado; la concurrencia optimista del agregado serializa las escrituras sin bloqueos globales.
/// </summary>
public sealed class EfLedger(AppDbContext db, ISecretProvider secrets, Microsoft.Extensions.Options.IOptions<LedgerOptions> options) : ILedger
{
    public const string Genesis = "GENESIS";
    private readonly Dictionary<string, string> _lastHashInScope = new();

    public async Task AppendAsync(LedgerRecord r, CancellationToken ct)
    {
        var key = $"{r.AggregateType}:{r.AggregateId}";
        if (!_lastHashInScope.TryGetValue(key, out var previous))
        {
            previous = await db.Ledger.AsNoTracking()
                .Where(e => e.AggregateType == r.AggregateType && e.AggregateId == r.AggregateId)
                .OrderByDescending(e => e.AggregateSequence)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync(ct) ?? Genesis;
        }

        var entry = new LedgerEntryEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = r.Actor.TenantId,
            AggregateType = r.AggregateType,
            AggregateId = r.AggregateId,
            AggregateSequence = r.AggregateSequence,
            EventType = r.EventType,
            FromStatus = r.FromStatus,
            ToStatus = r.ToStatus,
            AmountMinor = r.AmountMinor,
            Currency = r.Currency,
            ActorType = r.Actor.Type.ToString(),
            ActorId = r.Actor.ActorId,
            StrongAuthentication = r.Actor.StrongAuthentication,
            SourceIp = r.Actor.SourceIp,
            UserAgent = r.Actor.UserAgent is { Length: > 200 } ua ? ua[..200] : r.Actor.UserAgent,
            CorrelationId = r.Actor.CorrelationId,
            OccurredAt = r.OccurredAt,
            Detail = r.Detail is { Length: > 500 } d ? d[..500] : r.Detail,
            PreviousHash = previous,
        };
        entry.Hash = ComputeHash(await GetKeyAsync(secrets, options.Value, ct), entry);
        _lastHashInScope[key] = entry.Hash;
        db.Ledger.Add(entry);
        if (r.AggregateType == "payment" && r.ToStatus is not null && r.ToStatus != r.FromStatus)
            Telemetry.AppMetrics.PaymentTransition(r.ToStatus);
    }

    internal static async ValueTask<byte[]> GetKeyAsync(ISecretProvider secrets, LedgerOptions options, CancellationToken ct) =>
        Convert.FromBase64String(await secrets.GetAsync(options.HmacKeySecretName, ct));

    internal static string ComputeHash(byte[] key, LedgerEntryEntity e)
    {
        // Serialización canónica (array JSON con orden fijo) para que el hash sea reproducible.
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            e.PreviousHash, e.TenantId, e.AggregateType, e.AggregateId, e.AggregateSequence, e.EventType, e.FromStatus, e.ToStatus,
            e.AmountMinor, e.Currency, e.ActorType, e.ActorId, e.StrongAuthentication, e.SourceIp, e.UserAgent, e.CorrelationId,
            e.OccurredAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture), e.Detail,
        });
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record LedgerVerification(bool Valid, int AggregatesChecked, int EntriesChecked, string? FirstProblem);

/// <summary>Recalcula la cadena y detecta alteraciones, huecos de secuencia o entradas eliminadas al final.</summary>
public sealed class LedgerVerifier(AppDbContext db, ISecretProvider secrets, Microsoft.Extensions.Options.IOptions<LedgerOptions> options)
{
    public async Task<LedgerVerification> VerifyAsync(string tenantId, string? aggregateType, string? aggregateId, CancellationToken ct)
    {
        var key = await EfLedger.GetKeyAsync(secrets, options.Value, ct);
        var query = db.Ledger.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (aggregateType is not null) query = query.Where(e => e.AggregateType == aggregateType);
        if (aggregateId is not null) query = query.Where(e => e.AggregateId == aggregateId);

        int aggregates = 0, entries = 0;
        string? currentKey = null, previousHash = EfLedger.Genesis;
        var expectedSeq = 1;
        await foreach (var e in query.OrderBy(e => e.AggregateType).ThenBy(e => e.AggregateId).ThenBy(e => e.AggregateSequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            var k = $"{e.AggregateType}:{e.AggregateId}";
            if (k != currentKey)
            {
                if (currentKey is not null && await CheckTailAsync(currentKey, expectedSeq - 1, ct) is { } tailProblem)
                    return new(false, aggregates, entries, tailProblem);
                currentKey = k; previousHash = EfLedger.Genesis; expectedSeq = 1; aggregates++;
            }
            entries++;
            if (e.AggregateSequence != expectedSeq)
                return new(false, aggregates, entries, $"{k}: hueco de secuencia en {expectedSeq}");
            if (e.PreviousHash != previousHash)
                return new(false, aggregates, entries, $"{k}: enlace roto en secuencia {e.AggregateSequence}");
            if (EfLedger.ComputeHash(key, e) != e.Hash)
                return new(false, aggregates, entries, $"{k}: contenido alterado en secuencia {e.AggregateSequence}");
            previousHash = e.Hash;
            expectedSeq++;
        }
        if (currentKey is not null && await CheckTailAsync(currentKey, expectedSeq - 1, ct) is { } problem)
            return new(false, aggregates, entries, problem);
        return new(true, aggregates, entries, null);
    }

    /// <summary>Borrar las últimas entradas no rompe la cadena; por eso se cruza con la versión actual del agregado.</summary>
    private async Task<string?> CheckTailAsync(string key, int lastSequence, CancellationToken ct)
    {
        var parts = key.Split(':', 2);
        if (parts[0] != "payment" || !Guid.TryParseExact(parts[1], "N", out var id)) return null;
        var version = await db.Payments.AsNoTracking().Where(p => p.Id == id).Select(p => (int?)p.Version).FirstOrDefaultAsync(ct);
        return version is not null && version != lastSequence ? $"{key}: la versión del pago ({version}) no coincide con el libro ({lastSequence})" : null;
    }
}
