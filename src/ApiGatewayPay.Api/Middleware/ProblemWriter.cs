using System.Text.Json;
using ApiGatewayPay.Infrastructure.Platform;

namespace ApiGatewayPay.Api.Middleware;

/// <summary>
/// Formato único de error (RFC 9457 problem+json): código estable, mensaje genérico y correlationId.
/// Nunca incluye stack traces, SQL, nombres de host ni datos de terceros.
/// </summary>
public static class ProblemWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(HttpContext context, int status, string code, string title, IDictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var body = new Dictionary<string, object?>
        {
            ["type"] = "urn:agp:error:" + code,
            ["title"] = title,
            ["status"] = status,
            ["code"] = code,
            ["correlationId"] = CorrelationContext.Id ?? context.TraceIdentifier,
        };
        if (errors is not null) body["errors"] = errors;
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
