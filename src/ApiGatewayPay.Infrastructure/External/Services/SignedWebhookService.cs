using System.Globalization;
using System.Text;
using System.Text.Json;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Infrastructure.Crypto;

namespace ApiGatewayPay.Infrastructure.External.Services;

/// <summary>
/// EJEMPLO de adaptador real: envía un JSON firmado (HMAC) a una URL configurada. Sirve para alertas
/// (Slack/Teams vía relay, PagerDuty, SIEM) o para integrar cualquier servicio que acepte webhooks.
/// Config: ExternalServices:{nombre} { Type: "signed-webhook", BaseUrl, Settings:{ Kind, Path } } y el secreto
/// "services/{nombre}/signing-key".
/// </summary>
[ExternalService("signed-webhook")]
public sealed class SignedWebhookService(ExternalServiceContext context, TimeProvider time) : IExternalService
{
    public string Name => context.Name;
    public ExternalServiceKind Kind => Enum.TryParse<ExternalServiceKind>(context.Settings["Kind"], out var k) ? k : ExternalServiceKind.Other;

    public async Task<ExternalServiceResult> ExecuteAsync(ExternalServiceRequest request, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { operation = request.Operation, parameters = request.Parameters, sentAt = time.GetUtcNow() });
        var ts = time.GetUtcNow().ToUnixTimeSeconds();
        var nonce = HmacSignature.NewNonce();
        var key = Encoding.UTF8.GetBytes(await context.Secrets.GetAsync("signing-key", ct));

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(context.Options.BaseUrl!), context.Settings["Path"] ?? ""))
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new("application/json");
        message.Headers.Add("X-Signature-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
        message.Headers.Add("X-Signature-Nonce", nonce);
        message.Headers.Add("X-Signature", "v1=" + HmacSignature.Compute(key, ts, nonce, body));
        await context.Auth.ApplyAsync(message, ct);

        using var response = await context.Http.SendAsync(message, ct);
        return response.IsSuccessStatusCode ? ExternalServiceResult.Ok() : ExternalServiceResult.Fail("http_" + (int)response.StatusCode);
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(true);
}
