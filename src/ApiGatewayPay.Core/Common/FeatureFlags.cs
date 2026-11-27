namespace ApiGatewayPay.Core.Common;

/// <summary>
/// Interruptores por integración (planificados, vía configuración). No confundir con el kill switch,
/// que es la palanca de emergencia en runtime ante incidentes.
/// </summary>
public sealed class FeatureFlags
{
    public const string Section = "Features";

    public bool Payments { get; set; } = true;
    public bool AccountsQuery { get; set; } = true;
    public bool Webhooks { get; set; } = true;
    public bool Reconciliation { get; set; } = true;
    public bool BackendNotifications { get; set; } = true;
}
