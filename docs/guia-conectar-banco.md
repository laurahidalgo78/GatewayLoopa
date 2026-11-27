# Guía · Cómo conectar el banco

El puerto `IBankAdapter` (`src/ApiGatewayPay.Core/Ports/Bank/IBankAdapter.cs`) abstrae saldo, movimientos, iniciación de pagos, consulta de estado, extracto para conciliación y verificación de webhooks. `RealBankAdapter` es la plantilla configurable; casi siempre basta con configuración. Solo se escribe código si el formato de mensajes del banco no encaja con ningún codec.

## 0. Información que debes pedir al banco

| Tema | Pregunta | Dónde se configura |
|---|---|---|
| Ambientes | URL de sandbox y de producción; ventanas de mantenimiento | `Bank__Real__BaseUrl`, `Platform__ProductionBankHosts` |
| Formato | ¿REST/JSON propio, ISO 20022 (pain.001 / pain.002 / camt.052 / camt.053) o SOAP (WSDL)? | `Bank__Real__Format` |
| Autenticación | ¿OAuth2 client credentials? ¿private_key_jwt? ¿API key? ¿tokens ligados al certificado (RFC 8705)? | `Bank__Real__Auth__*` |
| mTLS | CSR, CA que firma, vigencia y procedimiento de renovación | `Bank__Real__Http__ClientCertificate__*` |
| Firma de mensajes | ¿JWS (PS256/ES256/RS256) desacoplado? ¿HMAC? ¿Cabecera? ¿Firman sus respuestas? | `Bank__Real__Signing__*` |
| CA del servidor | ¿CA pública o privada? Certificados para *pinning* (principal y respaldo) | `PinnedSpkiSha256`, `CustomRootCaSecretName` |
| Idempotencia | ¿Aceptan `Idempotency-Key` o deduplican por EndToEndId? (**imprescindible**) | ya se envían ambos |
| Estados | Códigos de estado y de rechazo (idealmente ISO 20022 `ExternalStatusReason1Code`) | codec |
| Webhooks | Formato, cabeceras de firma, algoritmo, rangos IP de origen, política de reintentos | `Bank__Webhooks__*` |
| Conciliación | ¿Extracto camt.053 o equivalente? ¿A qué hora queda disponible? | `Reconciliation__*` |

## 1. Certificados y claves

1. Genera la clave privada **dentro de tu KMS/HSM o Vault** si el banco lo permite. Si no, genérala en una máquina controlada y **destrúyela** tras importarla:
   ```bash
   openssl req -new -newkey rsa:3072 -nodes -keyout mtls.key -out mtls.csr -subj "/CN=apigatewaypay/O=TuEmpresa/C=CO"
   # El banco firma el CSR y devuelve mtls.crt (+ cadena)
   openssl pkcs12 -export -inkey mtls.key -in mtls.crt -certfile cadena.crt -out mtls.pfx
   base64 -w0 mtls.pfx > mtls.pfx.b64 && shred -u mtls.key
   ```
2. Sube los secretos **al espacio del entorno correcto** (ejemplo con Vault KV v2):
   ```bash
   vault kv put secret/sandbox/bank/mtls-client-pfx value=@mtls.pfx.b64
   vault kv put secret/sandbox/bank/mtls-client-pfx-password value='...'
   vault kv put secret/sandbox/bank/oauth-client-secret value='...'
   vault kv put secret/sandbox/bank/signing-private-key value=@firma.pem
   vault kv put secret/sandbox/bank/bank-public-key value=@banco-publica.pem
   vault kv put secret/sandbox/bank/webhook-secret value='...'
   ```
   Cada entorno tiene su propia política de Vault: el despliegue de sandbox no puede leer `secret/production/*`. La aplicación, además, rechaza nombres que intenten salir de su entorno.
3. **Pinning (opcional, recomendado si el banco lo acepta)**: calcula el pin del certificado intermedio o raíz, **más uno de respaldo**:
   ```bash
   openssl x509 -in intermedio.crt -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64
   ```

## 2. Configuración

```bash
Bank__Adapter=Real
Bank__Name=banco-x                           # la ruta de webhooks será /v1/webhooks/bank/banco-x
Bank__Real__BaseUrl=https://api.sandbox.banco-x.com
Bank__Real__Format=Iso20022                  # RestJson | Iso20022 | Soap
Bank__Real__Auth__Type=OAuth2ClientCredentials
Bank__Real__Auth__TokenUrl=https://auth.sandbox.banco-x.com/oauth2/token
Bank__Real__Auth__ClientId=...
Bank__Real__Auth__ClientSecretName=bank/oauth-client-secret
Bank__Real__Signing__Algorithm=JwsPs256      # None | HmacSha256 | JwsRs256 | JwsPs256 | JwsEs256
Bank__Real__Signing__KeySecretName=bank/signing-private-key
Bank__Real__Signing__KeyId=kid-registrado-en-el-banco
Bank__Real__Signing__HeaderName=x-jws-signature
Bank__Real__Signing__VerifyResponses=true
Bank__Real__Signing__BankKeySecretName=bank/bank-public-key
Bank__Real__Http__ClientCertificate__PfxSecretName=bank/mtls-client-pfx
Bank__Real__Http__ClientCertificate__PfxPasswordSecretName=bank/mtls-client-pfx-password
Bank__Real__Http__PinnedSpkiSha256__0=<pin-principal>
Bank__Real__Http__PinnedSpkiSha256__1=<pin-respaldo>
Bank__Real__Endpoints__Payments=/v2/transfers     # ajusta las rutas al banco
Bank__Real__Endpoints__PaymentStatus=/v2/transfers/{endToEndId}
Bank__Real__Iso20022__DebtorName=TU EMPRESA SAS
Bank__Real__Iso20022__DebtorAgentBic=BANCOXXX
Bank__Webhooks__Algorithm=RsaSha256          # o HmacSha256
Bank__Webhooks__SecretName=bank/webhook-public-key
Bank__Webhooks__SignatureHeader=X-Signature
Bank__Webhooks__AllowedCidrs__0=203.0.113.0/24   # rangos IP publicados por el banco
```

El host de `BaseUrl` y el de `TokenUrl` se añaden solos a la allowlist del cliente `bank` / `bank-auth`. No hace falta abrir nada más.

## 3. Ajustar el formato (solo si hace falta)

- **REST/JSON propio**: modifica los métodos `Build*`/`Parse*` de `RestJsonBankCodec`. Contrato canónico en `contracts/bank-restjson.schema.json`. Conserva las validaciones: campos requeridos, `EndToEndId` coincidente, moneda ISO y montos ≥ 0.
- **ISO 20022**: `Iso20022BankCodec` genera pain.001.001.09 y lee pain.002, camt.052 y camt.053 por nombre local. Revisa la *usage guideline* del banco (campos obligatorios, `ClrSysMmbId` vs. `BICFI`, esquema de cuenta).
- **SOAP**: `SoapBankCodec` es una plantilla. Ajusta las operaciones y elementos al WSDL. Si exigen WS-Security, implementa un `IMessageSigner` dedicado.
- **Un banco totalmente distinto**: crea `MiBancoAdapter : IBankAdapter` y regístralo en `AddBank` con un nuevo valor de `Bank:Adapter`. **Respeta el contrato de errores**: `InitiatePaymentAsync` nunca lanza por timeouts, sino que devuelve `Ambiguous`.

### Clasificación de resultados (no la cambies sin entenderla)

| Respuesta del banco a la iniciación | Resultado | Efecto |
|---|---|---|
| 2xx con estado aceptado | `Accepted` | Se espera la liquidación (webhook o consulta) |
| 2xx con rechazo, 400, 422 | `Rejected` | Pago `Failed` (definitivo) |
| Timeout, conexión cortada, 5xx, 401, 403, 408, 409, 429, firma inválida, cuerpo ilegible | `Ambiguous` | `Unknown` → **consulta de estado**; nunca se reenvía a ciegas |
| Consulta de estado: 404 / `NOT_FOUND` (pasada la gracia) | — | Reenvío con el **mismo** EndToEndId (máx. `Payments__MaxSubmissionAttempts`) |

## 4. Pruebas antes de producción

1. **Tests contra stubs**: `RealBankAdapterTests` muestra cómo probar la clasificación con un `HttpMessageHandler` falso. Añade casos con respuestas reales (anonimizadas) del sandbox del banco.
2. **Sandbox del banco**: con `Platform__Environment=Sandbox`, ejecuta `tools/demo-e2e.ps1` (cambia montos según los casos de prueba del banco) y verifica:
   - pago aceptado → confirmado (webhook y consulta);
   - rechazo con código;
   - timeout forzado (si el sandbox lo permite) → `unknown` → resuelto **sin doble débito**;
   - webhook con firma inválida → 401, y métrica `agp_webhooks_rejected_total`;
   - conciliación del día sin discrepancias.
3. **Kill switch**: actívalo y comprueba que ninguna llamada sale hacia el banco (métrica `agp_outbound_calls_total{integration="bank"}` sin cambios).
4. **Rotación de certificado**: sube un PFX nuevo a Vault y comprueba en los logs `Certificado mTLS de bank rotado` en ≤ 30 min, sin reiniciar.

## 5. Paso a producción

1. Repite el paso 1 con secretos en `production/` y una política de Vault distinta.
2. `Platform__Environment=Production` y añade el host de producción del banco a `Platform__ProductionBankHosts` **también en los despliegues de sandbox** (así un sandbox mal configurado no arranca).
3. Recorre el [checklist de producción](checklist-produccion.md).
