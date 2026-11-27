using ApiGatewayPay.Core.Common;

namespace ApiGatewayPay.Core.Domain.Ledger;

/// <summary>
/// Evento a anotar en el libro inmutable. La infraestructura añade la secuencia global y el
/// encadenamiento HMAC (PreviousHash/Hash) que hace detectable cualquier alteración.
/// </summary>
public sealed record LedgerRecord(
    string AggregateType,
    string AggregateId,
    int AggregateSequence,
    string EventType,
    AuditActor Actor,
    DateTime OccurredAt,
    string? FromStatus = null,
    string? ToStatus = null,
    long? AmountMinor = null,
    string? Currency = null,
    string? Detail = null);
