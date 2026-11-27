using ApiGatewayPay.Infrastructure.Platform;

namespace ApiGatewayPay.Api.Middleware;

/// <summary>
/// Acepta X-Correlation-Id solo si tiene un formato seguro (evita inyección en logs); si no, genera uno.
/// Se devuelve en la respuesta, se añade al scope de logs y se propaga a llamadas salientes y a la cola.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[CorrelationContext.HeaderName].ToString();
        var id = CorrelationContext.IsValid(incoming) ? incoming : CorrelationContext.NewId();
        CorrelationContext.Id = id;
        context.TraceIdentifier = id;
        context.Response.Headers[CorrelationContext.HeaderName] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }
}
