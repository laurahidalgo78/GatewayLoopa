using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ApiGatewayPay.Core.Ports.Backend;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Crypto;
using ApiGatewayPay.Infrastructure.Http;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Backend;

/// <summary>
/// PLANTILLA para conectar el backend real vía HTTP/JSON. El contrato esperado está en
/// contracts/backend-gateway.openapi.yaml. Ajuste rutas y autenticación por configuración (Backend:Http:*);
/// solo modifique este archivo si su backend usa otro formato.
/// </summary>
public sealed class HttpBackendAdapter(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<BackendOptions> options,
    ISecretProvider secrets,
    TimeProvider time) : IBackendGateway
{
    public const string HttpClientName = "backend";
    public const string TokenClientName = "backend-auth";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lazy<IOutboundAuthenticator> _auth = new(() =>
        OutboundAuthenticator.Create(options.CurrentValue.Http.Auth, secrets, httpFactory, TokenClientName, time));

    public string Name => "http-backend";

    private HttpBackendOptions O => options.CurrentValue.Http;

    public async Task<IReadOnlyList<BackendAccount>> GetTenantAccountsAsync(string tenantId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(O.Paths.TenantAccounts.Replace("{tenantId}", Uri.EscapeDataString(tenantId), StringComparison.Ordinal)));
        var accounts = await SendAsync<List<BackendAccount>>(request, ct);
        // Validación de la respuesta (API10: consumo inseguro de APIs): nada se usa sin verificar forma.
        return accounts?.Where(a => !string.IsNullOrWhiteSpace(a.AccountRef) && !string.IsNullOrWhiteSpace(a.BankAccountId) && a.Currency is { Length: 3 }).ToList() ?? [];
    }

    public async Task<BackendPaymentDecision> AuthorizePaymentAsync(BackendPaymentRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Url(O.Paths.AuthorizePayment)) { Content = JsonContent.Create(request, options: Json) };
        // Autorizar es una consulta sin efectos: se marca reintentable aunque sea POST.
        message.Options.Set(HardenedHttpClientExtensions.Retryable, true);
        return await SendAsync<BackendPaymentDecision>(message, ct) ?? new BackendPaymentDecision(false, null, "empty_response");
    }

    public async Task NotifyPaymentEventAsync(BackendPaymentEvent paymentEvent, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(paymentEvent, Json);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url(O.Paths.PaymentEvents)) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new("application/json");
        if (O.SigningSecretName is { } secretName)
        {
            var ts = time.GetUtcNow().ToUnixTimeSeconds();
            var nonce = HmacSignature.NewNonce();
            var key = Encoding.UTF8.GetBytes(await secrets.GetAsync(secretName, ct));
            message.Headers.Add("X-Signature-Timestamp", ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            message.Headers.Add("X-Signature-Nonce", nonce);
            message.Headers.Add("X-Signature", "v1=" + HmacSignature.Compute(key, ts, nonce, body));
        }
        // El backend debe deduplicar por PaymentId+Status (entrega al menos una vez).
        message.Headers.Add("Idempotency-Key", $"{paymentEvent.PaymentId:N}-{paymentEvent.Status}");
        await SendAsync<JsonElement?>(message, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(O.Paths.Health));
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private Uri Url(string path) => new(new Uri(O.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        await _auth.Value.ApplyAsync(request, ct);
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Polly.CircuitBreaker.BrokenCircuitException or Polly.Timeout.TimeoutRejectedException)
        {
            throw new BackendUnavailableException("Backend no disponible.", ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NoContent) return default;
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new BackendUnavailableException($"Backend respondió {(int)response.StatusCode}.");
            if (!response.IsSuccessStatusCode)
                throw new BackendUnavailableException($"Backend rechazó la petición ({(int)response.StatusCode}).");
            if (response.Content.Headers.ContentType?.MediaType != "application/json") return default;
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
    }
}
