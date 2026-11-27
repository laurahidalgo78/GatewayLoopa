# 02 · Modelo de amenazas (STRIDE), con foco en el flujo bancario

## Diagrama de flujo de datos y fronteras de confianza

```
 [Cliente/ERP] ──TB1──▶ ( API ) ──TB3 mTLS+firma──▶ [Banco]
 [Operador ]  ──TB1──▶ (     ) ◀──TB4 webhook firmado── [Banco]
                         │  │ ──TB5──▶ [Backend de negocio]
                         │  └──TB6──▶ [Gestor de secretos / KMS / HSM]
                         └──TB2──▶ [(BD: pagos, libro, cola, inbox)]
 [IdP OIDC] ── emite tokens ──▶ Cliente / Operador          [CI/CD] ──TB7──▶ imagen / despliegue
```

| Frontera | Qué la cruza | Activo crítico |
|---|---|---|
| TB1 Internet → API | Peticiones de clientes y operadores | Órdenes de pago, aprobaciones, datos de cuentas |
| TB2 API → BD | Estado de pagos, libro, cola | Integridad del libro, idempotencia |
| TB3 API → Banco | Iniciación de pagos, consultas | Credenciales mTLS/OAuth/firma, la orden misma |
| TB4 Banco → API | Webhooks de estado | Autenticidad del estado de un pago |
| TB5 API ↔ Backend | Reglas de negocio, pertenencia de cuentas, notificaciones | Decisión de autorizar el pago |
| TB6 API → Secretos | Claves y certificados | Claves de firma, cifrado de campo, HMAC del libro |
| TB7 Cadena de suministro | Dependencias, imagen, pipeline | Código que se ejecuta en producción |

## Amenazas y mitigaciones

Leyenda de estado: ✅ implementado en este código · 🔧 configurable/plantilla · 📋 fuera del software (proceso, infraestructura o contrato)

### S — Suplantación (Spoofing)

| # | Amenaza | Mitigación | Estado |
|---|---|---|---|
| S1 | Atacante usa credenciales robadas de un usuario para ordenar pagos | Tokens de 5 min; MFA + step-up (RFC 9470, `auth_time` ≤ 300 s) para aprobar pagos, registrar beneficiarios, activar el kill switch o crear API keys; bloqueo tras 5 intentos fallidos; rate limit en `/auth` | ✅ `Security/Authorization.cs`, `LocalTokenService.cs` |
| S2 | Robo de un refresh token | Rotación en cada uso; si se reutiliza, se revoca toda la familia y se alerta (`refresh_token_reuse`). `auth_time` y `amr` no se renuevan al refrescar | ✅ `RefreshTokenService.cs` |
| S3 | Token falsificado (`alg=none`, confusión HS/RS, otro emisor) | Lista cerrada de algoritmos (RS256/PS256/ES256), validación de emisor, audiencia, expiración y firma | ✅ `Program.cs` |
| S4 | Un tercero se hace pasar por el banco y envía webhooks | Firma HMAC/RSA/ECDSA sobre `ts.nonce.body`, ventana de ±5 min, nonce de un solo uso, allowlist de IP; además **el webhook solo dispara una consulta autenticada** | ✅ `WebhookVerifier.cs`, `WebhookService.cs` |
| S5 | DNS/MITM: la API habla con un falso banco | TLS 1.2/1.3 con validación de cadena y revocación, *pinning* SPKI con pin de respaldo, CA privada opcional, mTLS | ✅🔧 `CertificateStore.cs`, `HardenedHttpClientExtensions.cs` |
| S6 | API key de sandbox usada contra producción (o al revés) | Prefijo `agp_test_`/`agp_live_` validado y entorno guardado con la clave | ✅ `ApiKeyService.cs` |
| S7 | Notificaciones falsas hacia el backend | HMAC con timestamp y nonce en `X-Signature` | ✅ `HttpBackendAdapter.cs` (el backend debe verificar 📋) |

### T — Manipulación (Tampering)

| # | Amenaza | Mitigación | Estado |
|---|---|---|---|
| T1 | Modificar monto o beneficiario en tránsito hacia el banco | mTLS + firma de mensajes (JWS desacoplado / HMAC con método, ruta y cuerpo) | ✅🔧 `MessageSigners.cs` |
| T2 | Respuesta del banco alterada | Verificación de firma de respuesta; si falla, el resultado se trata como **ambiguo** (nunca "confirmado") y se alerta | ✅ `RealBankAdapter.cs` |
| T3 | Asignación masiva (`"status":"confirmed"` en el body) | DTOs explícitos, `UnmappedMemberHandling.Disallow`, sin claves duplicadas, números estrictos | ✅ `Program.cs`, `Contracts/Dtos.cs` |
| T4 | Alterar o borrar el historial (insider o DBA) | Libro append-only: el DbContext rechaza cambios; triggers y `DENY UPDATE/DELETE` en BD; cadena HMAC con clave fuera de la BD; cruce `Version` ↔ última secuencia para detectar truncado | ✅ `EfLedger.cs`, `db/sqlserver/002…` |
| T5 | Cambiar la cuenta destino de un beneficiario | El número se cifra con AES-GCM y el AAD incluye el tenant; no existe endpoint de edición (alta nueva + aprobación) | ✅ |
| T6 | XML malicioso (XXE, *billion laughs*) en respuestas ISO 20022/SOAP | DTD prohibido, sin `XmlResolver`, límite de tamaño | ✅ `SafeXml.cs` |
| T7 | Dependencia o imagen comprometida | Lock files + `--locked-mode`, NuGet audit, origen único con *source mapping*, CodeQL, Trivy, SBOM, firma cosign | ✅🔧 `ci.yml`, `nuget.config` |

### R — Repudio

| # | Amenaza | Mitigación | Estado |
|---|---|---|---|
| R1 | "Yo no aprobé ese pago" | Libro con actor, tipo de actor, MFA sí/no, IP, user-agent, correlationId y hora, para cada transición | ✅ `LedgerEntryEntity` |
| R2 | El banco niega haber recibido la orden | EndToEndId único y registrado; referencias del banco; conciliación diaria | ✅ |
| R3 | Acciones administrativas sin rastro | Kill switch y API keys auditados en el mismo libro | ✅ |
| R4 | Retención y no-repudio legal de los logs | Exportar el libro y los digests a almacenamiento WORM (Object Lock / Immutable Blob), con retención definida por normativa | 📋 |

### I — Divulgación de información

| # | Amenaza | Mitigación | Estado |
|---|---|---|---|
| I1 | BOLA/IDOR: leer pagos o cuentas de otro tenant | Todas las consultas filtran por `tenantId` del token; respuesta 404 (no 403); la pertenencia de cuentas la decide el backend | ✅ `Repositories.cs`, `AccountService.cs` |
| I2 | Exposición de números de cuenta | Cifrado AES-256-GCM por campo, huella HMAC para duplicados, respuestas solo con los últimos 4 dígitos, alias opacos | ✅ |
| I3 | Datos sensibles en logs y trazas | No se registran cuerpos; rutas con dígitos largos enmascaradas; `ToString` seguro de `BankCreditor`; errores de jobs sin mensaje de excepción; collector que elimina cabeceras de autenticación | ✅ |
| I4 | Errores que filtran detalles internos | problem+json uniforme con código y correlationId; detalles solo en logs | ✅ `GlobalExceptionHandler.cs` |
| I5 | Secretos en repo, imagen o variables | Gestor de secretos con prefijo por entorno y por integración; `appsettings.Development.json` excluido del publish; gitleaks en CI | ✅🔧 |
| I6 | SSRF hacia metadatos cloud o red interna | Allowlist de hosts + validación de IP en el socket (anti DNS rebinding) + sin redirecciones ni proxy | ✅ `SsrfGuard.cs` |
| I7 | Enumeración de usuarios | Mismo error para usuario inexistente, contraseña errada u OTP errado; hash ficticio para igualar tiempos | ✅ |

### D — Denegación de servicio

| # | Amenaza | Mitigación | Estado |
|---|---|---|---|
| D1 | Inundación de peticiones | Rate limit global por identidad/IP, más estricto en auth, dinero y webhooks | ✅ |
| D2 | Payloads enormes o lentos | Límite de cuerpo (64 KB, independiente del servidor), límites de cabeceras y de profundidad JSON, timeouts de Kestrel | ✅ |
| D3 | Banco lento arrastra a toda la API | Timeouts por intento y totales, circuit breaker, procesamiento asíncrono (la API no espera al banco) | ✅ |
| D4 | Respuestas gigantes o comprimidas del banco | `MaxResponseContentBufferSize`, descompresión desactivada, listas con tope | ✅ |
| D5 | DDoS volumétrico | WAF / CDN / protección del proveedor cloud delante del ingress | 📋 |

### E — Elevación de privilegios

| # | Amenaza | Mitigación | Estado |
|---|---|---|---|
| E1 | Un usuario aprueba su propio pago | Maker-checker en el dominio: quien crea no aprueba, y el checker debe ser humano con MFA reciente | ✅ `Payment.cs` |
| E2 | Una máquina (API key) realiza operaciones sensibles | Las API keys no pueden recibir scopes de aprobación ni de administración, y nunca cumplen step-up | ✅ |
| E3 | Endpoint sin protección por olvido | Política *fallback* = autenticado (deny by default) y scope explícito por endpoint | ✅ |
| E4 | Un administrador de tenant opera la plataforma | Scopes `platform:*` (kill switch, conciliación) separados de `admin:*` (API keys y auditoría del propio tenant) | ✅ |
| E5 | Contenedor comprometido escala en el host | Imagen *chiseled* sin shell, usuario no root, `read_only`, `cap_drop: ALL`, `no-new-privileges` | ✅🔧 |
| E6 | Un despliegue de sandbox apunta a producción | `EnvironmentGuard`: la aplicación no arranca con mocks, emisor local, secretos de configuración, SQLite, HTTP o hosts de sandbox en producción, ni con hosts de producción en sandbox | ✅ |

## Amenazas específicas del dinero

| # | Escenario | Defensa |
|---|---|---|
| M1 | **Doble débito** por reintento del cliente | `Idempotency-Key` obligatoria, índice único `(tenant, key)` en BD y hash de la petición (misma clave con otro contenido → 422) |
| M2 | **Doble débito** por timeout del banco | El POST de pago nunca se reintenta en el pipeline HTTP; timeout → `Unknown` → consulta; reenvío solo si el banco confirma `NotFound` y con el **mismo** EndToEndId |
| M3 | **Doble débito** por dos workers | *Lease* atómico del trabajo + concurrencia optimista del pago + handlers idempotentes por estado |
| M4 | Pago a cuenta del atacante | Lista blanca de beneficiarios con aprobación de un segundo usuario + MFA; revisión del primer pago y de montos anómalos |
| M5 | Fraude "bajo el umbral" | Límites diarios, velocidad y anomalía respecto al histórico; las reglas son configurables por moneda |
| M6 | Movimiento en el banco que no originamos | La conciliación detecta `missing_internally` (crítico) y alerta |
| M7 | Incidente en curso | Kill switch (variable de entorno inmediata o endpoint con MFA, propagado a todas las instancias en ≤ 2 s) |

## Riesgos residuales que hay que gestionar fuera del código

1. **Gestión de identidades en el IdP**: enrolamiento de MFA, recuperación de cuentas y altas/bajas de personal (JML).
2. **Custodia de claves**: HSM o KMS con políticas de acceso, separación de funciones y ceremonias de llaves para la clave de firma bancaria.
3. **Pruebas de penetración** independientes antes de producción y tras cambios mayores (requisito PCI-DSS 11.4 si aplica).
4. **Seguridad de red**: segmentación, *egress* restringido a los hosts del banco, WAF y protección DDoS.
5. **Contratos con el banco**: SLA, ventanas de mantenimiento, rangos IP de webhooks, procedimiento de rotación de certificados y de reversos.
6. **Fraude interno con colusión maker-checker**: controles de negocio (límites por rol, revisión de aprobaciones, rotación de funciones).
