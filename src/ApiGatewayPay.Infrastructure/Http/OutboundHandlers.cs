using System.Diagnostics;
using System.Text.RegularExpressions;
using ApiGatewayPay.Infrastructure.Platform;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.Http;

/// <summary>Verifica esquema, host y ausencia de credenciales en la URL antes de cualquier I/O.</summary>
public sealed class HostAllowlistHandler(string integration, OutboundHttpOptions options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new OutboundRequestBlockedException("URI vacía");
        if (!uri.IsAbsoluteUri) throw new OutboundRequestBlockedException("URI relativa");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new OutboundRequestBlockedException("credenciales en la URL");
        if (uri.Scheme != Uri.UriSchemeHttps && (options.RequireHttps || uri.Scheme != Uri.UriSchemeHttp))
            throw new OutboundRequestBlockedException($"{integration}: esquema no permitido");
        if (!IsAllowed(uri.IdnHost, options.AllowedHosts))
            throw new OutboundRequestBlockedException($"{integration}: host fuera de la lista blanca");
        return base.SendAsync(request, ct);
    }

    public static bool IsAllowed(string host, IEnumerable<string> allowed) =>
        allowed.Any(a => a.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(a[1..], StringComparison.OrdinalIgnoreCase) && host.Length > a.Length - 1
            : string.Equals(host, a, StringComparison.OrdinalIgnoreCase));
}

public sealed class CorrelationPropagationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (CorrelationContext.Id is { } id && !request.Headers.Contains(CorrelationContext.HeaderName))
            request.Headers.TryAddWithoutValidation(CorrelationContext.HeaderName, id);
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// Log de llamadas salientes sin cuerpo, sin query string y con identificadores largos enmascarados
/// en la ruta (p. ej. números de cuenta en /accounts/123456789/balance).
/// </summary>
public sealed partial class OutboundLoggingHandler(string integration, ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var path = RedactPath(request.RequestUri?.AbsolutePath);
        try
        {
            var response = await base.SendAsync(request, ct);
            Telemetry.AppMetrics.OutboundCall(integration, (int)response.StatusCode, sw.Elapsed);
            logger.LogInformation("Salida {Integration} {Method} {Host}{Path} -> {Status} en {ElapsedMs} ms",
                integration, request.Method.Method, request.RequestUri?.Host, path, (int)response.StatusCode, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            Telemetry.AppMetrics.OutboundCall(integration, 0, sw.Elapsed);
            logger.LogWarning("Salida {Integration} {Method} {Host}{Path} falló ({ErrorType}) en {ElapsedMs} ms",
                integration, request.Method.Method, request.RequestUri?.Host, path, ex.GetType().Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    public static string RedactPath(string? path) => path is null ? string.Empty : LongDigits().Replace(path, m => "***" + m.Value[^4..]);

    [GeneratedRegex(@"\d{6,}")]
    private static partial Regex LongDigits();
}
