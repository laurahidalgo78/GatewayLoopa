namespace ApiGatewayPay.Core.Ports.Bank;

/// <summary>
/// Puerto bancario. El núcleo solo conoce estas operaciones abstractas; el formato (REST/JSON, SOAP,
/// ISO 20022), la autenticación, mTLS y la firma son responsabilidad de cada adaptador.
/// </summary>
/// <remarks>
/// Contrato de errores:
/// <list type="bullet">
/// <item><see cref="InitiatePaymentAsync"/> NUNCA lanza por timeouts: devuelve <see cref="BankSubmissionOutcome.Ambiguous"/>.</item>
/// <item><see cref="BankUnavailableException"/>: no se pudo contactar al banco (seguro reintentar consultas).</item>
/// <item><see cref="BankIntegrityException"/>: respuesta con firma inválida o mal formada (no confiable).</item>
/// <item><see cref="WebhookVerificationException"/>: webhook con firma/timestamp inválido.</item>
/// </list>
/// </remarks>
public interface IBankAdapter
{
    string Name { get; }

    Task<BankBalance> GetBalanceAsync(BankAccountRef account, CancellationToken ct);

    Task<IReadOnlyList<BankMovement>> GetMovementsAsync(BankAccountRef account, DateOnly from, DateOnly to, CancellationToken ct);

    Task<BankPaymentSubmission> InitiatePaymentAsync(BankPaymentInstruction instruction, CancellationToken ct);

    Task<BankPaymentStatusInfo> GetPaymentStatusAsync(string endToEndId, CancellationToken ct);

    Task<BankStatement> GetStatementAsync(DateOnly date, CancellationToken ct);

    Task<BankWebhookNotification> VerifyAndParseWebhookAsync(BankWebhookEnvelope envelope, CancellationToken ct);
}

public sealed record BankAccountRef(string BankAccountId, string Currency);

public sealed record BankBalance(string Currency, long AvailableMinor, long LedgerMinor, DateTime AsOf);

public enum BankMovementDirection { Credit, Debit }

public sealed record BankMovement(
    string MovementId, DateTime BookedAt, long AmountMinor, string Currency,
    BankMovementDirection Direction, string? EndToEndId, string Description);

/// <summary>Instrucción de pago. <see cref="Creditor"/> contiene el número de cuenta descifrado solo en memoria.</summary>
public sealed record BankPaymentInstruction(
    string EndToEndId, long AmountMinor, string Currency, string DebtorAccountId,
    BankCreditor Creditor, string RemittanceInformation);

public sealed record BankCreditor(string Name, string BankCode, string AccountNumber, string AccountType)
{
    // Evita que un ToString() accidental (logs, excepciones) exponga el número completo.
    public override string ToString() => $"BankCreditor {{ Name = {Name}, BankCode = {BankCode}, Account = ****{(AccountNumber.Length > 4 ? AccountNumber[^4..] : "")} }}";
}

public enum BankSubmissionOutcome
{
    /// <summary>El banco recibió la orden (puede quedar pendiente de liquidación).</summary>
    Accepted,
    /// <summary>Rechazo definitivo: es seguro marcar el pago como fallido.</summary>
    Rejected,
    /// <summary>No sabemos si el banco la procesó (timeout, 5xx, conexión cortada). Prohibido reenviar sin consultar.</summary>
    Ambiguous,
}

public sealed record BankPaymentSubmission(BankSubmissionOutcome Outcome, string? BankReference, string? ReasonCode);

public enum BankPaymentState { Pending, Settled, Rejected, Returned, NotFound }

public sealed record BankPaymentStatusInfo(
    string EndToEndId, BankPaymentState State, string? BankReference, string? ReasonCode,
    long? AmountMinor = null, string? Currency = null);

public sealed record BankStatement(DateOnly Date, IReadOnlyList<BankStatementEntry> Entries);

public sealed record BankStatementEntry(
    string? EndToEndId, string BankReference, string DebtorAccountId, long AmountMinor, string Currency, BankPaymentState State);

public sealed record BankWebhookEnvelope(IReadOnlyDictionary<string, string> Headers, byte[] Body);

public sealed record BankWebhookNotification(
    string EventId, string EventType, string Nonce, DateTime Timestamp,
    string? EndToEndId, BankPaymentState? State, string? BankReference, string? ReasonCode);

public class BankUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public class BankIntegrityException(string message) : Exception(message);

public class WebhookVerificationException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
