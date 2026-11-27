using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Bank.Tls;
using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApiGatewayPay.Infrastructure.Platform;

public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("database");
}

/// <summary>Con el kill switch activo la instancia sigue "lista" (atiende consultas internas) pero degradada.</summary>
public sealed class KillSwitchHealthCheck(IKillSwitch killSwitch) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        (await killSwitch.GetAsync(ct)).Active ? HealthCheckResult.Degraded("bank_kill_switch_active") : HealthCheckResult.Healthy();
}

public sealed class BackendHealthCheck(IBackendGateway backend) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await backend.PingAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("backend_unreachable");
}

public sealed class CertificateHealthCheck(CertificateStore store, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var certs = store.Snapshot();
        if (certs.Any(c => c.NotAfter <= now)) return Task.FromResult(HealthCheckResult.Unhealthy("certificate_expired"));
        if (certs.Any(c => (c.NotAfter - now).TotalDays <= 7)) return Task.FromResult(HealthCheckResult.Degraded("certificate_expiring"));
        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
