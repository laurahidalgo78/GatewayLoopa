using System.Globalization;
using System.Text;
using System.Text.Json;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Core.UseCases.Webhooks;
using ApiGatewayPay.Infrastructure.Crypto;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Bank.Mock;

/// <summary>
/// Simula al banco enviando webhooks firmados cuando un pago se liquida, contra nuestro propio endpoint.
/// Ejercita la ruta completa: firma, timestamp, nonce, deduplicación y procesamiento asíncrono.
/// </summary>
public sealed class MockBankWebhookSimulator(
    MockBankState state, IHttpClientFactory httpFactory, ISecretProvider secrets,
    IOptionsMonitor<BankOptions> bankOptions, IOptionsMonitor<WebhookSecurityOptions> webhookOptions,
    TimeProvider time, ILogger<MockBankWebhookSimulator> logger) : BackgroundService
{
    public const string HttpClientName = "mockbank-webhooks";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!bankOptions.CurrentValue.Mock.Webhooks.Enabled) continue;
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var p in state.Payments.Values.Where(p => !p.WebhookSent && (p.State != BankPaymentState.Pending || p.SettleAt <= now)))
            {
                if (p.State == BankPaymentState.Pending) p.State = BankPaymentState.Settled;
                try
                {
                    await SendAsync(p, stoppingToken);
                    p.WebhookSent = true;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("[mock-bank] Webhook no entregado ({ErrorType}); se reintentará", ex.GetType().Name);
                }
            }
        }
    }

    private async Task SendAsync(MockBankPayment p, CancellationToken ct)
    {
        var o = webhookOptions.CurrentValue;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId = "evt_" + Guid.NewGuid().ToString("N"),
            type = "payment.status_changed",
            endToEndId = p.EndToEndId,
            status = p.State.ToString().ToUpperInvariant(),
            bankReference = p.BankReference,
            reasonCode = p.ReasonCode,
        });
        var ts = time.GetUtcNow().ToUnixTimeSeconds();
        var nonce = HmacSignature.NewNonce();
        var key = Encoding.UTF8.GetBytes(await secrets.GetAsync(o.SecretName, ct));
        using var request = new HttpRequestMessage(HttpMethod.Post, bankOptions.CurrentValue.Mock.Webhooks.TargetUrl) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(o.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(o.NonceHeader, nonce);
        request.Headers.Add(o.SignatureHeader, "v1=" + HmacSignature.Compute(key, ts, nonce, body));
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
