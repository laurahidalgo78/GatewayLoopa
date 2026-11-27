# Guía · Cómo añadir un servicio externo

Añadir un servicio nuevo (correo, SMS, KYC, CRM, pasarela, antifraude externo…) requiere **un archivo y una entrada de configuración**. El registro (`ExternalServiceDiscovery`) descubre la clase por su atributo y crea una instancia por cada entrada habilitada en `ExternalServices`.

Cada instancia recibe un `ExternalServiceContext` con:

- su **propio `HttpClient` endurecido**: allowlist de hosts, anti-SSRF, TLS 1.2+, timeouts, circuit breaker y reintentos solo en operaciones idempotentes;
- **sus propios secretos**: solo puede leer `services/{nombre}/…` dentro de su entorno, nunca las credenciales del banco ni las de otro servicio;
- su sección de configuración `Settings` y su autenticación saliente (`None`, `ApiKey`, `OAuth2ClientCredentials`).

## Paso 1 · Crea la clase

`src/ApiGatewayPay.Infrastructure/External/Services/KycProveedorXService.cs`:

```csharp
using System.Net.Http.Json;
using ApiGatewayPay.Core.Ports.External;

namespace ApiGatewayPay.Infrastructure.External.Services;

[ExternalService("kyc-proveedor-x")]                       // valor de "Type" en configuración
public sealed class KycProveedorXService(ExternalServiceContext context) : IExternalService
{
    public string Name => context.Name;
    public ExternalServiceKind Kind => ExternalServiceKind.Kyc;

    public async Task<ExternalServiceResult> ExecuteAsync(ExternalServiceRequest request, CancellationToken ct)
    {
        if (request.Operation != "verify-identity" || !request.Parameters.TryGetValue("documentNumber", out var doc))
            return ExternalServiceResult.Fail("unsupported_operation");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(context.Options.BaseUrl!), "/v1/checks"))
        {
            Content = JsonContent.Create(new { document = doc, type = request.Parameters.GetValueOrDefault("documentType", "CC") }),
        };
        await context.Auth.ApplyAsync(message, ct);              // credenciales de ESTE servicio
        using var response = await context.Http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) return ExternalServiceResult.Fail("http_" + (int)response.StatusCode);

        // Trate la respuesta como no confiable: valide antes de usar (API10).
        var body = await response.Content.ReadFromJsonAsync<KycResponse>(ct);
        return body?.Status is "approved" or "rejected"
            ? new ExternalServiceResult(true, body.Id, Data: new Dictionary<string, string> { ["status"] = body.Status })
            : ExternalServiceResult.Fail("invalid_response");
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct) => Task.FromResult(true);

    private sealed record KycResponse(string? Id, string? Status);
}
```

Reglas:

- **No registres datos personales completos.** Usa `Masking.MaskEmail`, `MaskPhone` y `MaskAccount`.
- **No reintentes POST con efectos** salvo que el proveedor acepte `Idempotency-Key`. En ese caso, márcalo con `message.Options.Set(HardenedHttpClientExtensions.Retryable, true)`.
- **Devuelve `ExternalServiceResult.Fail(código)`** para errores de negocio y deja que las excepciones de transporte suban (la cola reintentará si el llamador es un job).

## Paso 2 · Añade la configuración

`appsettings.json` (o variables de entorno):

```json
"ExternalServices": {
  "kyc": {
    "Type": "kyc-proveedor-x",
    "Enabled": true,
    "BaseUrl": "https://api.proveedor-x.com",
    "Auth": { "Type": "ApiKey", "ApiKeyHeader": "X-Api-Key", "ApiKeySecretName": "api-key" },
    "Http": { "TimeoutSeconds": 10, "AttemptTimeoutSeconds": 5, "MaxRetries": 1 },
    "Settings": { "Country": "CO" }
  }
}
```

Equivalente en variables: `ExternalServices__kyc__Type=kyc-proveedor-x`, `ExternalServices__kyc__Enabled=true`, …

- El host de `BaseUrl` se añade solo a la allowlist. Si el proveedor redirige a otros dominios, decláralos en `Http:AllowedHosts` (las redirecciones están desactivadas: hay que llamar directamente a la URL final).
- `Enabled` funciona como **feature flag**: con `false`, la instancia ni se crea.

## Paso 3 · Guarda el secreto

```bash
vault kv put secret/sandbox/services/kyc/api-key value='...'
# o con Secrets__Provider=Environment:
SECRET_SANDBOX_SERVICES_KYC_API_KEY=...
```

## Paso 4 · Úsalo

Inyecta `IExternalServiceRegistry` donde lo necesites, siempre desde un caso de uso o un job, nunca directamente desde un endpoint:

```csharp
var kyc = registry.Get("kyc");
var result = await kyc.ExecuteAsync(new ExternalServiceRequest("verify-identity",
    new Dictionary<string, string> { ["documentNumber"] = doc }), ct);
```

Si el servicio es de tipo `Alerting`, `AlertNotifier` lo usa automáticamente para todas las alertas (conciliación, kill switch, certificados, reutilización de tokens…). El ejemplo `SignedWebhookService` (`Type: signed-webhook`, `Settings:Kind: Alerting`) envía las alertas firmadas a Slack/Teams mediante un relay, a PagerDuty o a tu SIEM.

## Paso 5 · Pruébalo

Copia el patrón de `RealBankAdapterTests`: crea el `ExternalServiceContext` con un `HttpClient` sobre un `HttpMessageHandler` falso y verifica el mapeo de solicitud y respuesta, los errores y que no se registren datos sensibles. `ArchitectureTests.Every_external_service_type_is_discoverable` sirve de modelo para comprobar que el registro encuentra tu tipo.
