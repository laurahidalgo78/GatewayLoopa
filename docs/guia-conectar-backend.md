# Guía · Cómo conectar mi backend

El núcleo usa el puerto `IBackendGateway` (`src/ApiGatewayPay.Core/Ports/Backend/IBackendGateway.cs`) para tres cosas:

1. **`GetTenantAccountsAsync`**: qué cuentas bancarias pertenecen a cada tenant. Es la fuente de verdad de la autorización por atributos (anti-BOLA).
2. **`AuthorizePaymentAsync`**: tus reglas de negocio (¿existe la factura?, ¿el proveedor está activo?) y qué cuenta fondea el pago.
3. **`NotifyPaymentEventAsync`**: aviso asíncrono, desde la cola, de pagos confirmados, fallidos, rechazados o reversados.

Hoy funciona `MockBackendAdapter`. Para conectar el real tienes dos caminos.

## Opción A · Tu backend habla HTTP/JSON (sin escribir código)

1. **Implementa en tu backend el contrato** `contracts/backend-gateway.openapi.yaml`: 3 endpoints + `/health`. Las rutas son configurables.
2. **Elige cómo te autenticará ApiGatewayPay** y regístralo en tu IdP o backend:
   - OAuth2 client credentials (recomendado): crea un cliente `apigatewaypay` con el mínimo scope necesario.
   - API key: genera una clave larga y aleatoria.
   - mTLS: emite un certificado de cliente para ApiGatewayPay.
3. **Guarda los secretos en el gestor de secretos** (nunca en appsettings), con el prefijo del entorno:
   ```
   sandbox/backend/oauth-client-secret      ← client secret
   sandbox/backend/notify-signing-key       ← secreto HMAC compartido para firmar notificaciones
   ```
4. **Configura** (variables de entorno o `appsettings.{Entorno}.json`):
   ```bash
   Backend__Adapter=Http
   Backend__Http__BaseUrl=https://backend.tu-dominio.com
   Backend__Http__Auth__Type=OAuth2ClientCredentials        # o ApiKey / None (+ mTLS)
   Backend__Http__Auth__TokenUrl=https://idp.tu-dominio.com/oauth2/token
   Backend__Http__Auth__ClientId=apigatewaypay
   Backend__Http__Auth__ClientSecretName=backend/oauth-client-secret
   Backend__Http__SigningSecretName=backend/notify-signing-key
   # Rutas, si difieren del contrato:
   Backend__Http__Paths__TenantAccounts=/api/empresas/{tenantId}/cuentas
   # Política de salida (el host de BaseUrl/TokenUrl se añade solo a la allowlist):
   Backend__Http__Http__TimeoutSeconds=10
   Backend__Http__Http__MaxRetries=2
   # mTLS opcional:
   Backend__Http__Http__ClientCertificate__PfxSecretName=backend/mtls-client-pfx
   ```
   Si el backend está en la red interna (p. ej. `10.x`), el anti-SSRF lo bloqueará: es intencional. Expónlo con un nombre DNS detrás de un balanceador interno y añádelo a `Backend__Http__Http__AllowedHosts`. Solo en desarrollo puedes usar `AllowPrivateNetworks=true`; en producción el guard lo rechaza.
5. **Verifica las notificaciones en tu backend**:
   - recalcula `hex(HMAC-SHA256(secreto, "{X-Signature-Timestamp}.{X-Signature-Nonce}.{cuerpo}"))` y compáralo en tiempo constante con `X-Signature` (sin el prefijo `v1=`);
   - rechaza timestamps con más de 5 min de diferencia y nonces repetidos;
   - deduplica por `Idempotency-Key` (la entrega es *al menos una vez*).
6. **Prueba**:
   - `GET /ready` debe mostrar `backend: Healthy`;
   - `GET /v1/accounts` debe devolver las cuentas que entrega tu backend (enmascaradas);
   - ejecuta `tools/demo-e2e.ps1`.

## Opción B · Tu backend usa otro protocolo (gRPC, SOAP, base de datos, cola)

1. Crea `src/ApiGatewayPay.Infrastructure/Backend/MiBackendAdapter.cs` que implemente `IBackendGateway`. Usa `HttpBackendAdapter` como referencia de manejo de errores: lanza `BackendUnavailableException` para fallos transitorios, de modo que la cola reintente las notificaciones.
2. Regístralo en `InfrastructureServiceCollectionExtensions.AddBackend` con un nuevo valor de `Backend:Adapter`:
   ```csharp
   case "MiBackend":
       services.AddHardenedHttpClient("mi-backend", options.Http.Http.WithHostsOf(options.Http.BaseUrl)); // si usa HTTP
       services.AddSingleton<IBackendGateway, MiBackendAdapter>();
       break;
   ```
3. Añade a `EnvironmentGuard` la regla que corresponda (p. ej. permitir `MiBackend` en producción).

## Reglas que el backend debe respetar

- **No devuelvas números de cuenta a quien no corresponde**: `GetTenantAccounts` es la frontera de autorización.
- **`AuthorizePayment` no debe tener efectos secundarios**: puede reintentarse.
- **Las respuestas se validan** (API10): cuentas sin `accountRef`, `bankAccountId` o moneda válida se descartan.
