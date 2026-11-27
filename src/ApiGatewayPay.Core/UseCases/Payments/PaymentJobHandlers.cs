using System.Text.Json;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Core.UseCases.Payments;

/// <summary>
/// Envía un pago aprobado al banco. Regla de oro: el estado Sent se persiste ANTES de llamar al banco
/// y junto con una consulta de estado programada. Si el proceso muere a mitad, el pago queda en Sent y la
/// consulta lo resuelve: nunca se reenvía a ciegas.
/// </summary>
public sealed class SubmitPaymentHandler(
    IPaymentRepository payments,
    IBeneficiaryRepository beneficiaries,
    IBackendGateway backend,
    IBankAdapter bank,
    IFieldProtector protector,
    IKillSwitch killSwitch,
    ILedger ledger,
    IJobQueue jobs,
    IUnitOfWork uow,
    IOptionsMonitor<PaymentProcessingOptions> options,
    TimeProvider time,
    ILogger<SubmitPaymentHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.SubmitPayment;

    public async Task<JobResult> HandleAsync(string payloadJson, JobContext context, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<PaymentJob>(payloadJson)!;
        if ((await killSwitch.GetAsync(ct)).Active)
            return JobResult.Retry(TimeSpan.FromSeconds(30), "kill_switch_active");

        var payment = await payments.GetForProcessingAsync(job.PaymentId, ct);
        if (payment is null) return JobResult.Dead("payment_not_found");
        // Idempotencia del handler: si ya no está Approved, otro intento ya lo procesó.
        if (payment.Status != PaymentStatus.Approved) return JobResult.Done();

        var system = AuditActor.System(payment.TenantId, context.CorrelationId);
        var accounts = await backend.GetTenantAccountsAsync(payment.TenantId, ct);
        var debtor = accounts.FirstOrDefault(a => a.AccountRef == payment.DebtorAccountRef && a.Currency == payment.Currency);
        var beneficiary = await beneficiaries.GetAsync(payment.TenantId, payment.BeneficiaryId, ct);
        if (debtor is null || beneficiary is null)
        {
            await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkFailed(debtor is null ? "debtor_account_not_found" : "beneficiary_not_found", Now()), system), ct);
            EnqueueBackendNotification(payment);
            await uow.SaveChangesAsync(ct);
            return JobResult.Done();
        }

        var accountNumber = await protector.UnprotectAsync(beneficiary.AccountNumberCipher, BeneficiaryPurpose.For(payment.TenantId), ct);

        await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkSent(Now()), system), ct);
        jobs.Enqueue(JobTypes.PaymentInquiry, new PaymentJob(payment.Id), TimeSpan.FromSeconds(options.CurrentValue.FirstInquiryDelaySeconds));
        await uow.SaveChangesAsync(ct); // si esto falla por concurrencia, otro worker lo tomó: no se llama al banco.

        var instruction = new BankPaymentInstruction(payment.EndToEndId, payment.AmountMinor, payment.Currency,
            debtor.BankAccountId,
            new BankCreditor(beneficiary.DisplayName, beneficiary.BankCode, accountNumber, beneficiary.AccountType.ToString()),
            payment.Reference);

        BankPaymentSubmission result;
        try
        {
            result = await bank.InitiatePaymentAsync(instruction, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Incluso un error "antes de enviar" se trata como ambiguo: la consulta de estado decide.
            logger.LogWarning("Envío ambiguo del pago {PaymentId}: {ErrorType}", payment.Id, ex.GetType().Name);
            result = new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, ex.GetType().Name);
        }

        switch (result.Outcome)
        {
            case BankSubmissionOutcome.Accepted:
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RecordBankAcceptance(result.BankReference, Now()), system), ct);
                break;
            case BankSubmissionOutcome.Rejected:
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkFailed("bank_" + (result.ReasonCode ?? "rejected"), Now()), system), ct);
                EnqueueBackendNotification(payment);
                break;
            default:
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkUnknown(result.ReasonCode ?? "ambiguous", Now()), system), ct);
                jobs.Enqueue(JobTypes.PaymentInquiry, new PaymentJob(payment.Id), TimeSpan.FromSeconds(options.CurrentValue.AmbiguousInquiryDelaySeconds));
                break;
        }
        await uow.SaveChangesAsync(ct);
        return JobResult.Done();
    }

    private void EnqueueBackendNotification(Payment p) =>
        jobs.Enqueue(JobTypes.NotifyBackend, new BackendNotificationJob(p.Id, p.Status.ToString(), p.BankReference, p.FailureCode, p.UpdatedAt));

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}

/// <summary>
/// Consulta el estado autoritativo en el banco para pagos Sent/Unknown y aplica la transición.
/// Las consultas (lecturas) sí pueden reintentarse; solo un NotFound confirmado habilita un reenvío.
/// </summary>
public sealed class PaymentInquiryHandler(
    IPaymentRepository payments,
    IBankAdapter bank,
    IKillSwitch killSwitch,
    ILedger ledger,
    IJobQueue jobs,
    IUnitOfWork uow,
    IAlertNotifier alerts,
    IOptionsMonitor<PaymentProcessingOptions> options,
    TimeProvider time,
    ILogger<PaymentInquiryHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.PaymentInquiry;

    public async Task<JobResult> HandleAsync(string payloadJson, JobContext context, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<PaymentJob>(payloadJson)!;
        if ((await killSwitch.GetAsync(ct)).Active)
            return JobResult.Retry(TimeSpan.FromSeconds(30), "kill_switch_active");

        var payment = await payments.GetForProcessingAsync(job.PaymentId, ct);
        if (payment is null) return JobResult.Dead("payment_not_found");
        if (payment.Status is not (PaymentStatus.Sent or PaymentStatus.Unknown or PaymentStatus.Confirmed)) return JobResult.Done();

        var o = options.CurrentValue;
        var system = AuditActor.Bank(bank.Name, payment.TenantId, context.CorrelationId);
        var now = time.GetUtcNow().UtcDateTime;

        BankPaymentStatusInfo status;
        try
        {
            status = await bank.GetPaymentStatusAsync(payment.EndToEndId, ct);
        }
        catch (Exception ex) when (ex is BankUnavailableException or BankIntegrityException or HttpRequestException or TimeoutException)
        {
            await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RecordInquiry("error:" + ex.GetType().Name, now), system), ct);
            await uow.SaveChangesAsync(ct);
            if (ex is BankIntegrityException)
                await alerts.NotifyAsync(new Alert(AlertSeverity.High, "bank_response_integrity", "Respuesta del banco con firma o formato inválido.",
                    new Dictionary<string, string> { ["paymentId"] = payment.Id.ToString() }), ct);
            return await ScheduleNextAsync(payment, o, ct);
        }

        if (status.AmountMinor is { } amt && (amt != payment.AmountMinor || status.Currency != payment.Currency))
        {
            // El banco reporta otro monto para nuestro EndToEndId: no se confirma, se escala.
            await alerts.NotifyAsync(new Alert(AlertSeverity.Critical, "bank_amount_mismatch", "El banco reporta un monto distinto para el pago.",
                new Dictionary<string, string> { ["paymentId"] = payment.Id.ToString() }), ct);
            await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RecordInquiry("amount_mismatch", now), system), ct);
            await uow.SaveChangesAsync(ct);
            return JobResult.Dead("amount_mismatch_manual_review");
        }

        if (payment.Status == PaymentStatus.Confirmed)
        {
            // Solo interesa una devolución posterior a la liquidación (webhook "returned").
            if (status.State != BankPaymentState.Returned) return JobResult.Done();
            await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkReversed("bank_" + (status.ReasonCode ?? "returned"), now), system), ct);
            Notify(payment);
            await uow.SaveChangesAsync(ct);
            return JobResult.Done();
        }

        switch (status.State)
        {
            case BankPaymentState.Settled:
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkConfirmed(status.BankReference, now), system), ct);
                Notify(payment);
                break;
            case BankPaymentState.Rejected:
            case BankPaymentState.Returned:
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkFailed("bank_" + (status.ReasonCode ?? status.State.ToString().ToLowerInvariant()), now), system), ct);
                Notify(payment);
                break;
            case BankPaymentState.NotFound when payment.Status == PaymentStatus.Sent && payment.SentAt > now.AddSeconds(-o.NotFoundGraceSeconds):
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RecordInquiry("not_found_within_grace", now), system), ct);
                await uow.SaveChangesAsync(ct);
                return await ScheduleNextAsync(payment, o, ct);
            case BankPaymentState.NotFound:
                if (payment.Status == PaymentStatus.Sent)
                    await ledger.AppendAsync(PaymentLedger.Record(payment, payment.MarkUnknown("not_found_after_grace", now), system), ct);
                if (payment.SubmissionAttempts >= o.MaxSubmissionAttempts)
                {
                    await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RecordInquiry("not_found_max_attempts", now), system), ct);
                    await uow.SaveChangesAsync(ct);
                    await alerts.NotifyAsync(new Alert(AlertSeverity.High, "payment_requires_manual_review",
                        "Pago sin registro en el banco tras el máximo de reenvíos.",
                        new Dictionary<string, string> { ["paymentId"] = payment.Id.ToString() }), ct);
                    return JobResult.Dead("not_found_max_attempts");
                }
                // El banco confirmó que no tiene la orden: reenvío seguro con el MISMO EndToEndId.
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RequeueAfterNotFound(now), system), ct);
                jobs.Enqueue(JobTypes.SubmitPayment, new PaymentJob(payment.Id));
                break;
            default: // Pending
                await ledger.AppendAsync(PaymentLedger.Record(payment, payment.RecordInquiry("pending", now), system), ct);
                await uow.SaveChangesAsync(ct);
                return await ScheduleNextAsync(payment, o, ct);
        }
        await uow.SaveChangesAsync(ct);
        logger.LogInformation("Pago {PaymentId} actualizado a {Status} tras consulta", payment.Id, payment.Status);
        return JobResult.Done();
    }

    private async Task<JobResult> ScheduleNextAsync(Payment payment, PaymentProcessingOptions o, CancellationToken ct)
    {
        if (payment.InquiryAttempts == o.InquiryAlertThreshold)
            await alerts.NotifyAsync(new Alert(AlertSeverity.Warning, "payment_stuck", "Pago sin estado final tras múltiples consultas.",
                new Dictionary<string, string> { ["paymentId"] = payment.Id.ToString(), ["status"] = payment.Status.ToString() }), ct);
        var seconds = Math.Min(o.MaxInquiryDelaySeconds, 5 * Math.Pow(2, Math.Min(payment.InquiryAttempts, 12)));
        return JobResult.Retry(TimeSpan.FromSeconds(seconds), "awaiting_final_status");
    }

    private void Notify(Payment p) =>
        jobs.Enqueue(JobTypes.NotifyBackend, new BackendNotificationJob(p.Id, p.Status.ToString(), p.BankReference, p.FailureCode, p.UpdatedAt));
}

public sealed class BackendNotificationHandler(
    IPaymentRepository payments, IBackendGateway backend, IOptionsMonitor<FeatureFlags> features) : IJobHandler
{
    public string JobType => JobTypes.NotifyBackend;

    public async Task<JobResult> HandleAsync(string payloadJson, JobContext context, CancellationToken ct)
    {
        if (!features.CurrentValue.BackendNotifications) return JobResult.Done();
        var job = JsonSerializer.Deserialize<BackendNotificationJob>(payloadJson)!;
        var payment = await payments.GetForProcessingAsync(job.PaymentId, ct);
        if (payment is null) return JobResult.Dead("payment_not_found");
        try
        {
            await backend.NotifyPaymentEventAsync(new BackendPaymentEvent(job.PaymentId, payment.TenantId, job.Status,
                job.BankReference, job.FailureCode, job.OccurredAt), ct);
            return JobResult.Done();
        }
        catch (BackendUnavailableException)
        {
            return JobResult.Retry(TimeSpan.FromSeconds(Math.Min(600, 10 * Math.Pow(2, context.Attempt))), "backend_unavailable");
        }
    }
}

public static class BeneficiaryPurpose
{
    /// <summary>AAD del cifrado: un texto cifrado copiado a otro tenant no descifra.</summary>
    public static string For(string tenantId) => $"beneficiary.account:{tenantId}";
}
