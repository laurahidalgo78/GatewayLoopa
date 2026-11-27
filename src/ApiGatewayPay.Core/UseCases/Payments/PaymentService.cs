using System.Security.Cryptography;
using System.Text;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Beneficiaries;
using ApiGatewayPay.Core.Domain.Ledger;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Fraud;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.Ports.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Core.UseCases.Payments;

public sealed record CreatePaymentCommand(Guid BeneficiaryId, long AmountMinor, string Currency, string Reference, string IdempotencyKey)
{
    /// <summary>Huella canónica de la petición: misma Idempotency-Key con otro contenido = error, no reutilización.</summary>
    public string ComputeHash()
    {
        var canonical = $"{BeneficiaryId:N}|{AmountMinor}|{Currency}|{Reference}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record PaymentResult(Payment Payment, bool Replayed);

public sealed class PaymentService(
    IPaymentRepository payments,
    IBeneficiaryRepository beneficiaries,
    IBackendGateway backend,
    IKillSwitch killSwitch,
    ILedger ledger,
    IJobQueue jobs,
    IUnitOfWork uow,
    FraudEngine fraud,
    IOptionsMonitor<FeatureFlags> features,
    IOptionsMonitor<FraudOptions> fraudOptions,
    TimeProvider time,
    ILogger<PaymentService> logger)
{
    public async Task<PaymentResult> CreateAsync(CreatePaymentCommand cmd, AuditActor actor, CancellationToken ct)
    {
        if (!features.CurrentValue.Payments)
            throw AppException.Unavailable("payments_disabled", "Los pagos están deshabilitados temporalmente.");
        if ((await killSwitch.GetAsync(ct)).Active)
            throw AppException.Unavailable("bank_connection_disabled", "La conexión bancaria está suspendida.");

        var requestHash = cmd.ComputeHash();
        var existing = await payments.FindByIdempotencyKeyAsync(actor.TenantId, cmd.IdempotencyKey, ct);
        if (existing is not null) return Replay(existing, requestHash);

        var money = new Money(cmd.AmountMinor, cmd.Currency);
        var beneficiary = await beneficiaries.GetAsync(actor.TenantId, cmd.BeneficiaryId, ct)
            ?? throw AppException.NotFound("beneficiary_not_found", "Beneficiario no encontrado.");
        if (beneficiary.Status != BeneficiaryStatus.Active)
            throw AppException.BusinessRule("beneficiary_not_active", "El beneficiario no está aprobado en la lista blanca.");

        var now = time.GetUtcNow().UtcDateTime;
        var (payment, created) = Payment.Create(Guid.CreateVersion7(), actor, beneficiary.Id, money, cmd.Reference,
            cmd.IdempotencyKey, requestHash, now);
        await AppendAsync(payment, created, actor, ct);

        var velocityWindow = TimeSpan.FromSeconds(fraudOptions.CurrentValue.VelocityWindowSeconds);
        var stats = await payments.GetStatsAsync(actor.TenantId, money.Currency, beneficiary.Id,
            now.AddHours(-24), now - velocityWindow, ct);
        var decision = fraud.Evaluate(money, stats);

        if (decision.Outcome == FraudOutcome.Block)
        {
            logger.LogWarning("Pago bloqueado por antifraude. Tenant={Tenant} Reasons={Reasons}", actor.TenantId, string.Join(',', decision.Reasons));
            var system = AuditActor.System(actor.TenantId, actor.CorrelationId);
            await AppendAsync(payment, payment.Reject(system, "risk_" + decision.Reasons[0], now), system, ct);
            return await SaveNewAsync(payment, actor, requestHash, ct);
        }

        var backendDecision = await backend.AuthorizePaymentAsync(new BackendPaymentRequest(
            payment.Id, actor.TenantId, actor.ActorId, money.AmountMinor, money.Currency, beneficiary.Id, cmd.Reference), ct);
        if (!backendDecision.Approved || string.IsNullOrEmpty(backendDecision.DebtorAccountRef))
        {
            var system = AuditActor.System(actor.TenantId, actor.CorrelationId);
            await AppendAsync(payment, payment.Reject(system, "backend_" + (backendDecision.ReasonCode ?? "declined"), now), system, ct);
            return await SaveNewAsync(payment, actor, requestHash, ct);
        }
        payment.AssignDebtorAccount(backendDecision.DebtorAccountRef);

        if (decision.Outcome == FraudOutcome.RequireApproval)
        {
            await AppendAsync(payment, payment.RequireApproval(decision.Reasons, now), actor, ct);
        }
        else
        {
            await AppendAsync(payment, payment.AutoApprove(now), actor, ct);
            jobs.Enqueue(JobTypes.SubmitPayment, new PaymentJob(payment.Id));
        }
        return await SaveNewAsync(payment, actor, requestHash, ct);
    }

    public async Task<Payment> ApproveAsync(Guid paymentId, AuditActor actor, CancellationToken ct)
    {
        if ((await killSwitch.GetAsync(ct)).Active)
            throw AppException.Unavailable("bank_connection_disabled", "La conexión bancaria está suspendida.");
        var payment = await GetAsync(paymentId, actor, ct);
        var change = payment.Approve(actor, time.GetUtcNow().UtcDateTime);
        await AppendAsync(payment, change, actor, ct);
        jobs.Enqueue(JobTypes.SubmitPayment, new PaymentJob(payment.Id));
        await SaveAsync(ct);
        return payment;
    }

    public async Task<Payment> RejectAsync(Guid paymentId, string reasonCode, AuditActor actor, CancellationToken ct)
    {
        var payment = await GetAsync(paymentId, actor, ct);
        if (payment.Status != PaymentStatus.PendingApproval)
            throw AppException.Conflict("invalid_state_transition", "Solo se pueden rechazar pagos pendientes de aprobación.");
        var change = payment.Reject(actor, reasonCode, time.GetUtcNow().UtcDateTime);
        await AppendAsync(payment, change, actor, ct);
        jobs.Enqueue(JobTypes.NotifyBackend, new BackendNotificationJob(payment.Id, payment.Status.ToString(), null, reasonCode, payment.UpdatedAt));
        await SaveAsync(ct);
        return payment;
    }

    public async Task<Payment> GetAsync(Guid paymentId, AuditActor actor, CancellationToken ct) =>
        // 404 (no 403) para recursos de otro tenant: no revela su existencia.
        await payments.GetAsync(actor.TenantId, paymentId, ct)
        ?? throw AppException.NotFound("payment_not_found", "Pago no encontrado.");

    public Task<PagedResult<Payment>> ListAsync(AuditActor actor, PaymentStatus? status, int limit, string? cursor, CancellationToken ct) =>
        payments.ListAsync(actor.TenantId, status, Math.Clamp(limit, 1, 100), cursor, ct);

    private async Task<PaymentResult> SaveNewAsync(Payment payment, AuditActor actor, string requestHash, CancellationToken ct)
    {
        payments.Add(payment);
        try
        {
            await uow.SaveChangesAsync(ct);
            return new PaymentResult(payment, Replayed: false);
        }
        catch (DuplicateKeyException)
        {
            // Carrera: dos peticiones con la misma Idempotency-Key. El índice único garantiza un solo pago.
            var winner = await payments.FindByIdempotencyKeyAsync(actor.TenantId, payment.IdempotencyKey, ct)
                ?? throw AppException.Conflict("idempotency_in_progress", "Operación en curso con la misma Idempotency-Key.");
            return Replay(winner, requestHash);
        }
    }

    private static PaymentResult Replay(Payment existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            throw new AppException(ErrorKind.IdempotencyMismatch, "idempotency_key_reused",
                "La Idempotency-Key ya se usó con un contenido distinto.");
        return new PaymentResult(existing, Replayed: true);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await uow.SaveChangesAsync(ct); }
        catch (ConcurrencyConflictException)
        {
            throw AppException.Conflict("concurrent_modification", "El pago fue modificado por otra operación. Reintente.");
        }
    }

    private Task AppendAsync(Payment p, StatusChange change, AuditActor actor, CancellationToken ct) =>
        ledger.AppendAsync(PaymentLedger.Record(p, change, actor), ct);
}

public static class PaymentLedger
{
    public static LedgerRecord Record(Payment p, StatusChange change, AuditActor actor) => new(
        AggregateType: "payment",
        AggregateId: p.Id.ToString("N"),
        AggregateSequence: p.Version,
        EventType: change.EventType,
        Actor: actor,
        OccurredAt: p.UpdatedAt,
        FromStatus: change.From?.ToString(),
        ToStatus: change.To.ToString(),
        AmountMinor: p.AmountMinor,
        Currency: p.Currency,
        Detail: change.Reason);
}
