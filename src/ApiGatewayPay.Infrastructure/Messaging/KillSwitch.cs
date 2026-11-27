using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Ledger;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace ApiGatewayPay.Infrastructure.Messaging;

/// <summary>
/// Kill switch bancario con dos palancas: la variable de configuración Bank:KillSwitch (inmediata, por
/// despliegue/entorno) y el estado en BD (desde el endpoint de administración, compartido por todas las
/// instancias en ≤2 s). Cualquiera de las dos activa = conexión bancaria suspendida.
/// </summary>
public sealed class KillSwitch(AppDbContext db, IConfiguration configuration, IMemoryCache cache, ILedger ledger, IAlertNotifier alerts, TimeProvider time) : IKillSwitch
{
    private const string Id = "bank";
    private const string CacheKey = "killswitch:bank";

    public async Task<KillSwitchState> GetAsync(CancellationToken ct)
    {
        if (configuration.GetValue<bool>("Bank:KillSwitch"))
            return Report(new KillSwitchState(true, "configuration", "configuration", null, FromConfiguration: true));

        if (cache.TryGetValue(CacheKey, out KillSwitchState? cached) && cached is not null) return cached;
        var row = await db.KillSwitches.AsNoTracking().FirstOrDefaultAsync(k => k.Id == Id, ct);
        var state = new KillSwitchState(row?.Active ?? false, row?.Reason, row?.ChangedBy, row?.ChangedAt, false);
        cache.Set(CacheKey, state, TimeSpan.FromSeconds(2));
        return Report(state);
    }

    public async Task SetAsync(bool active, string reason, AuditActor actor, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var row = await db.KillSwitches.FirstOrDefaultAsync(k => k.Id == Id, ct);
        if (row is null)
        {
            row = new KillSwitchEntity { Id = Id };
            db.KillSwitches.Add(row);
        }
        row.Active = active;
        row.Reason = reason;
        row.ChangedBy = actor.ActorId;
        row.ChangedAt = now;
        row.Version++;
        await ledger.AppendAsync(new LedgerRecord("killswitch", Id, row.Version, active ? "killswitch.activated" : "killswitch.deactivated",
            actor, now, ToStatus: active ? "Active" : "Inactive", Detail: reason), ct);
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        Telemetry.AppMetrics.KillSwitch(active);
        await alerts.NotifyAsync(new Alert(active ? AlertSeverity.Critical : AlertSeverity.Warning,
            active ? "bank_kill_switch_activated" : "bank_kill_switch_deactivated",
            active ? "Conexión bancaria suspendida por kill switch." : "Conexión bancaria reactivada.",
            new Dictionary<string, string> { ["by"] = actor.ActorId }), ct);
    }

    private static KillSwitchState Report(KillSwitchState state)
    {
        Telemetry.AppMetrics.KillSwitch(state.Active);
        return state;
    }
}
