using System.Text.Json;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Messaging;
using ApiGatewayPay.Core.Ports.Persistence;
using ApiGatewayPay.Core.UseCases.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Core.UseCases.Webhooks;

public enum WebhookIngestResult { Accepted, Duplicate }

/// <summary>
/// Ingesta de webhooks bancarios: verificar firma → anti-replay (nonce) → deduplicar (event id) →
/// guardar en inbox → encolar. Responde rápido; el procesamiento es asíncrono.
/// </summary>
public sealed class WebhookService(
    IBankAdapter bank,
    IWebhookInbox inbox,
    IJobQueue jobs,
    IUnitOfWork uow,
    IOptionsMonitor<FeatureFlags> features,
    IOptionsMonitor<WebhookSecurityOptions> security,
    TimeProvider time)
{
    public async Task<WebhookIngestResult> IngestAsync(string provider, BankWebhookEnvelope envelope, CancellationToken ct)
    {
        if (!features.CurrentValue.Webhooks)
            throw AppException.Unavailable("webhooks_disabled", "Webhooks deshabilitados.");
        if (!string.Equals(provider, bank.Name, StringComparison.OrdinalIgnoreCase))
            throw AppException.NotFound("unknown_provider", "Proveedor desconocido.");

        var notification = await bank.VerifyAndParseWebhookAsync(envelope, ct); // lanza WebhookVerificationException

        var expires = time.GetUtcNow().UtcDateTime.AddSeconds(security.CurrentValue.ToleranceSeconds * 2);
        if (!await inbox.TryRegisterNonceAsync(provider, notification.Nonce, expires, ct))
            throw new WebhookVerificationException("replayed_nonce");

        // Reenvíos legítimos del banco traen el mismo EventId con un nonce nuevo: se aceptan sin reprocesar.
        if (await inbox.ExistsAsync(provider, notification.EventId, ct))
            return WebhookIngestResult.Duplicate;

        var item = new WebhookInboxItem(Guid.CreateVersion7(), provider, notification.EventId, notification.EventType,
            notification.EndToEndId, notification.State?.ToString(), notification.BankReference, notification.ReasonCode,
            time.GetUtcNow().UtcDateTime);
        inbox.Add(item);
        jobs.Enqueue(JobTypes.ProcessBankWebhook, new WebhookJob(item.Id));
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException)
        {
            return WebhookIngestResult.Duplicate;
        }
        return WebhookIngestResult.Accepted;
    }
}

public sealed class WebhookSecurityOptions
{
    public const string Section = "Bank:Webhooks";

    public int ToleranceSeconds { get; set; } = 300;
    /// <summary>Rangos CIDR desde los que el banco envía webhooks. Vacío = no se filtra por IP.</summary>
    public List<string> AllowedCidrs { get; set; } = [];
    public string SignatureHeader { get; set; } = "X-Bank-Signature";
    public string TimestampHeader { get; set; } = "X-Bank-Timestamp";
    public string NonceHeader { get; set; } = "X-Bank-Nonce";
    /// <summary>HmacSha256 (secreto compartido) | RsaSha256 | EcdsaSha256 (clave pública del banco en PEM).</summary>
    public string Algorithm { get; set; } = "HmacSha256";
    /// <summary>Secreto HMAC o clave pública PEM del banco, según el algoritmo.</summary>
    public string SecretName { get; set; } = "bank/webhook-secret";
}

public sealed class ProcessBankWebhookHandler(
    IWebhookInbox inbox,
    IPaymentRepository payments,
    IJobQueue jobs,
    IUnitOfWork uow,
    ILogger<ProcessBankWebhookHandler> logger) : IJobHandler
{
    public string JobType => JobTypes.ProcessBankWebhook;

    public async Task<JobResult> HandleAsync(string payloadJson, JobContext context, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<WebhookJob>(payloadJson)!;
        var item = await inbox.GetAsync(job.InboxId, ct);
        if (item is null) return JobResult.Dead("inbox_item_not_found");

        var payment = item.EndToEndId is null ? null : await payments.FindByEndToEndIdAsync(item.EndToEndId, ct);
        if (payment is null)
        {
            logger.LogWarning("Webhook {EventId} sin pago asociado", item.EventId);
            await inbox.MarkProcessedAsync(item.Id, "no_matching_payment", ct);
            await uow.SaveChangesAsync(ct);
            return JobResult.Done();
        }

        // El webhook solo dispara la verificación: el estado se toma de una consulta autenticada al banco.
        // Así, un webhook falsificado con un secreto filtrado no puede confirmar un pago.
        var relevant = payment.Status is PaymentStatus.Sent or PaymentStatus.Unknown ||
                       (payment.Status == PaymentStatus.Confirmed && item.State == nameof(BankPaymentState.Returned));
        if (relevant) jobs.Enqueue(JobTypes.PaymentInquiry, new PaymentJob(payment.Id));
        await inbox.MarkProcessedAsync(item.Id, relevant ? "inquiry_scheduled" : "ignored_status_" + payment.Status, ct);
        await uow.SaveChangesAsync(ct);
        return JobResult.Done();
    }
}
