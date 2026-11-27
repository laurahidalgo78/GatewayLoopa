using ApiGatewayPay.Core.Ports.External;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.Bank.Tls;

/// <summary>
/// Recarga periódica de certificados mTLS desde el gestor de secretos (rotación sin redespliegue) y alertas
/// de expiración (cliente y servidor observado). La emisión/renovación en sí la hace la PKI
/// (Vault PKI, cert-manager, la CA del banco); este proceso garantiza que el cambio se tome y se vigile.
/// </summary>
public sealed class CertificateMonitor(CertificateStore store, IAlertNotifier alerts, TimeProvider time, ILogger<CertificateMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await CheckAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        foreach (var (integration, options) in store.Registrations.ToList())
        {
            try { await store.ReloadClientCertificateAsync(integration, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError("No se pudo recargar el certificado mTLS de {Integration}: {ErrorType}", integration, ex.GetType().Name);
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var cert in store.Snapshot())
        {
            var days = (cert.NotAfter - now).TotalDays;
            Telemetry.AppMetrics.CertificateExpiry($"{cert.Integration}:{cert.Kind}", days);
            var opts = store.Registrations.FirstOrDefault(r => r.Integration == cert.Integration).Options;
            var (warn, critical) = (opts?.ExpiryWarningDays ?? 30, opts?.ExpiryCriticalDays ?? 7);
            if (days <= warn)
                await alerts.NotifyAsync(new Alert(days <= critical ? AlertSeverity.Critical : AlertSeverity.Warning, "certificate_expiring",
                    $"Certificado {cert.Kind} de {cert.Integration} vence en {Math.Floor(days)} días.",
                    new Dictionary<string, string> { ["integration"] = cert.Integration, ["thumbprint"] = cert.Thumbprint }), ct);
        }
    }
}
