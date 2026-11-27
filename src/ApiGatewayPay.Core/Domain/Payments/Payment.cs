using ApiGatewayPay.Core.Common;

namespace ApiGatewayPay.Core.Domain.Payments;

public sealed record StatusChange(PaymentStatus? From, PaymentStatus To, string EventType, string? Reason);

/// <summary>
/// Agregado de pago. Cada mutación incrementa <see cref="Version"/> (token de concurrencia optimista),
/// que además es la secuencia de la cadena de auditoría del agregado.
/// </summary>
public sealed class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = default!;
    public string CreatedBy { get; private set; } = default!;
    public ActorType CreatedByType { get; private set; }
    public Guid BeneficiaryId { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = default!;
    public string Reference { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;

    /// <summary>Identificador enviado al banco. Se reutiliza en reenvíos para que el banco deduplique.</summary>
    public string EndToEndId { get; private set; } = default!;
    public string? DebtorAccountRef { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? BankReference { get; private set; }
    public string? FailureCode { get; private set; }
    public bool RequiresApproval { get; private set; }
    public string? RiskFlags { get; private set; }
    public string? ApprovedBy { get; private set; }
    public int SubmissionAttempts { get; private set; }
    public int InquiryAttempts { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public Money Amount => new(AmountMinor, Currency);

    public static (Payment Payment, StatusChange Change) Create(
        Guid id, AuditActor actor, Guid beneficiaryId, Money amount, string reference,
        string idempotencyKey, string requestHash, DateTime now)
    {
        var payment = new Payment
        {
            Id = id,
            TenantId = actor.TenantId,
            CreatedBy = actor.ActorId,
            CreatedByType = actor.Type,
            BeneficiaryId = beneficiaryId,
            AmountMinor = amount.AmountMinor,
            Currency = amount.Currency,
            Reference = reference,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            // 32 caracteres hex: compatible con el límite de 35 de ISO 20022 EndToEndId.
            EndToEndId = id.ToString("N"),
            Status = PaymentStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        return (payment, new StatusChange(null, PaymentStatus.Created, "payment.created", null));
    }

    public void AssignDebtorAccount(string accountRef) => DebtorAccountRef = accountRef;

    public StatusChange RequireApproval(IEnumerable<string> riskFlags, DateTime now)
    {
        RequiresApproval = true;
        RiskFlags = string.Join(',', riskFlags);
        return Move(PaymentStatus.PendingApproval, "payment.approval_required", RiskFlags, now);
    }

    public StatusChange AutoApprove(DateTime now)
    {
        ApprovedBy = "system";
        return Move(PaymentStatus.Approved, "payment.auto_approved", null, now);
    }

    /// <summary>Maker-checker: quien crea no puede aprobar, y solo un humano con MFA reciente aprueba.</summary>
    public StatusChange Approve(AuditActor approver, DateTime now)
    {
        EnsureChecker(approver);
        ApprovedBy = approver.ActorId;
        return Move(PaymentStatus.Approved, "payment.approved", null, now);
    }

    public StatusChange Reject(AuditActor actor, string reasonCode, DateTime now)
    {
        if (Status == PaymentStatus.PendingApproval) EnsureChecker(actor);
        FailureCode = reasonCode;
        CompletedAt = now;
        return Move(PaymentStatus.Rejected, "payment.rejected", reasonCode, now);
    }

    public StatusChange MarkSent(DateTime now)
    {
        SubmissionAttempts++;
        SentAt = now;
        return Move(PaymentStatus.Sent, "payment.sent", $"attempt={SubmissionAttempts}", now);
    }

    public StatusChange RecordBankAcceptance(string? bankReference, DateTime now)
    {
        if (Status != PaymentStatus.Sent)
            throw AppException.Conflict("invalid_state_transition", "Solo un pago enviado puede registrar aceptación del banco.");
        BankReference = bankReference;
        Touch(now);
        return new StatusChange(Status, Status, "payment.bank_accepted", bankReference);
    }

    public StatusChange MarkConfirmed(string? bankReference, DateTime now)
    {
        BankReference = bankReference ?? BankReference;
        CompletedAt = now;
        return Move(PaymentStatus.Confirmed, "payment.confirmed", BankReference, now);
    }

    public StatusChange MarkFailed(string reasonCode, DateTime now)
    {
        FailureCode = reasonCode;
        CompletedAt = now;
        return Move(PaymentStatus.Failed, "payment.failed", reasonCode, now);
    }

    public StatusChange MarkUnknown(string reason, DateTime now) =>
        Move(PaymentStatus.Unknown, "payment.outcome_unknown", reason, now);

    public StatusChange RequeueAfterNotFound(DateTime now) =>
        Move(PaymentStatus.Approved, "payment.requeued_not_found", $"attempts={SubmissionAttempts}", now);

    public StatusChange MarkReversed(string reasonCode, DateTime now)
    {
        FailureCode = reasonCode;
        return Move(PaymentStatus.Reversed, "payment.reversed", reasonCode, now);
    }

    public StatusChange RecordInquiry(string result, DateTime now)
    {
        InquiryAttempts++;
        Touch(now);
        return new StatusChange(Status, Status, "payment.status_inquiry", result);
    }

    private void EnsureChecker(AuditActor actor)
    {
        if (!actor.IsHuman || !actor.StrongAuthentication)
            throw AppException.Forbidden("step_up_required", "La aprobación requiere un usuario con MFA reciente.");
        if (string.Equals(actor.ActorId, CreatedBy, StringComparison.Ordinal))
            throw AppException.Forbidden("maker_checker_violation", "Quien crea la operación no puede aprobarla.");
        if (!string.Equals(actor.TenantId, TenantId, StringComparison.Ordinal))
            throw AppException.NotFound("payment_not_found", "Pago no encontrado.");
    }

    private StatusChange Move(PaymentStatus to, string eventType, string? reason, DateTime now)
    {
        PaymentStateMachine.EnsureCanTransition(Status, to);
        var from = Status;
        Status = to;
        Touch(now);
        return new StatusChange(from, to, eventType, reason);
    }

    private void Touch(DateTime now)
    {
        UpdatedAt = now;
        Version++;
    }
}
