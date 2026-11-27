using System.Collections.Concurrent;
using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Infrastructure.Bank.Mock;

public sealed class MockBankPayment
{
    public required string EndToEndId { get; init; }
    public required string BankReference { get; init; }
    public required long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public required string DebtorAccountId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime SettleAt { get; init; }
    public BankPaymentState State { get; set; }
    public string? ReasonCode { get; set; }
    public bool WebhookSent { get; set; }
}

/// <summary>Estado en memoria del banco simulado (singleton). Expuesto para pruebas de fallos y conciliación.</summary>
public sealed class MockBankState
{
    public ConcurrentDictionary<string, MockBankPayment> Payments { get; } = new();
    public ConcurrentDictionary<string, int> InitiationCalls { get; } = new();
    /// <summary>Movimientos que el banco reporta sin que los hayamos originado (simulación de fraude/error).</summary>
    public ConcurrentBag<BankStatementEntry> InjectedStatementEntries { get; } = new();

    public int InitiationsFor(string endToEndId) => InitiationCalls.TryGetValue(endToEndId, out var n) ? n : 0;
}
