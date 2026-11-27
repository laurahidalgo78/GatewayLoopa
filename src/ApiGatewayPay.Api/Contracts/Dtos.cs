using System.ComponentModel.DataAnnotations;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Beneficiaries;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Api.Contracts;

/// <summary>
/// DTOs explícitos de entrada/salida (nunca entidades): evita asignación masiva (BOPLA) y exposición
/// excesiva de datos. Los campos desconocidos se rechazan en la deserialización.
/// </summary>
public static class Patterns
{
    /// <summary>Texto seguro para mensajes bancarios (subset compatible con ISO 20022/SWIFT, sin controles ni marcado).</summary>
    public const string SafeText = @"^[\p{L}\p{N} .,:;'\-_/#()]+$";
    public const string IdempotencyKey = "^[A-Za-z0-9_-]{16,64}$";

    public static bool IsValidIdempotencyKey(string? key) =>
        key is not null && System.Text.RegularExpressions.Regex.IsMatch(key, IdempotencyKey, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
}

public sealed record CreatePaymentRequest
{
    [Required] public Guid? BeneficiaryId { get; init; }
    [Range(1, 99_999_999_999_999)] public long AmountMinor { get; init; }
    [Required, RegularExpression("^[A-Z]{3}$")] public string Currency { get; init; } = default!;
    [Required, StringLength(140, MinimumLength = 1), RegularExpression(Patterns.SafeText)] public string Reference { get; init; } = default!;
}

public sealed record RejectPaymentRequest
{
    [Required, RegularExpression("^[a-z_]{3,32}$")] public string ReasonCode { get; init; } = default!;
}

public sealed record PaymentResponse(
    Guid Id, PaymentStatus Status, long AmountMinor, string Currency, Guid BeneficiaryId, string Reference,
    bool RequiresApproval, IReadOnlyList<string> RiskFlags, string? BankReference, string? FailureCode,
    DateTime CreatedAt, DateTime UpdatedAt)
{
    public static PaymentResponse From(Payment p) => new(p.Id, p.Status, p.AmountMinor, p.Currency, p.BeneficiaryId, p.Reference,
        p.RequiresApproval, p.RiskFlags?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [], p.BankReference, p.FailureCode, p.CreatedAt, p.UpdatedAt);
}

public sealed record PaymentListResponse(IReadOnlyList<PaymentResponse> Items, string? NextCursor);

public sealed record RegisterBeneficiaryRequest
{
    [Required, StringLength(140, MinimumLength = 2), RegularExpression(Patterns.SafeText)] public string DisplayName { get; init; } = default!;
    [Required, RegularExpression("^[0-9A-Z]{3,11}$")] public string BankCode { get; init; } = default!;
    [Required] public AccountType? AccountType { get; init; }
    [Required, RegularExpression("^[0-9]{6,20}$")] public string AccountNumber { get; init; } = default!;
}

public sealed record BeneficiaryResponse(Guid Id, string DisplayName, string BankCode, AccountType AccountType, string AccountNumberMasked, BeneficiaryStatus Status, DateTime CreatedAt)
{
    public static BeneficiaryResponse From(Beneficiary b) =>
        new(b.Id, b.DisplayName, b.BankCode, b.AccountType, "******" + b.AccountLast4, b.Status, b.CreatedAt);
}

public sealed record AccountResponse(string AccountRef, string DisplayName, string Currency, string AccountNumberMasked)
{
    public static AccountResponse From(BackendAccount a) => new(a.AccountRef, a.DisplayName, a.Currency, Masking.MaskAccount(a.BankAccountId));
}

public sealed record BalanceResponse(string AccountRef, string Currency, long AvailableMinor, long LedgerMinor, DateTime AsOf);

public sealed record MovementResponse(string Id, DateTime BookedAt, long AmountMinor, string Currency, BankMovementDirection Direction, string Description);

public sealed record KillSwitchRequest
{
    [Required] public bool? Active { get; init; }
    [Required, StringLength(200, MinimumLength = 5), RegularExpression(Patterns.SafeText)] public string Reason { get; init; } = default!;
}

public sealed record CreateApiKeyRequest
{
    [Required, StringLength(64, MinimumLength = 3), RegularExpression(Patterns.SafeText)] public string Name { get; init; } = default!;
    [Required, MinLength(1), MaxLength(10)] public string[] Scopes { get; init; } = [];
    [Range(1, 365)] public int ExpiresInDays { get; init; } = 90;
}

public sealed record ReconciliationRequest
{
    [Required] public DateOnly? Date { get; init; }
}
