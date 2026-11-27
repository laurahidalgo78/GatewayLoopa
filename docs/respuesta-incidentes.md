# Plan de respuesta a incidentes y rotación de credenciales de emergencia

## Roles

| Rol | Responsabilidad |
|---|---|
| Líder del incidente | Decide, coordina y comunica; abre el registro del incidente |
| Operador de plataforma (`platform:killswitch`) | Ejecuta el kill switch y las rotaciones |
| Enlace con el banco | Canal 24/7 acordado contractualmente |
| Cumplimiento / datos | Evalúa notificaciones (SFC, SIC, titulares; GDPR 72 h si aplica) |

## Severidades

| Sev | Ejemplos | Acción inicial |
|---|---|---|
| **1** | Pagos no autorizados; credenciales del banco o clave de firma filtradas; discrepancia `missing_internally`; libro con `valid=false` | **Kill switch inmediato** |
| 2 | Webhooks con firma inválida en masa; reutilización de refresh tokens; banco con respuestas inconsistentes | Evaluar kill switch; contener |
| 3 | Circuit breaker abierto; certificado por vencer; pagos atascados | Seguimiento en horario laboral |

## <a id="kill-switch"></a>1. Contener: kill switch

Opción A (con MFA, auditada y propagada a todas las instancias en ≤ 2 s):
```bash
curl -X POST https://api.tu-dominio.com/v1/admin/kill-switch \
  -H "Authorization: Bearer $TOKEN_OPERADOR_CON_MFA" -H "Content-Type: application/json" \
  -d '{"active": true, "reason": "INC-123 sospecha de credenciales filtradas"}'
```
Opción B (si el IdP o la API no responden): `Bank__KillSwitch=true` en el despliegue y reinicio. No depende de la base de datos.

Efecto: se rechazan pagos y aprobaciones nuevos (503 `bank_connection_disabled`), los workers no envían ni consultan al banco (los trabajos esperan) y `/ready` reporta `Degraded`. Los webhooks se siguen guardando en la inbox, pero no se confían: su efecto pasa por una consulta al banco, que también está suspendida.

## 2. Investigar

1. `GET /v1/admin/ledger/verify` (por tenant): integridad del libro.
2. Consulta del libro por correlationId, actor, IP y rango de tiempo (tabla `ledger_entries`).
3. Conciliación manual del día: `POST /v1/admin/reconciliation/runs {"date": "AAAA-MM-DD"}`.
4. Métricas `agp_auth_failures_total`, `agp_webhooks_rejected_total`, `agp_alerts_total{code=...}` y logs del SIEM por correlationId.
5. Trabajos en dead-letter: `SELECT * FROM jobs WHERE Status = 'Dead'`.

## 3. Rotación de credenciales de emergencia

Los secretos se cachean un máximo de `Secrets__CacheSeconds` (300 s por defecto). Tras rotar en Vault, **reinicie las instancias** para que el cambio sea inmediato.

| Credencial comprometida | Pasos |
|---|---|
| **Certificado mTLS del banco** | 1) Kill switch. 2) Pedir al banco la revocación. 3) Generar CSR nuevo (clave nueva), obtener el certificado y subir `bank/mtls-client-pfx` (+ password). 4) Reiniciar o esperar al `CertificateMonitor` (≤ 30 min). 5) Desactivar el kill switch. |
| **Clave de firma (JWS/HMAC) hacia el banco** | Kill switch → generar clave nueva (idealmente en HSM/Vault Transit) → registrar el nuevo `kid` en el banco → actualizar `bank/signing-private-key` y `Bank__Real__Signing__KeyId` → pedir al banco que revoque el `kid` anterior. |
| **Client secret OAuth2 del banco** | Rotar en el portal del banco → actualizar `bank/oauth-client-secret` → reiniciar (el token cacheado caduca solo). |
| **Secreto de webhooks** | Coordinar con el banco → actualizar `bank/webhook-secret`. Durante la ventana habrá rechazos (`agp_webhooks_rejected_total`): no se pierde nada, porque las consultas de estado siguen resolviendo los pagos. |
| **API key de un cliente** | `DELETE /v1/admin/api-keys/{id}` (efecto inmediato) → emitir una nueva. |
| **Pepper de API keys** (`auth/api-key-pepper`) | Invalida **todas** las API keys: rotar y re-emitir a todos los clientes. |
| **Clave de cifrado de campos** | Crear `crypto/field-key-v{n+1}` → `Crypto__ActiveKeyVersion=n+1` → re-cifrar beneficiarios con un job de mantenimiento → retirar la versión anterior. |
| **Clave HMAC del libro** | No se "rota" sobre el pasado: conservar la clave anterior para verificar el histórico (archivar la cadena hasta el corte) e iniciar una época nueva. Requiere un cambio planificado (versión de clave por entrada). |
| **Cuenta de usuario / IdP** | Revocar sesiones y refresh tokens en el IdP, forzar el re-enrolamiento de MFA y revisar en el libro las aprobaciones de ese usuario. |
| **Cadena de conexión de la BD** | Rotar la contraseña (o pasar a identidad administrada) → actualizar `db/connection-string` → reiniciar. |

## <a id="discrepancias-de-conciliacion"></a>4. Discrepancias de conciliación

| Tipo | Significado | Acción |
|---|---|---|
| `missing_internally` (crítica) | El banco tiene un débito que no originamos | **Sev 1**: kill switch, contactar al banco, revisar credenciales |
| `settled_but_failed` (crítica) | Salió el dinero de un pago marcado como fallido | Corregir el estado con un procedimiento manual auditado; avisar al backend |
| `amount_mismatch` / `duplicate_in_bank` (crítica) | Monto distinto o doble débito | Sev 1 con el banco; iniciar reverso |
| `missing_in_bank` (alta) | Confirmado internamente sin reflejo en el extracto | Verificar con el banco (puede ser un corte de día) |
| `settled_pending_confirmation` (media) | El banco liquidó y aún no lo registramos | Se resuelve solo (la conciliación programa una consulta) |

## 5. Recuperar y cerrar

1. Verificar que no queden pagos en `Unknown`/`Sent` sin resolver ni trabajos en dead-letter.
2. Desactivar el kill switch (queda auditado) y vigilar métricas durante 1 h.
3. Postmortem sin culpables en ≤ 5 días hábiles: causa raíz, cronología desde el libro y acciones preventivas.
4. Notificaciones regulatorias o a titulares en los plazos que defina cumplimiento.
