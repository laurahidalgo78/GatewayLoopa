using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Core.UseCases.Reconciliation;

/// <summary>
/// Concilia los registros internos contra el extracto del banco de un día. Detecta:
/// pagos que el banco no tiene, movimientos del banco que no originamos (posible fraude),
/// diferencias de monto y de estado. Alerta si hay discrepancias de severidad alta o crítica.
/// </summary>
public sealed class ReconciliationService(
    IPaymentRepository payments,
    IBankAdapter bank,
    IKillSwitch killSwitch,
    IReconciliationStore store,
    IJobQueue jobs,
    IUnitOfWork uow,
    IAlertNotifier alerts,
    TimeProvider time,
    ILogger<ReconciliationService> logger)
{
    public async Task<ReconciliationReport> RunAsync(DateOnly date, CancellationToken ct)
    {
        if ((await killSwitch.GetAsync(ct)).Active)
            throw AppException.Unavailable("bank_connection_disabled", "La conexión bancaria está suspendida.");

        var startedAt = time.GetUtcNow().UtcDateTime;
        var from = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var statement = await bank.GetStatementAsync(date, ct);

        var internalForDay = await payments.ListSentBetweenAsync(from, from.AddDays(1), ct);
        var bankE2E = statement.Entries.Where(e => e.EndToEndId is not null).Select(e => e.EndToEndId!).ToHashSet();
        // Incluye pagos enviados otro día pero liquidados en este (cortes de día).
        var extra = await payments.FindByEndToEndIdsAsync(bankE2E.Except(internalForDay.Select(p => p.EndToEndId)).ToList(), ct);
        var internalAll = internalForDay.Concat(extra).ToDictionary(p => p.EndToEndId);
        var bankByE2E = statement.Entries.Where(e => e.EndToEndId is not null).GroupBy(e => e.EndToEndId!).ToDictionary(g => g.Key, g => g.ToList());

        var discrepancies = new List<ReconciliationDiscrepancy>();
        foreach (var p in internalAll.Values)
        {
            if (!bankByE2E.TryGetValue(p.EndToEndId, out var entries))
            {
                if (p.Status == PaymentStatus.Confirmed)
                    discrepancies.Add(new("missing_in_bank", DiscrepancySeverity.High, p.EndToEndId, p.Id, "Confirmado internamente sin registro en el extracto."));
                else if (p.Status is PaymentStatus.Sent or PaymentStatus.Unknown)
                    discrepancies.Add(new("pending_not_in_statement", DiscrepancySeverity.Low, p.EndToEndId, p.Id, "Pago en curso aún sin reflejo en el extracto."));
                continue;
            }
            if (entries.Count > 1)
                discrepancies.Add(new("duplicate_in_bank", DiscrepancySeverity.Critical, p.EndToEndId, p.Id, $"El banco reporta {entries.Count} movimientos para el mismo EndToEndId."));

            var e = entries[0];
            if (e.AmountMinor != p.AmountMinor || e.Currency != p.Currency)
                discrepancies.Add(new("amount_mismatch", DiscrepancySeverity.Critical, p.EndToEndId, p.Id, "Monto o moneda distintos entre banco y registro interno."));

            switch (e.State)
            {
                case BankPaymentState.Settled when p.Status is PaymentStatus.Failed or PaymentStatus.Rejected:
                    discrepancies.Add(new("settled_but_failed", DiscrepancySeverity.Critical, p.EndToEndId, p.Id, "El banco liquidó un pago marcado como fallido."));
                    break;
                case BankPaymentState.Settled when p.Status is PaymentStatus.Sent or PaymentStatus.Unknown:
                    discrepancies.Add(new("settled_pending_confirmation", DiscrepancySeverity.Medium, p.EndToEndId, p.Id, "Liquidado en banco; se programa consulta de estado."));
                    jobs.Enqueue(JobTypes.PaymentInquiry, new PaymentJob(p.Id));
                    break;
                case BankPaymentState.Rejected when p.Status == PaymentStatus.Confirmed:
                    discrepancies.Add(new("rejected_but_confirmed", DiscrepancySeverity.Critical, p.EndToEndId, p.Id, "El banco rechazó un pago confirmado internamente."));
                    break;
                case BankPaymentState.Returned when p.Status == PaymentStatus.Confirmed:
                    discrepancies.Add(new("returned_not_reversed", DiscrepancySeverity.High, p.EndToEndId, p.Id, "Devolución bancaria no registrada internamente."));
                    jobs.Enqueue(JobTypes.PaymentInquiry, new PaymentJob(p.Id));
                    break;
            }
        }

        foreach (var e in statement.Entries.Where(e => e.EndToEndId is null || !internalAll.ContainsKey(e.EndToEndId)))
            discrepancies.Add(new("missing_internally", DiscrepancySeverity.Critical, e.EndToEndId, null,
                $"Movimiento bancario sin origen interno (ref ****{Masking.Last4(e.BankReference)})."));

        var report = new ReconciliationReport(Guid.CreateVersion7(), date, startedAt, time.GetUtcNow().UtcDateTime,
            internalAll.Count, statement.Entries.Count, discrepancies);
        store.Add(report);
        await uow.SaveChangesAsync(ct);

        var serious = discrepancies.Count(d => d.Severity >= DiscrepancySeverity.High);
        logger.LogInformation("Conciliación {Date}: internos={Internal} banco={Bank} discrepancias={Count} graves={Serious}",
            date, report.InternalCount, report.BankCount, discrepancies.Count, serious);
        if (serious > 0)
            await alerts.NotifyAsync(new Alert(
                discrepancies.Any(d => d.Severity == DiscrepancySeverity.Critical) ? AlertSeverity.Critical : AlertSeverity.High,
                "reconciliation_discrepancies", $"Conciliación {date:yyyy-MM-dd}: {serious} discrepancias graves.",
                new Dictionary<string, string> { ["reportId"] = report.Id.ToString(), ["date"] = date.ToString("yyyy-MM-dd") }), ct);
        return report;
    }
}
