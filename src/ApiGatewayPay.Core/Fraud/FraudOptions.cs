namespace ApiGatewayPay.Core.Fraud;

public sealed class FraudOptions
{
    public const string Section = "Fraud";

    /// <summary>Límites por moneda. Una moneda sin límites configurados se bloquea (deny by default).</summary>
    public Dictionary<string, CurrencyLimits> Limits { get; set; } = new();

    public int VelocityMaxPayments { get; set; } = 10;
    public int VelocityWindowSeconds { get; set; } = 60;

    /// <summary>Un monto mayor que Factor × promedio histórico se envía a aprobación manual.</summary>
    public double AnomalyFactor { get; set; } = 5;
    public int AnomalyMinHistory { get; set; } = 5;

    /// <summary>Primer pago a un beneficiario por encima de este monto requiere aprobación.</summary>
    public bool ReviewFirstPaymentToBeneficiary { get; set; } = true;
}

public sealed class CurrencyLimits
{
    public long MaxPerTransactionMinor { get; set; }
    public long DailyMaxMinor { get; set; }
    /// <summary>Desde este monto se exige doble aprobación (maker-checker).</summary>
    public long ApprovalThresholdMinor { get; set; }
    public long FirstPaymentReviewMinor { get; set; }
}
