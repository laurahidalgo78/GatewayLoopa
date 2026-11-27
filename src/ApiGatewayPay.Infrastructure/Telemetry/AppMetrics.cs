using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ApiGatewayPay.Infrastructure.Telemetry;

/// <summary>Métricas de negocio y seguridad (exportadas vía OpenTelemetry). Sin datos personales en etiquetas.</summary>
public static class AppMetrics
{
    public const string MeterName = "ApiGatewayPay";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> PaymentTransitions = Meter.CreateCounter<long>("agp.payments.transitions");
    private static readonly Counter<long> OutboundCalls = Meter.CreateCounter<long>("agp.outbound.calls");
    private static readonly Histogram<double> OutboundLatency = Meter.CreateHistogram<double>("agp.outbound.duration", "ms");
    private static readonly Counter<long> CircuitOpens = Meter.CreateCounter<long>("agp.outbound.circuit_opened");
    private static readonly Counter<long> WebhookRejections = Meter.CreateCounter<long>("agp.webhooks.rejected");
    private static readonly Counter<long> AuthFailures = Meter.CreateCounter<long>("agp.auth.failures");
    private static readonly Counter<long> Alerts = Meter.CreateCounter<long>("agp.alerts");
    private static readonly Counter<long> JobsProcessed = Meter.CreateCounter<long>("agp.jobs.processed");
    private static readonly Dictionary<string, double> CertificateDays = new();
    private static int _killSwitchActive;

    static AppMetrics()
    {
        Meter.CreateObservableGauge("agp.certificates.days_to_expiry", () =>
        {
            lock (CertificateDays)
                return CertificateDays.Select(kv => new Measurement<double>(kv.Value, new KeyValuePair<string, object?>("certificate", kv.Key))).ToArray();
        });
        Meter.CreateObservableGauge("agp.bank.kill_switch_active", () => Volatile.Read(ref _killSwitchActive));
    }

    public static void PaymentTransition(string status) => PaymentTransitions.Add(1, new KeyValuePair<string, object?>("status", status));

    public static void OutboundCall(string integration, int status, TimeSpan elapsed)
    {
        var tags = new TagList { { "integration", integration }, { "status", status } };
        OutboundCalls.Add(1, tags);
        OutboundLatency.Record(elapsed.TotalMilliseconds, tags);
    }

    public static void CircuitOpened(string integration) => CircuitOpens.Add(1, new KeyValuePair<string, object?>("integration", integration));
    public static void WebhookRejected(string reason) => WebhookRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public static void AuthFailure(string reason) => AuthFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public static void Alert(string code, string severity) => Alerts.Add(1, new TagList { { "code", code }, { "severity", severity } });
    public static void JobProcessed(string type, string outcome) => JobsProcessed.Add(1, new TagList { { "type", type }, { "outcome", outcome } });
    public static void KillSwitch(bool active) => Volatile.Write(ref _killSwitchActive, active ? 1 : 0);

    public static void CertificateExpiry(string certificate, double days)
    {
        lock (CertificateDays) CertificateDays[certificate] = days;
    }
}
