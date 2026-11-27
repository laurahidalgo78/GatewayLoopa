using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Infrastructure.Persistence;
using ApiGatewayPay.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Messaging;

public sealed class QueueOptions
{
    public const string Section = "Queue";

    public bool Enabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int BatchSize { get; set; } = 10;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxAttempts { get; set; } = 20;
}

/// <summary>
/// Consume trabajos con arrendamiento (lease): varias instancias pueden correr a la vez sin procesar el
/// mismo trabajo dos veces de forma concurrente. Si una instancia muere, el lease expira y otra lo retoma;
/// por eso los handlers son idempotentes (verifican el estado antes de actuar).
/// </summary>
public sealed class JobWorker(IServiceScopeFactory scopes, IOptionsMonitor<QueueOptions> options, TimeProvider time, ILogger<JobWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName[..Math.Min(15, Environment.MachineName.Length)]}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                if (options.CurrentValue.Enabled) processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error en el ciclo del worker de trabajos");
            }
            if (processed == 0)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(options.CurrentValue.PollIntervalMilliseconds), time, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        List<Guid> candidates;
        var now = time.GetUtcNow().UtcDateTime;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            candidates = await db.Jobs.AsNoTracking()
                .Where(j => (j.Status == JobStatus.Pending && j.AvailableAt <= now) || (j.Status == JobStatus.Processing && j.LockedUntil < now))
                .OrderBy(j => j.AvailableAt).Select(j => j.Id).Take(o.BatchSize).ToListAsync(ct);
        }

        var processed = 0;
        foreach (var id in candidates)
        {
            if (await TryProcessAsync(id, o, ct)) processed++;
        }
        return processed;
    }

    private async Task<bool> TryProcessAsync(Guid id, QueueOptions o, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var leaseUntil = now.AddSeconds(o.LeaseSeconds);

        // Reclamo atómico: solo una instancia obtiene filas afectadas = 1.
        var claimed = await db.Jobs
            .Where(j => j.Id == id && ((j.Status == JobStatus.Pending && j.AvailableAt <= now) || (j.Status == JobStatus.Processing && j.LockedUntil < now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Processing)
                .SetProperty(j => j.LockedUntil, leaseUntil)
                .SetProperty(j => j.LockedBy, _workerId)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (claimed != 1) return false;

        var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id, ct);
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.JobType == job.Type);
        CorrelationContext.Id = job.CorrelationId;
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = job.CorrelationId, ["JobType"] = job.Type, ["JobId"] = job.Id });

        JobResult result;
        if (handler is null)
        {
            result = JobResult.Dead("no_handler");
        }
        else
        {
            try
            {
                result = await handler.HandleAsync(job.Payload, new JobContext(job.Id, job.Attempts, job.CorrelationId), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // No se guarda el mensaje de la excepción (podría contener datos sensibles), solo su tipo.
                logger.LogError(ex, "Fallo procesando trabajo {JobType}", job.Type);
                result = JobResult.Retry(Backoff(job.Attempts), ex.GetType().Name);
            }
        }

        if (result.Outcome == JobOutcome.Retry && job.Attempts >= o.MaxAttempts)
            result = JobResult.Dead("max_attempts:" + result.Reason);

        var finishedAt = time.GetUtcNow().UtcDateTime;
        var status = result.Outcome switch { JobOutcome.Done => JobStatus.Done, JobOutcome.Dead => JobStatus.Dead, _ => JobStatus.Pending };
        var availableAt = result.Outcome == JobOutcome.Retry ? finishedAt + (result.RetryAfter ?? Backoff(job.Attempts)) : job.AvailableAt;
        var error = result.Reason is { Length: > 500 } r ? r[..500] : result.Reason;
        await db.Jobs.Where(j => j.Id == id && j.LockedBy == _workerId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.AvailableAt, availableAt)
            .SetProperty(j => j.LockedUntil, (DateTime?)null)
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.LastError, error)
            .SetProperty(j => j.UpdatedAt, finishedAt), CancellationToken.None);

        Telemetry.AppMetrics.JobProcessed(job.Type, result.Outcome.ToString());
        if (result.Outcome == JobOutcome.Dead)
            logger.LogWarning("Trabajo {JobType} enviado a dead-letter: {Reason}", job.Type, result.Reason);
        return true;
    }

    /// <summary>Backoff exponencial con jitter completo para no sincronizar reintentos entre instancias.</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Min(300_000, 1000 * Math.Pow(2, Math.Min(attempt, 10)))));
}
