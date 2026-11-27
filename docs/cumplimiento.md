# Cumplimiento: qué controles cubre el software y qué queda fuera

> **Importante**: este documento es una guía técnica de diseño, no un concepto jurídico. Valide la vigencia y el alcance de cada norma con su área de cumplimiento, el oficial de protección de datos y, si aplica, el QSA de PCI. Las circulares de la Superintendencia Financiera de Colombia (SFC) se actualizan con frecuencia.

Leyenda: ✅ control técnico implementado · 🔧 soportado, requiere configuración o integración · 📋 fuera del software (política, proceso, contrato, auditoría)

## PCI-DSS v4.0.1 (solo si se procesan tarjetas)

**Decisión de diseño**: la base **no almacena, procesa ni transmite datos de tarjeta** (PAN, CVV o banda). Si se agregan pagos con tarjeta, deben integrarse mediante una pasarela certificada que tokenice en el navegador o la app (campos hospedados), de modo que la API solo maneje tokens. Esto mantiene el alcance en el nivel mínimo (SAQ A o A-EP según la integración).

| Requisito | Cobertura en el software | Fuera del software |
|---|---|---|
| 1 Controles de red | 🔧 Egress solo hacia hosts en allowlist, anti-SSRF | 📋 Segmentación, firewalls, WAF |
| 2 Configuraciones seguras | ✅ `EnvironmentGuard`, imagen chiseled no root, cabeceras, CORS cerrado | 📋 Hardening del host y del clúster |
| 3 Protección de datos almacenados | ✅ Sin PAN/CVV; cifrado de campo AES-256-GCM con rotación de claves; enmascaramiento (últimos 4) | 📋 Política de retención, gestión de claves (KMS/HSM) |
| 4 Cifrado en tránsito | ✅ TLS 1.2/1.3, mTLS hacia el banco, HSTS | 📋 Certificados públicos y su gestión |
| 5 Antimalware | — | 📋 En los hosts y en el pipeline |
| 6 Desarrollo seguro | ✅ SAST (CodeQL), SCA (NuGet audit, dependency review), escaneo de imagen, revisión vía PR, tests de seguridad; 6.4 protección de la API pública (validación, rate limit) | 📋 Capacitación en desarrollo seguro, gestión de cambios |
| 7 Acceso por necesidad de conocer | ✅ Scopes por endpoint, tenant en cada consulta, separación `admin:*` / `platform:*` | 📋 Revisión periódica de accesos |
| 8 Identificación y autenticación | ✅ MFA para operaciones sensibles (8.4), tokens cortos, bloqueo por intentos, API keys hasheadas y expirables | 📋 IdP: políticas de contraseña, MFA para todo acceso administrativo |
| 10 Registro y monitoreo | ✅ Logs JSON con correlationId, libro inmutable con actor/IP/hora, métricas y alertas | 📋 SIEM, retención ≥ 12 meses (3 disponibles de inmediato), revisión diaria |
| 11 Pruebas de seguridad | ✅ Tests automáticos | 📋 Pentest anual y tras cambios significativos, escaneos ASV trimestrales |
| 12 Políticas | — | 📋 Política de seguridad, respuesta a incidentes, gestión de proveedores |

## ISO/IEC 27001:2022 (controles del Anexo A)

| Control | Cobertura técnica |
|---|---|
| 5.15 Control de acceso · 8.3 Restricción de acceso a la información | ✅ RBAC por scopes + ABAC (tenant, tipo de actor, MFA, maker-checker) |
| 5.17 Información de autenticación · 8.5 Autenticación segura | ✅ PBKDF2 600k, TOTP, step-up, rotación de refresh tokens, hash de API keys |
| 8.2 Derechos de acceso privilegiado | ✅ Operaciones de plataforma separadas y con MFA; 📋 PAM para acceso a infraestructura |
| 8.9 Gestión de la configuración | ✅ Configuración declarativa, validada al arrancar, `.env.example` documentado |
| 8.11 Enmascaramiento de datos · 8.12 Prevención de fuga de datos | ✅ Enmascaramiento en respuestas y logs; DTOs mínimos |
| 8.15 Registro · 8.16 Actividades de monitoreo | ✅ Logs estructurados, libro inmutable, métricas OTel, reglas de alerta |
| 8.20/8.21 Seguridad de redes y servicios | ✅ mTLS, pinning, allowlist; 📋 red |
| 8.24 Uso de criptografía | ✅ AES-GCM, HMAC-SHA256, ECDSA/RSA-PSS, TLS 1.2+; 📋 política criptográfica y ciclo de vida de claves |
| 8.25 Ciclo de vida de desarrollo seguro · 8.28 Codificación segura · 8.29 Pruebas de seguridad | ✅ Pipeline con SAST/SCA/secretos/imagen; 178 tests |
| 8.31 Separación de entornos | ✅ Sandbox/Producción con secretos y API keys separadas y bloqueo de mezcla |
| 8.32 Gestión de cambios | 🔧 PR + CI obligatorio + aprobación manual del entorno de producción |
| 8.8 Gestión de vulnerabilidades técnicas | ✅ Escaneo semanal programado, Dependabot |
| 5.24–5.28 Gestión de incidentes | ✅ Kill switch, alertas; 📋 [plan de respuesta](respuesta-incidentes.md) operado y ensayado |
| 5.19–5.22 Proveedores · 5.23 Servicios en la nube | 📋 Contratos, evaluación de terceros (banco, IdP, cloud) |
| 8.13 Copias de respaldo | 📋 Backups cifrados y probados de la BD (el libro debe poder restaurarse íntegro) |

## Superintendencia Financiera de Colombia (SFC)

Aplica si la entidad es vigilada por la SFC o presta servicios a vigiladas (como tercero o proveedor tecnológico). Referencias principales:

| Marco | Requisitos relevantes | Cobertura |
|---|---|---|
| **Circular Básica Jurídica, Parte I, Título II, Capítulo I** (canales, medios, seguridad y calidad de la información) | Cifrado fuerte, autenticación robusta (doble factor para operaciones monetarias), registro de operaciones con trazabilidad, límites por canal, mecanismos para detectar operaciones inusuales, notificación al cliente | ✅ TLS/mTLS, MFA y step-up, libro con IP/hora/actor, límites por monto y frecuencia, antifraude; 🔧 notificaciones vía `IExternalService` (SMS/correo); 📋 definición de límites por producto |
| **Gestión del riesgo de ciberseguridad** (CE 007 de 2018, incorporada a la CBJ, Parte I, Título IV) | Gestión de incidentes, monitoreo, pruebas, reporte de incidentes a la SFC, gestión de terceros | ✅ Monitoreo y alertas, kill switch, pruebas automatizadas; 📋 SOC, reporte de incidentes en los plazos exigidos, plan de continuidad |
| **Riesgo operativo (SARO / sistema integral de administración de riesgos)** en la Circular Básica Contable y Financiera | Registro de eventos de riesgo, controles sobre procesos críticos | ✅ Eventos auditables (discrepancias, rechazos, kill switch); 📋 matriz de riesgos y registro de eventos |
| **Computación en la nube** (instrucciones de la SFC sobre uso de servicios en la nube) | Cifrado, gestión de claves, ubicación de datos, auditoría del proveedor | 🔧 Claves en KMS/HSM propio; 📋 contrato y evaluación del proveedor cloud |

## Ley 1581 de 2012 (protección de datos personales / habeas data) y Decreto 1377 de 2013 (compilado en el Decreto 1074 de 2015)

| Principio o deber | Cobertura técnica | Fuera del software |
|---|---|---|
| Seguridad (art. 4 lit. g) | ✅ Cifrado, control de acceso, auditoría | 📋 Política de tratamiento de la información |
| Acceso y circulación restringida (art. 4 lit. f) | ✅ Aislamiento por tenant, mínimo privilegio, secretos por integración | 📋 Contratos de transmisión o transferencia con encargados (banco, SMS, KYC) |
| Confidencialidad (art. 4 lit. h) | ✅ Enmascaramiento en respuestas y logs; minimización (solo nombre y número de cuenta cifrado) | 📋 Acuerdos de confidencialidad |
| Finalidad y libertad (autorización) | — | 📋 Autorización previa e informada del titular (la gestiona el backend o el front) |
| Derechos del titular: consulta y reclamo (arts. 14 y 15) | 🔧 El backend puede exponerlos; la API no guarda más datos personales de los necesarios | 📋 Procedimiento y tiempos de respuesta |
| Registro Nacional de Bases de Datos (SIC) | — | 📋 Inscripción y actualización |
| Habeas data financiero (Ley 1266 de 2008), si se reporta información crediticia | — | 📋 Fuera del alcance actual |

**Tensión con la supresión**: el libro es inmutable por exigencias de auditoría financiera. Por eso **no guarda datos personales directos**: usa identificadores, montos, estados y últimos 4 dígitos. La supresión de un titular se hace en las tablas operativas (o con *crypto-shredding*, destruyendo la clave de cifrado de sus campos), mientras el libro conserva solo datos seudonimizados. Valide este enfoque con el oficial de protección de datos.

## GDPR (si hay titulares en la UE)

| Artículo | Cobertura |
|---|---|
| Art. 25 Protección de datos desde el diseño | ✅ Minimización, seudonimización, enmascaramiento por defecto |
| Art. 32 Seguridad del tratamiento | ✅ Cifrado, resiliencia (circuit breaker, cola durable), pruebas periódicas en CI |
| Art. 30 Registro de actividades | 📋 Registro de actividades de tratamiento |
| Art. 33 Notificación de brechas (72 h) | ✅ Alertas y libro para la investigación; 📋 procedimiento de notificación |
| Art. 17 Derecho de supresión | 🔧 Ver la nota sobre libro inmutable y crypto-shredding |
| Art. 44+ Transferencias internacionales | 📋 Cláusulas contractuales tipo, ubicación de datos |

## Resumen de lo que queda fuera del software

1. Políticas: seguridad de la información, tratamiento de datos, criptografía y retención.
2. Gestión de identidades del personal y del IdP (JML, revisiones de acceso).
3. Custodia de claves en HSM o KMS, con separación de funciones.
4. Contratos con el banco, la nube, el IdP y los proveedores de SMS/KYC (incluidos acuerdos de transmisión de datos).
5. Pruebas de penetración, escaneos ASV y auditorías (ISO 27001, PCI, revisoría o auditoría SFC).
6. SOC/SIEM 24/7, reporte de incidentes a las autoridades y plan de continuidad del negocio.
7. Inscripción en el RNBD (SIC) y atención de derechos de los titulares.
