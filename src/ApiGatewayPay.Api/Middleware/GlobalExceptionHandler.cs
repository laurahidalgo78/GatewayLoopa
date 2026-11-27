using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace ApiGatewayPay.Api.Middleware;

/// <summary>Traduce excepciones a problem+json uniforme. Los detalles solo van al log (con correlationId).</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, title) = exception switch
        {
            AppException e => (StatusFor(e.Kind), e.Code, e.Message),
            WebhookVerificationException e => LogWebhook(e),
            BadHttpRequestException { StatusCode: 413 } => (413, "payload_too_large", "El cuerpo de la petición excede el tamaño permitido."),
            BadHttpRequestException => (400, "invalid_request", "Petición mal formada o con campos no permitidos."),
            BankUnavailableException or BankIntegrityException => (503, "bank_unavailable", "El banco no está disponible. Intente más tarde."),
            BackendUnavailableException => (503, "backend_unavailable", "Servicio temporalmente no disponible."),
            ConcurrencyConflictException => (409, "concurrent_modification", "El recurso fue modificado por otra operación. Reintente."),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "client_closed_request", "Petición cancelada."),
            _ => (500, "internal_error", "Error interno. Use el correlationId para soporte."),
        };

        if (status >= 500) logger.LogError(exception, "Error no controlado {Code}", code);
        else logger.LogInformation("Petición rechazada {Status} {Code}", status, code);

        await ProblemWriter.WriteAsync(context, status, code, title);
        return true;
    }

    private (int, string, string) LogWebhook(WebhookVerificationException e)
    {
        // El motivo exacto queda en el log; al emisor no se le da pista de qué validación falló.
        logger.LogWarning("Webhook rechazado: {Reason}", e.Reason);
        return (401, "webhook_rejected", "Webhook no verificado.");
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => 400,
        ErrorKind.Unauthorized => 401,
        ErrorKind.Forbidden => 403,
        ErrorKind.NotFound => 404,
        ErrorKind.Conflict => 409,
        ErrorKind.IdempotencyMismatch => 422,
        ErrorKind.BusinessRule => 422,
        ErrorKind.Unavailable => 503,
        _ => 500,
    };
}
