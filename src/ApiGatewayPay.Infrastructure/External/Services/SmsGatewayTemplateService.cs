using System.Net.Http.Json;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.External;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.External.Services;

/// <summary>
/// PLANTILLA para un proveedor de SMS (OTP, notificaciones). Copie este archivo para cualquier proveedor
/// REST: cambie el atributo, el mapeo del cuerpo y la interpretación de la respuesta.
/// Config de ejemplo en appsettings.json → ExternalServices:sms.
/// </summary>
[ExternalService("sms-gateway")]
public sealed class SmsGatewayTemplateService(ExternalServiceContext context) : IExternalService
{
    public string Name => context.Name;
    public ExternalServiceKind Kind => ExternalServiceKind.Sms;

    public async Task<ExternalServiceResult> ExecuteAsync(ExternalServiceRequest request, CancellationToken ct)
    {
        if (request.Operation != "send" || !request.Parameters.TryGetValue("to", out var to) || !request.Parameters.TryGetValue("text", out var text))
            return ExternalServiceResult.Fail("unsupported_operation");
        if (!to.StartsWith('+') || to.Length > 16 || !to[1..].All(char.IsAsciiDigit))
            return ExternalServiceResult.Fail("invalid_phone");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(context.Options.BaseUrl!), context.Settings["Path"] ?? "/v1/messages"))
        {
            Content = JsonContent.Create(new { to, text = text.Length > 160 ? text[..160] : text, sender = context.Settings["Sender"] }),
        };
        await context.Auth.ApplyAsync(message, ct); // API key u OAuth2 según ExternalServices:sms:Auth
        using var response = await context.Http.SendAsync(message, ct);
        context.Logger.LogInformation("SMS a {To} → {Status}", Masking.MaskPhone(to), (int)response.StatusCode);
        if (!response.IsSuccessStatusCode) return ExternalServiceResult.Fail("http_" + (int)response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ProviderResponse>(ct);
        return ExternalServiceResult.Ok(result?.Id);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        using var response = await context.Http.GetAsync(new Uri(new Uri(context.Options.BaseUrl!), "/health"), ct);
        return response.IsSuccessStatusCode;
    }

    private sealed record ProviderResponse(string? Id);
}
