# Checklist de seguridad y cumplimiento antes de producción

Marque cada punto con evidencia (enlace a ticket, captura o reporte). Un punto sin evidencia se considera no cumplido.

## Configuración y entornos
- [ ] `Platform__Environment=Production` y la aplicación **arranca** (el `EnvironmentGuard` no reporta violaciones).
- [ ] Los despliegues de sandbox incluyen `Platform__ProductionBankHosts` (no pueden apuntar al banco productivo).
- [ ] `Auth__Mode=Oidc` contra el IdP productivo; `RequireHttpsMetadata=true`; audiencia propia.
- [ ] `OpenApi__Enabled=false`; `Cors__AllowedOrigins` vacío o solo con orígenes propios.
- [ ] `AllowedHosts` con el hostname público; `Network__TrustedProxyNetworks` solo con el ingress/LB.
- [ ] `Security__RequireHttps=true`; TLS terminado con certificado válido; HSTS activo.
- [ ] Ningún secreto en appsettings, variables de la imagen, `docker-compose` ni repositorio (gitleaks en verde sobre **todo** el historial).

## Identidad y acceso
- [ ] MFA obligatorio en el IdP para todos los usuarios con `payments:approve`, `beneficiaries:*`, `admin:*` y `platform:*`.
- [ ] El IdP emite `acr`/`amr` y `auth_time`; un usuario sin MFA reciente recibe 401 `step_up_required` al aprobar (prueba manual).
- [ ] Refresh tokens con rotación y detección de reutilización activas en el IdP; vida del access token ≤ 5–10 min.
- [ ] Mapeo de claims verificado (`Auth__Claims__Tenant`, `Auth__Claims__Scope`).
- [ ] Matriz de roles → scopes aprobada por negocio; el maker y el checker son personas distintas.
- [ ] Usuarios de desarrollo y emisor local **no** presentes.

## Banco
- [ ] mTLS con certificado productivo; vencimiento ≥ 60 días; alerta `CertificateExpiringSoon` probada.
- [ ] Pinning con **dos** pins (principal y respaldo) o CA privada configurada; procedimiento de rotación acordado con el banco.
- [ ] Firma de peticiones activa y verificación de respuestas (`VerifyResponses=true`) acordada con el banco.
- [ ] El banco deduplica por EndToEndId / `Idempotency-Key` (confirmado **por escrito**).
- [ ] Webhooks: algoritmo y secreto o clave pública productivos, `AllowedCidrs` con los rangos del banco, tolerancia ≤ 300 s.
- [ ] Pruebas en el sandbox del banco: aceptado, rechazado, timeout ambiguo **sin doble débito**, webhook inválido, conciliación limpia.
- [ ] Límites antifraude (`Fraud__Limits__*`) aprobados por riesgo/negocio para cada moneda.
- [ ] Kill switch probado en producción (activar → 503 → desactivar) y documentado para guardia.

## Datos
- [ ] Claves `crypto/field-key-v1`, `crypto/fingerprint-key` y `ledger/hmac-key` generadas en KMS/Vault, **distintas** de las de sandbox, con respaldo seguro (perderlas = perder los datos o la verificación del libro).
- [ ] SQL Server: scripts `001` y `002` aplicados; la app usa el rol `agp_app` (sin DDL ni UPDATE/DELETE en el libro); identidad administrada o integrada.
- [ ] TDE / cifrado en reposo y backups cifrados con restauración probada.
- [ ] (Recomendado) Tabla `ledger_entries` como LEDGER APPEND_ONLY + digests en almacenamiento inmutable.
- [ ] Exportación periódica del libro a almacenamiento WORM con la retención legal definida.
- [ ] `GET /v1/admin/ledger/verify` devuelve `valid=true` después de las pruebas.

## Plataforma y cadena de suministro
- [ ] Pipeline en verde: build con warnings como errores, tests, NuGet audit, dependency review, CodeQL, gitleaks, Trivy (sin HIGH/CRITICAL), SBOM.
- [ ] Imagen firmada (cosign) y verificación de firma en el despliegue (admission controller).
- [ ] Actions fijadas por SHA; *branch protection* que exija los jobs y la revisión de un segundo desarrollador.
- [ ] Contenedor no root, sistema de archivos de solo lectura, `cap_drop: ALL`, límites de CPU y memoria.
- [ ] Egress de red restringido a los hosts del banco, del backend, del IdP, de Vault y de los proveedores.
- [ ] Sondas: liveness `/health`, readiness `/ready`.
- [ ] Una sola réplica ejecuta la conciliación (o se programa como job externo).

## Observabilidad y respuesta
- [ ] `Telemetry__OtlpEndpoint` configurado; métricas `agp_*` visibles; reglas de `deploy/alerts.yml` cargadas y enrutadas a guardia.
- [ ] Logs JSON enviados al SIEM; verificado que no contienen tokens, números de cuenta completos ni cuerpos.
- [ ] Servicio de alertas (`ExternalServices` de tipo `Alerting`) habilitado y probado.
- [ ] [Plan de respuesta a incidentes](respuesta-incidentes.md) revisado, con responsables y ensayado (simulacro de kill switch y de rotación de credenciales).

## Cumplimiento
- [ ] Matriz de [cumplimiento](cumplimiento.md) revisada por cumplimiento, el oficial de datos y seguridad.
- [ ] Pentest externo sin hallazgos críticos o altos abiertos.
- [ ] Contratos con el banco y los proveedores (transmisión de datos, SLA, incidentes) firmados.
- [ ] Bases de datos inscritas en el RNBD; política de tratamiento publicada.
- [ ] Si hay tarjetas: alcance PCI definido y validado por un QSA.
