# 01 · Arquitectura

## Stack elegido: ASP.NET Core sobre .NET 10 (LTS)

- **Soporte**: .NET 10 es LTS, con soporte hasta noviembre de 2028. .NET 8 salió de soporte en noviembre de 2026.
- **Seguridad incluida en el framework**: JWT Bearer/OIDC, políticas de autorización, rate limiting, `System.Security.Cryptography` (AES-GCM, ECDSA, PBKDF2, X509), `SslStream` con mTLS y validación de cadena personalizada. Para un sistema financiero, cuantas menos dependencias de terceros, menos superficie de ataque.
- **Resiliencia oficial**: `Microsoft.Extensions.Http.Resilience` (Polly v8) aporta timeouts, reintentos con jitter y circuit breaker por cliente HTTP.
- **Encaje con el equipo**: ya existía una solución de Visual Studio y se trabaja con SQL Server. EF Core soporta SQL Server (producción) y SQLite (desarrollo y tests) sin cambiar el núcleo.

## Arquitectura hexagonal (puertos y adaptadores)

```
                        ┌──────────────────────── ApiGatewayPay.Api (adaptador de entrada) ───────────────────────┐
  Clientes ──HTTPS──▶   │ Kestrel ▶ CorrelationId ▶ ExceptionHandler ▶ SecurityHeaders ▶ SizeLimit ▶ CORS          │
  (apps, ERP,           │ ▶ AuthN (JWT OIDC | API key) ▶ RateLimiter ▶ AuthZ (scopes + step-up) ▶ Validation       │
   operadores)          │ ▶ Endpoints /v1/* ── construyen AuditActor ──┐                                            │
                        └──────────────────────────────────────────────┼────────────────────────────────────────────┘
  Banco ──webhook──▶ /v1/webhooks/bank/{p} (firma+ts+nonce+IP) ────────┤
                                                                       ▼
                        ┌──────────────────────────── ApiGatewayPay.Core (núcleo, sin dependencias) ──────────────┐
                        │ Dominio: Payment (máquina de estados), Beneficiary (lista blanca), Money, LedgerRecord   │
                        │ Casos de uso: PaymentService, SubmitPaymentHandler, PaymentInquiryHandler,              │
                        │               WebhookService, ReconciliationService, AccountService, FraudEngine        │
                        │ PUERTOS: IBankAdapter · IBackendGateway · IExternalService(+Registry) · IAlertNotifier  │
                        │          IPaymentRepository · ILedger · IJobQueue · IKillSwitch · ISecretProvider ·     │
                        │          IFieldProtector · IWebhookInbox · IReconciliationStore · IUnitOfWork          │
                        └───────────────▲────────────────▲───────────────────▲───────────────────▲──────────────┘
                                        │                │                   │                   │
                        ┌───────────────┴──── ApiGatewayPay.Infrastructure (adaptadores de salida) ┴──────────────┐
                        │ Bank:     MockBankAdapter (sandbox simulado) │ RealBankAdapter (RestJson/ISO 20022/SOAP,  │
                        │           OAuth2, HMAC/JWS, mTLS, pinning, CA privada, rotación de certificados)        │
                        │ Backend:  MockBackendAdapter │ HttpBackendAdapter (plantilla configurable)              │
                        │ External: Registry dinámico [ExternalService("tipo")] + config → email, SMS, alertas…    │
                        │ Persistencia: EF Core (SQL Server / SQLite), libro HMAC encadenado, outbox de trabajos  │
                        │ Secretos: Config(dev) | Env | File | Vault  →  caché  →  ámbito por entorno             │
                        │ HTTP saliente endurecido: allowlist + anti-SSRF por socket + TLS1.2/1.3 + resiliencia   │
                        │ Workers: JobWorker (cola), Reconciliation, CertificateMonitor, Housekeeping             │
                        └─────────────────────────────────────────────────────────────────────────────────────────┘
```

**Regla de dependencias**: `Api → Infrastructure → Core`. El núcleo no conoce ASP.NET, EF Core, Polly ni ningún banco. Lo verifica el test `ArchitectureTests.Core_does_not_depend_on_adapters_or_frameworks`.

**Qué implementación se usa para cada puerto lo decide solo la configuración** (`Bank:Adapter`, `Backend:Adapter`, `Secrets:Provider`, `Persistence:Provider`, `ExternalServices:*`). La composición vive en `InfrastructureServiceCollectionExtensions`.

## Estructura de carpetas

```
ApiGatewayPay/
├── ApiGatewayPay.sln               global.json · Directory.Build.props · Directory.Packages.props · nuget.config
├── src/
│   ├── ApiGatewayPay.Core/         Dominio, puertos y casos de uso (sin infraestructura)
│   │   ├── Common/                 AppException, Money, AuditActor, Masking, FeatureFlags
│   │   ├── Domain/                 Payments (Payment, PaymentStateMachine), Beneficiaries, Ledger
│   │   ├── Ports/                  Bank, Backend, External, Persistence, Security, Messaging
│   │   ├── Fraud/                  FraudEngine + FraudOptions
│   │   └── UseCases/               Payments, Beneficiaries, Accounts, Webhooks, Reconciliation
│   ├── ApiGatewayPay.Infrastructure/
│   │   ├── Bank/Mock/              Banco simulado con fallos deterministas + simulador de webhooks
│   │   ├── Bank/Real/              RealBankAdapter, Codecs (RestJson, Iso20022, Soap), Signing (HMAC, JWS, Vault Transit)
│   │   ├── Bank/Tls/               CertificateStore (mTLS, CA privada, pinning), CertificateMonitor
│   │   ├── Bank/Webhooks/          WebhookVerifier (HMAC/RSA/ECDSA + timestamp + nonce)
│   │   ├── Backend/                MockBackendAdapter, HttpBackendAdapter
│   │   ├── External/               Registry, AlertNotifier, Services/ (email-console, signed-webhook, sms-gateway)
│   │   ├── Http/                   Cliente endurecido, SsrfGuard, allowlist, auth saliente (OAuth2/API key)
│   │   ├── Persistence/            AppDbContext, repositorios, EfLedger + LedgerVerifier, SchemaInitializer
│   │   ├── Messaging/              Outbox (cola durable), JobWorker, KillSwitch, Housekeeping
│   │   ├── Secrets/                Proveedores de secretos + aislamiento por entorno/integración
│   │   ├── Crypto/                 AES-256-GCM por campo, HMAC
│   │   ├── Security/               ApiKeyService, RefreshTokenService
│   │   ├── Platform/               EnvironmentGuard (fail-fast), health checks, correlación
│   │   └── Telemetry/              Métricas OpenTelemetry
│   └── ApiGatewayPay.Api/          Program.cs, Endpoints/, Security/ (JWT, API key, step-up, emisor local),
│                                   Middleware/, Contracts/ (DTOs + validación), appsettings*.json
├── tests/
│   ├── ApiGatewayPay.Tests.Unit/        dominio, antifraude, SSRF, cripto, TOTP, codecs, adaptador bancario, guard
│   └── ApiGatewayPay.Tests.Integration/ API completa en memoria: flujos de pago, fallos del banco, seguridad, webhooks
├── contracts/                      openapi.v1.json (generado) · backend-gateway.openapi.yaml · bank-restjson.schema.json
├── db/sqlserver/                   001/002 + Rollback (esquema, libro append-only, mínimo privilegio)
├── deploy/                         otel-collector, prometheus, reglas de alerta
├── docs/                           arquitectura, STRIDE, seguridad, guías, cumplimiento, checklist, incidentes
├── tools/                          demo-e2e.ps1, Get-Totp.ps1
├── Dockerfile · docker-compose.yml · .env.example · requests.http
└── .github/                        workflows/ci.yml · dependabot.yml
```

## Flujo de un pago

```
Cliente            API/PaymentService              BD (1 transacción)               JobWorker            Banco
  │ POST /v1/payments (Idempotency-Key)                                                │                   │
  ├───────────────▶ kill switch? feature flag?                                         │                   │
  │                 ¿existe (tenant,key)? ── sí ──▶ mismo hash → repetir respuesta     │                   │
  │                 beneficiario Active (lista blanca)                                 │                   │
  │                 antifraude: Block | RequireApproval | Allow                        │                   │
  │                 backend.AuthorizePayment (reglas + cuenta origen)                  │                   │
  │                 ── INSERT payment + ledger + job(payment.submit) ──▶ commit        │                   │
  │◀── 201 {status: approved | pending_approval | rejected}                            │                   │
  │                                                         claim job (lease) ◀────────┤                   │
  │                                     Approved→Sent + ledger + job(inquiry, +30 s) ◀─┤ commit ANTES del  │
  │                                                                                    ├── POST pago ─────▶│
  │                                                                                    │◀── 201 / timeout ─┤
  │                        Accepted → bank_accepted │ Rejected → Failed │ Ambiguo → Unknown + inquiry       │
  │                                                                                    ├── GET estado ────▶│
  │                              Settled → Confirmed │ NotFound (tras gracia) → Approved → reenvío MISMO E2E│
  │  Banco ── webhook firmado ──▶ verificar → nonce → dedupe → inbox + job → dispara inquiry (pull autenticado)
```

## Máquina de estados

```
             ┌──────────────▶ Rejected (antifraude, backend o checker)
             │
 Created ────┼──▶ PendingApproval ──(checker + MFA)──▶ Approved ──▶ Sent ──▶ Confirmed ──▶ Reversed
             │                      └──▶ Rejected          ▲   │      │  ▲
             └──────────────────────────(auto)─────────────┘   │      │  │
                                                               │      ▼  │
                                           NotFound confirmado └── Unknown ──▶ Failed
                                                                      Sent ──▶ Failed
```

`PaymentStateMachine` define las transiciones y cualquier otra lanza `invalid_state_transition`. Cada transición incrementa `Version` (concurrencia optimista) y escribe una entrada en el libro con la misma secuencia.

## Decisiones clave

| Decisión | Por qué |
|---|---|
| Montos en unidades mínimas (`long amountMinor`) | Sin errores de punto flotante; JSON estricto rechaza `"100"` como número. |
| `EndToEndId` = id del pago, reutilizado en reenvíos y enviado como `Idempotency-Key` al banco | El banco deduplica, así que un reenvío nunca produce un doble débito. |
| Estado `Sent` persistido **antes** de llamar al banco, junto con la consulta programada | Si el proceso muere a mitad, el pago queda en `Sent` y la consulta lo resuelve. No hay reenvío a ciegas. |
| Outbox transaccional en BD como cola | Pago y trabajo se confirman en la misma transacción. Escala con un relay a RabbitMQ/Service Bus sin tocar el núcleo. |
| Webhook = disparador; la verdad viene de una consulta autenticada | Un webhook falsificado (p. ej. con un secreto filtrado) no puede confirmar un pago por sí solo. |
| Libro append-only con HMAC encadenado por agregado | La concurrencia optimista del agregado serializa la cadena sin bloqueos globales; la clave HMAC fuera de la BD impide recalcular la cadena con acceso de DBA. |
| Sandbox y Producción como "entorno de plataforma" separado del hosting | Prefijos de secretos (`sandbox/`, `production/`) y de API keys (`agp_test_`, `agp_live_`) más `EnvironmentGuard` hacen imposible mezclar credenciales. |
| Emisor OAuth2 local solo para desarrollo | Permite probar MFA, step-up y rotación de refresh tokens de punta a punta. En producción el guard exige OIDC. |
