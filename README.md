# ApiGatewayPay

Base de una API de pagos y conexión bancaria **segura por diseño** y **lista para conectar**: el backend, el banco y cualquier servicio externo se integran mediante adaptadores y configuración, sin tocar el núcleo. Incluye un backend y un banco simulados que funcionan de punta a punta.

**Stack**: ASP.NET Core / .NET 10 (LTS) · EF Core (SQL Server / SQLite) · Polly (resiliencia) · OpenTelemetry · xUnit.

## Arranque rápido (Windows, Visual Studio o CLI)

```powershell
dotnet run --project src/ApiGatewayPay.Api          # http://localhost:8080 (Development: mocks + emisor OAuth2 local)
./tools/demo-e2e.ps1                                 # en otra consola: recorre todos los flujos
dotnet test                                          # 126 tests unitarios + 52 de integración
```

En Visual Studio: abre `ApiGatewayPay.sln`, ejecuta el perfil **ApiGatewayPay.Api** y usa `requests.http`.

**Usuarios de desarrollo** (contraseña `DevOnly-Sandbox-2026!`; el código MFA se obtiene con `./tools/Get-Totp.ps1 -User <usuario>`):

| Usuario | Tenant | Puede |
|---|---|---|
| `maker@tenant-a` | tenant-a | crear pagos, registrar beneficiarios, consultar cuentas |
| `checker@tenant-a` | tenant-a | aprobar o rechazar pagos y beneficiarios (segundo par de ojos) |
| `admin@tenant-a` | tenant-a | API keys y verificación del libro de su tenant |
| `maker@tenant-b` | tenant-b | igual que el maker (sirve para probar el aislamiento entre tenants) |
| `ops@platform` | platform | kill switch y conciliación |

**Banco simulado**: el monto (en centavos) define el comportamiento: terminado en `13` → rechazo; en `99` → timeout ambiguo (el banco sí lo procesó); en `98` → timeout con orden perdida. Cualquier otro → aceptado y liquidado.

> `appsettings.Development.json` contiene **solo secretos aleatorios de sandbox** para los mocks. No se publica en la imagen, y en producción la aplicación se niega a arrancar si esa sección tiene contenido.

## Endpoints

| Método y ruta | Scope | MFA |
|---|---|---|
| `POST /v1/payments` (cabecera `Idempotency-Key`) | `payments:write` | — |
| `GET /v1/payments`, `GET /v1/payments/{id}` | `payments:read` | — |
| `POST /v1/payments/{id}/approve` · `/reject` | `payments:approve` | ✔ |
| `POST /v1/beneficiaries` · `GET` | `beneficiaries:write` / `:read` | ✔ (alta) |
| `POST /v1/beneficiaries/{id}/approve` | `beneficiaries:approve` | ✔ |
| `GET /v1/accounts`, `/{ref}/balance`, `/{ref}/movements?from&to` | `accounts:read` | — |
| `POST /v1/webhooks/bank/{provider}` | firma + timestamp + nonce + IP | — |
| `GET/POST /v1/admin/kill-switch` | `platform:killswitch` | ✔ (POST) |
| `POST/GET/DELETE /v1/admin/api-keys` | `admin:apikeys` | ✔ (POST/DELETE) |
| `POST /v1/admin/reconciliation/runs`, `GET …/{id}` | `platform:reconciliation` | — |
| `GET /v1/admin/ledger/verify` | `admin:audit` | — |
| `POST /v1/auth/token`, `/revoke` (solo `Auth:Mode=Local`) | público + rate limit | — |
| `GET /health` · `GET /ready` | público | — |

Contrato completo: `contracts/openapi.v1.json`, y `/openapi/v1.json` en desarrollo.

## Documentación

| | |
|---|---|
| [01 · Arquitectura](docs/01-arquitectura.md) | Diagrama, carpetas, flujos, máquina de estados, decisiones |
| [02 · Modelo de amenazas STRIDE](docs/02-modelo-amenazas-stride.md) | Con foco en el flujo bancario |
| [03 · Seguridad y OWASP API Top 10](docs/03-seguridad.md) | Cobertura, decisiones y tabla de secretos |
| [Cómo conectar mi backend](docs/guia-conectar-backend.md) | |
| [Cómo conectar el banco](docs/guia-conectar-banco.md) | mTLS, firma, ISO 20022, pruebas |
| [Cómo añadir un servicio externo](docs/guia-anadir-servicio-externo.md) | Un archivo + una entrada de configuración |
| [Cumplimiento](docs/cumplimiento.md) | PCI-DSS, ISO 27001, SFC, Ley 1581, GDPR |
| [Checklist de producción](docs/checklist-produccion.md) | |
| [Respuesta a incidentes](docs/respuesta-incidentes.md) | Kill switch y rotación de emergencia |

## DevOps

- `Dockerfile`: multi-stage, runtime *chiseled* sin shell, usuario no root, restore con lock files.
- `docker-compose.yml`: API + SQL Server, con perfiles opcionales `observability` (OTel Collector + Prometheus con `deploy/alerts.yml`) y `vault`. Copia `.env.example` a `.env`.
- `.github/workflows/ci.yml`: build (warnings como errores), tests, NuGet audit / dependencias vulnerables, dependency review, CodeQL, gitleaks, Trivy (imagen y Dockerfile), SBOM y publicación firmada con cosign.
- `db/sqlserver/`: esquema, endurecimiento del libro append-only y permisos mínimos, cada uno con su rollback.
