using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.Persistence;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Core.Fraud;

public enum FraudOutcome { Allow, RequireApproval, Block }

public sealed record FraudDecision(FraudOutcome Outcome, IReadOnlyList<string> Reasons);

/// <summary>
/// Reglas antifraude deterministas y auditables. Bloquea lo que excede límites duros y deriva a
/// aprobación humana lo anómalo. Punto de extensión natural para un motor externo (vía IExternalService).
/// </summary>
public sealed class FraudEngine(IOptionsMonitor<FraudOptions> options)
{
    public FraudDecision Evaluate(Money amount, TenantPaymentStats stats)
    {
        var o = options.CurrentValue;
        if (!o.Limits.TryGetValue(amount.Currency, out var limits))
            return new(FraudOutcome.Block, ["currency_not_allowed"]);

        var block = new List<string>();
        if (amount.AmountMinor > limits.MaxPerTransactionMinor) block.Add("per_transaction_limit");
        if (stats.SumLast24h + amount.AmountMinor > limits.DailyMaxMinor) block.Add("daily_limit");
        if (stats.CountInVelocityWindow >= o.VelocityMaxPayments) block.Add("velocity_limit");
        if (block.Count > 0) return new(FraudOutcome.Block, block);

        var review = new List<string>();
        if (amount.AmountMinor >= limits.ApprovalThresholdMinor) review.Add("high_amount");
        if (stats.RecentAmounts.Count >= o.AnomalyMinHistory)
        {
            var avg = stats.RecentAmounts.Average();
            if (amount.AmountMinor > avg * o.AnomalyFactor) review.Add("amount_anomaly");
        }
        if (o.ReviewFirstPaymentToBeneficiary && !stats.BeneficiaryPreviouslyPaid &&
            amount.AmountMinor >= limits.FirstPaymentReviewMinor)
            review.Add("first_payment_to_beneficiary");

        return review.Count > 0 ? new(FraudOutcome.RequireApproval, review) : new(FraudOutcome.Allow, []);
    }
}
