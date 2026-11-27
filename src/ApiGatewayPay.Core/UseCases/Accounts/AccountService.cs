using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Security;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Core.UseCases.Accounts;

/// <summary>
/// Consultas de saldo y movimientos. La pertenencia de la cuenta al tenant la decide el backend
/// (ABAC): el cliente solo envía un alias opaco y nunca el número real.
/// </summary>
public sealed class AccountService(
    IBackendGateway backend, IBankAdapter bank, IKillSwitch killSwitch, IOptionsMonitor<FeatureFlags> features)
{
    public const int MaxMovementRangeDays = 31;

    public Task<IReadOnlyList<BackendAccount>> ListAsync(AuditActor actor, CancellationToken ct) =>
        backend.GetTenantAccountsAsync(actor.TenantId, ct);

    public async Task<BankBalance> GetBalanceAsync(string accountRef, AuditActor actor, CancellationToken ct)
    {
        var account = await ResolveAsync(accountRef, actor, ct);
        return await bank.GetBalanceAsync(new BankAccountRef(account.BankAccountId, account.Currency), ct);
    }

    public async Task<IReadOnlyList<BankMovement>> GetMovementsAsync(string accountRef, DateOnly from, DateOnly to, AuditActor actor, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > MaxMovementRangeDays)
            throw AppException.Validation("invalid_date_range", $"El rango debe ser de máximo {MaxMovementRangeDays} días.");
        var account = await ResolveAsync(accountRef, actor, ct);
        return await bank.GetMovementsAsync(new BankAccountRef(account.BankAccountId, account.Currency), from, to, ct);
    }

    private async Task<BackendAccount> ResolveAsync(string accountRef, AuditActor actor, CancellationToken ct)
    {
        if (!features.CurrentValue.AccountsQuery)
            throw AppException.Unavailable("accounts_disabled", "Las consultas de cuentas están deshabilitadas.");
        if ((await killSwitch.GetAsync(ct)).Active)
            throw AppException.Unavailable("bank_connection_disabled", "La conexión bancaria está suspendida.");
        var accounts = await backend.GetTenantAccountsAsync(actor.TenantId, ct);
        return accounts.FirstOrDefault(a => string.Equals(a.AccountRef, accountRef, StringComparison.Ordinal))
            ?? throw AppException.NotFound("account_not_found", "Cuenta no encontrada.");
    }
}
