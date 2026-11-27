using ApiGatewayPay.Core.Ports.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Backend;

/// <summary>Backend simulado en memoria. Funcional de punta a punta; prohibido en producción (EnvironmentGuard).</summary>
public sealed class MockBackendAdapter(IOptionsMonitor<BackendOptions> options, ILogger<MockBackendAdapter> logger) : IBackendGateway
{
    public string Name => "mock-backend";

    public Task<IReadOnlyList<BackendAccount>> GetTenantAccountsAsync(string tenantId, CancellationToken ct)
    {
        IReadOnlyList<BackendAccount> accounts = options.CurrentValue.Mock.Tenants.TryGetValue(tenantId, out var list)
            ? list.Select(a => new BackendAccount(a.AccountRef, a.BankAccountId, a.Currency, a.DisplayName)).ToList()
            : [];
        return Task.FromResult(accounts);
    }

    public async Task<BackendPaymentDecision> AuthorizePaymentAsync(BackendPaymentRequest request, CancellationToken ct)
    {
        var o = options.CurrentValue.Mock;
        if (request.Reference.Contains(o.DeclineReferencesContaining, StringComparison.OrdinalIgnoreCase))
            return new BackendPaymentDecision(false, null, "business_rule");
        var accounts = await GetTenantAccountsAsync(request.TenantId, ct);
        var debtor = accounts.FirstOrDefault(a => a.Currency == request.Currency);
        return debtor is null
            ? new BackendPaymentDecision(false, null, "no_account_for_currency")
            : new BackendPaymentDecision(true, debtor.AccountRef, null);
    }

    public Task NotifyPaymentEventAsync(BackendPaymentEvent paymentEvent, CancellationToken ct)
    {
        logger.LogInformation("[mock-backend] Pago {PaymentId} → {Status}", paymentEvent.PaymentId, paymentEvent.Status);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
}
