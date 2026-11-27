using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.UseCases.Reconciliation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Reconciliation;

public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Días hacia atrás que se re-concilian en cada ciclo (para capturar liquidaciones tardías).</summary>
    public int LookbackDays { get; set; } = 1;
}

/// <summary>Conciliación automática periódica (hoy y los días de lookback). En multi-instancia, ejecútela en una sola réplica o mediante un job programado.</summary>
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, IOptionsMonitor<ReconciliationOptions> options,
    IOptionsMonitor<FeatureFlags> features, TimeProvider time, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.CurrentValue.IntervalMinutes), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!features.CurrentValue.Reconciliation) continue;
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            for (var d = options.CurrentValue.LookbackDays; d >= 0; d--)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(today.AddDays(-d), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("Conciliación de {Date} falló: {ErrorType}", today.AddDays(-d), ex.GetType().Name);
                }
            }
        }
    }
}
