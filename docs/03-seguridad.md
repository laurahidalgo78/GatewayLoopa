# 03 · Decisiones de seguridad y cobertura de OWASP API Security Top 10 (2023)

## Cobertura de OWASP API Top 10

| Riesgo | Cómo se cubre | Dónde | Test |
|---|---|---|---|
| **API1 BOLA** | Todo repositorio expuesto exige `tenantId` del token; 404 para recursos ajenos; la pertenencia de cuentas la decide el backend | `Repositories.cs`, `AccountService.cs` | `Tenants_cannot_see_or_use_each_others_resources` |
| **API2 Autenticación rota** | JWT de 5 min con algoritmos fijos; refresh rotativo con detección de reutilización; API keys hasheadas (HMAC con *pepper*), con expiración y revocación; MFA/step-up; bloqueo y rate limit en login; errores que no permiten enumerar | `Program.cs`, `Security/*`, `ApiKeyService.cs` | `Refresh_tokens_rotate…`, `Api_keys_are_hashed…`, `Forged_or_tampered_tokens…`, `LockoutTests` |
| **API3 BOPLA** | DTOs explícitos de entrada y salida; campos desconocidos y duplicados rechazados; respuestas enmascaradas | `Contracts/Dtos.cs` | `Payloads_are_validated_strictly` |
| **API4 Consumo de recursos** | Rate limiting por identidad; límite de cuerpo (independiente del servidor); límites de Kestrel, de profundidad JSON y de paginación (máx. 100); rango de movimientos ≤ 31 días; timeouts salientes | `Program.cs`, `RequestSizeLimitMiddleware.cs` | `Oversized_payload…`, `RateLimitTests` |
| **API5 BFLA** | Política por scope en cada endpoint; *fallback* autenticado; `platform:*` separado de `admin:*` | `Endpoints/*`, `Policies` | `Missing_scope_is_forbidden`, `Kill_switch_requires_platform_scope…` |
| **API6 Flujos de negocio sensibles** | Antifraude (límites, velocidad, anomalías), maker-checker, lista blanca de beneficiarios, step-up, idempotencia | `FraudEngine.cs`, `Payment.cs` | `High_amount_requires…`, `Only_whitelisted…`, `FraudEngineTests` |
| **API7 SSRF** | Allowlist de hosts por integración; validación de la IP resuelta en el socket (bloquea RFC1918, loopback, link-local, metadatos cloud, IPv6 ULA y NAT64); sin redirecciones ni proxy | `SsrfGuard.cs`, `HostAllowlistHandler` | `SsrfGuardTests` |
| **API8 Mala configuración** | Cabeceras de seguridad, CORS cerrado por defecto, OpenAPI desactivado en producción, errores uniformes, arranque bloqueado ante configuración insegura | `SecurityHeadersMiddleware.cs`, `EnvironmentGuard.cs` | `Responses_carry_security_headers…`, `EnvironmentGuardTests` |
| **API9 Inventario** | Versionado `/v1`, contratos OpenAPI y JSON Schema versionados en el repo, entornos separados, inventario de integraciones en configuración | `contracts/` | — |
| **API10 Consumo inseguro de APIs** | Respuestas del banco y del backend tratadas como no confiables: validación de forma, tipos, rangos y EndToEndId; firma verificada; XML seguro; respuesta ilegible → ambigua (no confirmada) | `Codecs/*`, `RealBankAdapter.cs`, `HttpBackendAdapter.cs` | `CodecTests`, `RealBankAdapterTests` |

## Por qué se tomó cada decisión

**Autenticación**
- **OIDC externo en producción.** Un IdP dedicado (Keycloak, Entra ID, Auth0, Cognito) resuelve MFA, recuperación de cuentas y federación mejor que código propio. El emisor local existe solo para probar de punta a punta, y `EnvironmentGuard` lo bloquea en producción.
- **Step-up según RFC 9470.** El 401 incluye `error="insufficient_user_authentication"`, `acr_values` y `max_age`, de modo que el cliente sabe que debe re-autenticar con MFA, no que le falta un permiso.
- **`auth_time` se conserva al refrescar.** Si no, un refresh token robado daría "MFA reciente" indefinidamente.
- **TOTP de un solo uso.** El paso de tiempo usado se recuerda: un código interceptado no sirve dos veces.

**Autorización**
- **Deny by default.** Un endpoint nuevo sin política queda protegido, no abierto.
- **404 en lugar de 403 para recursos ajenos.** Así no se revela que el recurso existe.
- **ABAC además de RBAC.** Atributos como el tenant, el tipo de actor (humano o máquina), la fuerza de autenticación y la relación maker/checker se evalúan en el dominio, no solo en el borde.

**Validación**
- **JSON estricto:** sin campos desconocidos, sin duplicados (evita diferencias entre parsers), sin números como texto, profundidad máxima 16.
- **Texto restringido** en referencias y nombres (subconjunto compatible con ISO 20022/SWIFT): evita inyección en mensajes bancarios y logs.

**Salida a terceros**
- **Un HttpClient por integración**, cada uno con su allowlist, su certificado y su política de reintentos: comprometer una integración no da acceso a otra.
- **Reintentos solo en operaciones idempotentes.** Un POST de pago nunca se reintenta automáticamente.
- **Circuit breaker.** Protege al banco y a la API durante caídas parciales.

**Datos**
- **AES-256-GCM** (cifrado autenticado) con el tenant como dato asociado: un texto cifrado copiado a otro tenant no descifra.
- **Versión de clave en el texto cifrado** (`v1.`): la rotación no requiere migración inmediata.
- **Huella HMAC** del número de cuenta para detectar duplicados sin descifrar.
- **No se almacenan datos de tarjeta.** Si en el futuro se integran tarjetas, use la tokenización de la pasarela (alcance PCI mínimo); nunca guarde CVV.

**Auditoría**
- **El libro encadena con HMAC, no con un hash simple**: sin la clave, que vive en el gestor de secretos, un atacante con acceso a la BD no puede recalcular la cadena después de alterarla.
- **Cruce versión ↔ libro**: borrar las últimas entradas no rompe la cadena, pero sí ese cruce.

**Operación**
- **Kill switch con dos palancas**: la variable `Bank__KillSwitch=true` (inmediata, no depende de la BD) y el endpoint con MFA (auditado y compartido por todas las instancias).
- **Fail-fast**: es preferible que un despliegue mal configurado no arranque a que arranque inseguro.

## Tabla de secretos

| Nombre lógico (relativo al entorno) | Contenido | Usado por |
|---|---|---|
| `bank/webhook-secret` | Secreto HMAC (o PEM público del banco si `Algorithm=RsaSha256/EcdsaSha256`) | Verificación de webhooks |
| `bank/oauth-client-secret` | Client secret OAuth2 del banco | `RealBankAdapter` |
| `bank/signing-private-key` | PEM privado (RSA/EC) para JWS | Firma de peticiones |
| `bank/bank-public-key` | PEM público del banco | Verificación de respuestas |
| `bank/mtls-client-pfx` (+ `-password`) | PKCS#12 en base64 | mTLS |
| `crypto/field-key-v{n}` | 32 bytes en base64 | Cifrado de números de cuenta |
| `crypto/fingerprint-key` | 32 bytes en base64 | Huellas HMAC |
| `ledger/hmac-key` | 32 bytes en base64 | Cadena del libro |
| `auth/api-key-pepper` | Texto aleatorio | Hash de API keys |
| `auth/local-signing-key` | PEM EC P-256 (solo emisor local) | Tokens de desarrollo |
| `backend/oauth-client-secret`, `backend/notify-signing-key` | Credenciales del backend | `HttpBackendAdapter` |
| `services/{nombre}/…` | Credenciales de cada servicio externo | Solo esa instancia |
| `db/connection-string` | Cadena de conexión (preferir identidad administrada) | Persistencia |

Con `Secrets:Provider=Environment`, `production/bank/webhook-secret` se lee de `SECRET_PRODUCTION_BANK_WEBHOOK_SECRET`. Con `File`, de `/run/secrets/production__bank__webhook-secret`. Con `Vault` (KV v2), de `secret/data/production/bank/webhook-secret`, campo `value`.
