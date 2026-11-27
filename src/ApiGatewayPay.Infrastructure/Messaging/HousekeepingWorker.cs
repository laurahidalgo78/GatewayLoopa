using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.Messaging;

/// <summary>Purga nonces expirados, trabajos terminados antiguos y refresh tokens vencidos. Nunca toca el libro.</summary>
public sealed class HousekeepingWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<HousekeepingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = time.GetUtcNow().UtcDateTime;
                await db.WebhookNonces.Where(n => n.ExpiresAt < now).ExecuteDeleteAsync(stoppingToken);
                var cutoff = now.AddDays(-7);
                await db.Jobs.Where(j => j.Status == JobStatus.Done && j.UpdatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                await db.RefreshTokens.Where(t => t.FamilyExpiresAt < now).ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Fallo en tareas de mantenimiento");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
