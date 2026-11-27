using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Beneficiaries;
using ApiGatewayPay.Core.Domain.Ledger;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Core.UseCases.Payments;

namespace ApiGatewayPay.Core.UseCases.Beneficiaries;

public sealed record RegisterBeneficiaryCommand(string DisplayName, string BankCode, AccountType AccountType, string AccountNumber);

public sealed class BeneficiaryService(
    IBeneficiaryRepository beneficiaries,
    IFieldProtector protector,
    ILedger ledger,
    IUnitOfWork uow,
    TimeProvider time)
{
    public async Task<Beneficiary> RegisterAsync(RegisterBeneficiaryCommand cmd, AuditActor actor, CancellationToken ct)
    {
        var purpose = BeneficiaryPurpose.For(actor.TenantId);
        var normalized = new string(cmd.AccountNumber.Where(char.IsAsciiDigit).ToArray());
        var fingerprint = await protector.FingerprintAsync($"{cmd.BankCode}:{normalized}", purpose, ct);
        if (await beneficiaries.ExistsWithFingerprintAsync(actor.TenantId, fingerprint, ct))
            throw AppException.Conflict("beneficiary_exists", "La cuenta ya está registrada como beneficiaria.");

        var cipher = await protector.ProtectAsync(normalized, purpose, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var beneficiary = Beneficiary.Create(Guid.CreateVersion7(), actor, cmd.DisplayName, cmd.BankCode, cmd.AccountType,
            cipher, Masking.Last4(normalized), fingerprint, now);
        beneficiaries.Add(beneficiary);
        await ledger.AppendAsync(Record(beneficiary, "beneficiary.registered", actor, now), ct);
        await uow.SaveChangesAsync(ct);
        return beneficiary;
    }

    public async Task<Beneficiary> ApproveAsync(Guid id, AuditActor actor, CancellationToken ct)
    {
        var beneficiary = await beneficiaries.GetAsync(actor.TenantId, id, ct)
            ?? throw AppException.NotFound("beneficiary_not_found", "Beneficiario no encontrado.");
        var now = time.GetUtcNow().UtcDateTime;
        beneficiary.Approve(actor, now);
        await ledger.AppendAsync(Record(beneficiary, "beneficiary.approved", actor, now), ct);
        await uow.SaveChangesAsync(ct);
        return beneficiary;
    }

    public Task<IReadOnlyList<Beneficiary>> ListAsync(AuditActor actor, CancellationToken ct) =>
        beneficiaries.ListAsync(actor.TenantId, ct);

    private static LedgerRecord Record(Beneficiary b, string eventType, AuditActor actor, DateTime now) => new(
        "beneficiary", b.Id.ToString("N"), b.Version, eventType, actor, now,
        ToStatus: b.Status.ToString(), Detail: $"bank={b.BankCode};last4={b.AccountLast4}");
}
