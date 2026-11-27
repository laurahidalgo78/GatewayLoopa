using Microsoft.AspNetCore.Http.Features;

namespace ApiGatewayPay.Api.Middleware;

/// <summary>
/// Límite de cuerpo independiente del servidor (Kestrel, IIS, HTTP.sys, TestServer): rechaza por Content-Length
/// antes de leer y fija el límite del servidor para cuerpos chunked.
/// </summary>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly long _max = configuration.GetValue("Security:MaxRequestBodyBytes", 64 * 1024);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > _max)
        {
            await ProblemWriter.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large", "El cuerpo de la petición excede el tamaño permitido.");
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = _max;
        await next(context);
    }
}
