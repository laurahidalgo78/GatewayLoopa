using ApiGatewayPay.Core.Common;

namespace ApiGatewayPay.Core.Domain.Beneficiaries;

public enum BeneficiaryStatus
{
    PendingApproval,
    Active,
    Disabled,
}

public enum AccountType
{
    Savings,
    Checking,
    Electronic,
}

/// <summary>
/// Cuenta destino en lista blanca. Solo se paga a beneficiarios Active, aprobados por un segundo usuario.
/// El número de cuenta se guarda cifrado (AES-GCM) y se expone únicamente con los últimos 4 dígitos.
/// </summary>
public sealed class Beneficiary
{
    private Beneficiary() { }

    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string BankCode { get; private set; } = default!;
    public AccountType AccountType { get; private set; }
    public string AccountNumberCipher { get; private set; } = default!;
    public string AccountLast4 { get; private set; } = default!;
    /// <summary>HMAC del número de cuenta: permite detectar duplicados sin descifrar.</summary>
    public string AccountFingerprint { get; private set; } = default!;
    public BeneficiaryStatus Status { get; private set; }
    public string CreatedBy { get; private set; } = default!;
    public string? ApprovedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? ActivatedAt { get; private set; }
    public int Version { get; private set; }

    public static Beneficiary Create(
        Guid id, AuditActor actor, string displayName, string bankCode, AccountType accountType,
        string accountNumberCipher, string accountLast4, string accountFingerprint, DateTime now) => new()
    {
        Id = id,
        TenantId = actor.TenantId,
        DisplayName = displayName,
        BankCode = bankCode,
        AccountType = accountType,
        AccountNumberCipher = accountNumberCipher,
        AccountLast4 = accountLast4,
        AccountFingerprint = accountFingerprint,
        Status = BeneficiaryStatus.PendingApproval,
        CreatedBy = actor.ActorId,
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1,
    };

    public void Approve(AuditActor approver, DateTime now)
    {
        if (Status != BeneficiaryStatus.PendingApproval)
            throw AppException.Conflict("invalid_state_transition", "El beneficiario no está pendiente de aprobación.");
        if (!approver.IsHuman || !approver.StrongAuthentication)
            throw AppException.Forbidden("step_up_required", "La aprobación requiere un usuario con MFA reciente.");
        if (string.Equals(approver.ActorId, CreatedBy, StringComparison.Ordinal))
            throw AppException.Forbidden("maker_checker_violation", "Quien registra el beneficiario no puede aprobarlo.");
        Status = BeneficiaryStatus.Active;
        ApprovedBy = approver.ActorId;
        ActivatedAt = now;
        UpdatedAt = now;
        Version++;
    }

    public void Disable(DateTime now)
    {
        Status = BeneficiaryStatus.Disabled;
        UpdatedAt = now;
        Version++;
    }
}
