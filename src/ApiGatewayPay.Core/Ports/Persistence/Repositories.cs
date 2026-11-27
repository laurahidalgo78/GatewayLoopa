using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Beneficiaries;
using ApiGatewayPay.Core.Domain.Ledger;
using ApiGatewayPay.Core.Domain.Payments;

namespace ApiGatewayPay.Core.Ports.Persistence;

/// <summary>
/// Todas las lecturas expuestas a clientes exigen tenantId (anti-BOLA/IDOR por diseño):
/// no existe un método "GetById" sin tenant salvo los marcados como uso interno de workers.
/// </summary>
public interface IPaymentRepository
{
    Task<Payment?> GetAsync(string tenantId, Guid id, CancellationToken ct);

    Task<Payment?> FindByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct);

    Task<PagedResult<Payment>> ListAsync(string tenantId, PaymentStatus? status, int limit, string? cursor, CancellationToken ct);

    Task<TenantPaymentStats> GetStatsAsync(string tenantId, string currency, Guid beneficiaryId, DateTime dailySince, DateTime velocitySince, CancellationToken ct);

    /// <summary>Uso interno (workers). Nunca exponer a endpoints.</summary>
    Task<Payment?> GetForProcessingAsync(Guid id, CancellationToken ct);

    /// <summary>Uso interno (webhooks/conciliación).</summary>
    Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken ct);

    Task<IReadOnlyList<Payment>> ListSentBetweenAsync(DateTime from, DateTime to, CancellationToken ct);

    Task<IReadOnlyList<Payment>> FindByEndToEndIdsAsync(IReadOnlyCollection<string> endToEndIds, CancellationToken ct);

    void Add(Payment payment);
}

public sealed record TenantPaymentStats(int CountInVelocityWindow, long SumLast24h, IReadOnlyList<long> RecentAmounts, bool BeneficiaryPreviouslyPaid);

public interface IBeneficiaryRepository
{
    Task<Beneficiary?> GetAsync(string tenantId, Guid id, CancellationToken ct);

    Task<bool> ExistsWithFingerprintAsync(string tenantId, string fingerprint, CancellationToken ct);

    Task<IReadOnlyList<Beneficiary>> ListAsync(string tenantId, CancellationToken ct);

    void Add(Beneficiary beneficiary);
}

/// <summary>Libro append-only. No hay operaciones de actualización ni borrado.</summary>
public interface ILedger
{
    Task AppendAsync(LedgerRecord record, CancellationToken ct);
}

/// <summary>Confirma atómicamente todos los cambios (agregados + libro + cola) de la operación.</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Violación de clave única (p. ej. Idempotency-Key concurrente).</summary>
public class DuplicateKeyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Otro proceso modificó el agregado (concurrencia optimista).</summary>
public class ConcurrencyConflictException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record WebhookInboxItem(Guid Id, string Provider, string EventId, string EventType, string? EndToEndId,
    string? State, string? BankReference, string? ReasonCode, DateTime ReceivedAt);

public interface IWebhookInbox
{
    /// <summary>Registra el nonce; false si ya fue usado dentro de la ventana (replay).</summary>
    Task<bool> TryRegisterNonceAsync(string provider, string nonce, DateTime expiresAt, CancellationToken ct);

    Task<bool> ExistsAsync(string provider, string eventId, CancellationToken ct);

    void Add(WebhookInboxItem item);

    Task<WebhookInboxItem?> GetAsync(Guid id, CancellationToken ct);

    Task MarkProcessedAsync(Guid id, string result, CancellationToken ct);
}

public enum DiscrepancySeverity { Low, Medium, High, Critical }

public sealed record ReconciliationDiscrepancy(
    string Type, DiscrepancySeverity Severity, string? EndToEndId, Guid? PaymentId, string Detail);

public sealed record ReconciliationReport(
    Guid Id, DateOnly Date, DateTime StartedAt, DateTime FinishedAt, int InternalCount, int BankCount,
    IReadOnlyList<ReconciliationDiscrepancy> Discrepancies);

public interface IReconciliationStore
{
    void Add(ReconciliationReport report);

    Task<ReconciliationReport?> GetAsync(Guid id, CancellationToken ct);
}
