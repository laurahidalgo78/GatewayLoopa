namespace ApiGatewayPay.Core.Ports.Messaging;

/// <summary>
/// Cola de trabajos asíncronos. Enqueue NO confirma: el trabajo se guarda en la misma transacción que
/// el cambio de negocio (patrón outbox), así nunca hay un pago sin su trabajo ni un trabajo sin su pago.
/// </summary>
public interface IJobQueue
{
    void Enqueue<TPayload>(string jobType, TPayload payload, TimeSpan? delay = null);
}

public interface IJobHandler
{
    string JobType { get; }

    Task<JobResult> HandleAsync(string payloadJson, JobContext context, CancellationToken ct);
}

public sealed record JobContext(Guid JobId, int Attempt, string CorrelationId);

public enum JobOutcome { Done, Retry, Dead }

public readonly record struct JobResult(JobOutcome Outcome, TimeSpan? RetryAfter = null, string? Reason = null)
{
    public static JobResult Done() => new(JobOutcome.Done);
    public static JobResult Retry(TimeSpan after, string reason) => new(JobOutcome.Retry, after, reason);
    public static JobResult Dead(string reason) => new(JobOutcome.Dead, null, reason);
}

public static class JobTypes
{
    public const string SubmitPayment = "payment.submit";
    public const string PaymentInquiry = "payment.inquiry";
    public const string ProcessBankWebhook = "bank.webhook.process";
    public const string NotifyBackend = "backend.notify";
}

public sealed record PaymentJob(Guid PaymentId);

public sealed record WebhookJob(Guid InboxId);

public sealed record BackendNotificationJob(Guid PaymentId, string Status, string? BankReference, string? FailureCode, DateTime OccurredAt);
