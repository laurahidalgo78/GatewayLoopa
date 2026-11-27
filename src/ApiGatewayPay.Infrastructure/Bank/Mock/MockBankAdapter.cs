using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Infrastructure.Bank.Real.Codecs;
using ApiGatewayPay.Infrastructure.Bank.Webhooks;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Bank.Mock;

/// <summary>
/// Sandbox bancario simulado con fallos deterministas según los dos últimos dígitos del monto (en unidades mínimas):
/// <list type="bullet">
/// <item>..13 → rechazo definitivo (AC04, cuenta cerrada).</item>
/// <item>..99 → el banco PROCESA la orden pero la respuesta "se pierde" (timeout ambiguo) en el primer intento.</item>
/// <item>..98 → la orden NO llega al banco y la respuesta es un timeout (ambiguo) en el primer intento.</item>
/// <item>cualquier otro → aceptado; se liquida tras SettlementDelaySeconds.</item>
/// </list>
/// Aplica idempotencia del lado banco por EndToEndId, como un banco real.
/// </summary>
public sealed class MockBankAdapter(
    MockBankState state, WebhookVerifier webhookVerifier, IOptionsMonitor<BankOptions> options, TimeProvider time) : IBankAdapter
{
    public string Name => options.CurrentValue.Name;

    private MockBankOptions O => options.CurrentValue.Mock;

    public async Task<BankBalance> GetBalanceAsync(BankAccountRef account, CancellationToken ct)
    {
        await LatencyAsync(ct);
        Settle();
        var opening = O.OpeningBalances.GetValueOrDefault(account.BankAccountId, 0);
        var debits = state.Payments.Values.Where(p => p.DebtorAccountId == account.BankAccountId && p.State == BankPaymentState.Settled && p.Currency == account.Currency).Sum(p => p.AmountMinor);
        var pending = state.Payments.Values.Where(p => p.DebtorAccountId == account.BankAccountId && p.State == BankPaymentState.Pending && p.Currency == account.Currency).Sum(p => p.AmountMinor);
        return new BankBalance(account.Currency, opening - debits - pending, opening - debits, time.GetUtcNow().UtcDateTime);
    }

    public async Task<IReadOnlyList<BankMovement>> GetMovementsAsync(BankAccountRef account, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await LatencyAsync(ct);
        Settle();
        return state.Payments.Values
            .Where(p => p.DebtorAccountId == account.BankAccountId && p.State == BankPaymentState.Settled)
            .Where(p => DateOnly.FromDateTime(p.SettleAt) >= from && DateOnly.FromDateTime(p.SettleAt) <= to)
            .OrderBy(p => p.SettleAt)
            .Select(p => new BankMovement(p.BankReference, p.SettleAt, p.AmountMinor, p.Currency, BankMovementDirection.Debit, p.EndToEndId, "Transferencia"))
            .ToList();
    }

    public async Task<BankPaymentSubmission> InitiatePaymentAsync(BankPaymentInstruction i, CancellationToken ct)
    {
        await LatencyAsync(ct);
        var attempt = state.InitiationCalls.AddOrUpdate(i.EndToEndId, 1, (_, n) => n + 1);
        if (state.Payments.TryGetValue(i.EndToEndId, out var existing))
            return new BankPaymentSubmission(existing.State == BankPaymentState.Rejected ? BankSubmissionOutcome.Rejected : BankSubmissionOutcome.Accepted,
                existing.BankReference, existing.ReasonCode);

        var now = time.GetUtcNow().UtcDateTime;
        var lastTwo = i.AmountMinor % 100;
        if (lastTwo == 98 && attempt == 1)
            return new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "timeout");

        var payment = new MockBankPayment
        {
            EndToEndId = i.EndToEndId,
            BankReference = "MB" + Guid.NewGuid().ToString("N")[..14].ToUpperInvariant(),
            AmountMinor = i.AmountMinor,
            Currency = i.Currency,
            DebtorAccountId = i.DebtorAccountId,
            CreatedAt = now,
            SettleAt = now.AddSeconds(O.SettlementDelaySeconds),
            State = lastTwo == 13 ? BankPaymentState.Rejected : BankPaymentState.Pending,
            ReasonCode = lastTwo == 13 ? "AC04" : null,
        };
        state.Payments[i.EndToEndId] = payment;

        if (lastTwo == 99 && attempt == 1)
            return new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "timeout");
        return payment.State == BankPaymentState.Rejected
            ? new BankPaymentSubmission(BankSubmissionOutcome.Rejected, payment.BankReference, payment.ReasonCode)
            : new BankPaymentSubmission(BankSubmissionOutcome.Accepted, payment.BankReference, null);
    }

    public async Task<BankPaymentStatusInfo> GetPaymentStatusAsync(string endToEndId, CancellationToken ct)
    {
        await LatencyAsync(ct);
        Settle();
        return state.Payments.TryGetValue(endToEndId, out var p)
            ? new BankPaymentStatusInfo(endToEndId, p.State, p.BankReference, p.ReasonCode, p.AmountMinor, p.Currency)
            : new BankPaymentStatusInfo(endToEndId, BankPaymentState.NotFound, null, null);
    }

    public async Task<BankStatement> GetStatementAsync(DateOnly date, CancellationToken ct)
    {
        await LatencyAsync(ct);
        Settle();
        var entries = state.Payments.Values
            .Where(p => DateOnly.FromDateTime(p.CreatedAt) == date && p.State != BankPaymentState.Pending)
            .Select(p => new BankStatementEntry(p.EndToEndId, p.BankReference, p.DebtorAccountId, p.AmountMinor, p.Currency, p.State))
            .Concat(state.InjectedStatementEntries)
            .ToList();
        return new BankStatement(date, entries);
    }

    public async Task<BankWebhookNotification> VerifyAndParseWebhookAsync(BankWebhookEnvelope envelope, CancellationToken ct)
    {
        var (timestamp, nonce) = await webhookVerifier.VerifyAsync(envelope, ct);
        var payload = RestJsonBankCodec.ParseWebhookBody(envelope.Body);
        return new BankWebhookNotification(payload.EventId, payload.Type, nonce, timestamp, payload.EndToEndId, payload.State, payload.BankReference, payload.ReasonCode);
    }

    internal void Settle()
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var p in state.Payments.Values.Where(p => p.State == BankPaymentState.Pending && p.SettleAt <= now))
            p.State = BankPaymentState.Settled;
    }

    private Task LatencyAsync(CancellationToken ct) =>
        O.LatencyMilliseconds > 0 ? Task.Delay(TimeSpan.FromMilliseconds(O.LatencyMilliseconds), time, ct) : Task.CompletedTask;
}
