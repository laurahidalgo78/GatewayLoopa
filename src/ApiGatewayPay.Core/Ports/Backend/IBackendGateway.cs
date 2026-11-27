namespace ApiGatewayPay.Core.Ports.Backend;

/// <summary>
/// Puerto hacia el backend de negocio (aún no definido). Es la fuente de verdad sobre qué cuentas
/// pertenecen a cada tenant y quién decide reglas de negocio propias antes de mover dinero.
/// </summary>
public interface IBackendGateway
{
    string Name { get; }

    /// <summary>Cuentas operativas del tenant. Se usa para autorización por atributos (anti-BOLA).</summary>
    Task<IReadOnlyList<BackendAccount>> GetTenantAccountsAsync(string tenantId, CancellationToken ct);

    /// <summary>Validación de negocio del backend y selección de la cuenta origen.</summary>
    Task<BackendPaymentDecision> AuthorizePaymentAsync(BackendPaymentRequest request, CancellationToken ct);

    /// <summary>Notificación asíncrona (vía cola) de cambios de estado.</summary>
    Task NotifyPaymentEventAsync(BackendPaymentEvent paymentEvent, CancellationToken ct);

    Task<bool> PingAsync(CancellationToken ct);
}

/// <param name="AccountRef">Alias opaco expuesto a clientes (nunca el número real).</param>
/// <param name="BankAccountId">Identificador de la cuenta en el banco. No se devuelve completo al cliente.</param>
public sealed record BackendAccount(string AccountRef, string BankAccountId, string Currency, string DisplayName);

public sealed record BackendPaymentRequest(
    Guid PaymentId, string TenantId, string RequestedBy, long AmountMinor, string Currency,
    Guid BeneficiaryId, string Reference);

public sealed record BackendPaymentDecision(bool Approved, string? DebtorAccountRef, string? ReasonCode);

public sealed record BackendPaymentEvent(
    Guid PaymentId, string TenantId, string Status, string? BankReference, string? FailureCode, DateTime OccurredAt);

public class BackendUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
