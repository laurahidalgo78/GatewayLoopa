/* =============================================================================
   ApiGatewayPay - 002 - Endurecimiento del libro inmutable y mínimo privilegio
   Defensa en profundidad del libro (además del encadenamiento HMAC en la aplicación):
     1. Trigger INSTEAD OF UPDATE/DELETE: la propia BD rechaza alterar el libro.
     2. Rol de aplicación sin UPDATE/DELETE/ALTER sobre el libro ni permisos DDL.
     3. (Recomendado en SQL Server 2022 / Azure SQL) tabla LEDGER APPEND_ONLY: ver bloque final.
   Rollback: "Rollback - 002 - Endurecimiento libro inmutable y permisos.sql"
   ============================================================================= */
SET XACT_ABORT ON;
GO

IF OBJECT_ID('dbo.trg_ledger_append_only', 'TR') IS NULL
    EXEC('CREATE TRIGGER dbo.trg_ledger_append_only ON dbo.ledger_entries INSTEAD OF UPDATE, DELETE
          AS THROW 51000, ''ledger_entries is append-only'', 1;');
GO

/* Rol de la aplicación: solo DML sobre tablas operativas; sobre el libro solo SELECT/INSERT. */
IF DATABASE_PRINCIPAL_ID('agp_app') IS NULL CREATE ROLE agp_app;
GO
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.payments TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.beneficiaries TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.jobs TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.kill_switches TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.webhook_inbox TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.webhook_nonces TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.api_keys TO agp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.refresh_tokens TO agp_app;
GRANT SELECT, INSERT ON dbo.reconciliation_runs TO agp_app;
GRANT SELECT, INSERT ON dbo.reconciliation_discrepancies TO agp_app;
GRANT SELECT, INSERT ON dbo.ledger_entries TO agp_app;
DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON dbo.ledger_entries TO agp_app;
DENY ALTER ON SCHEMA::dbo TO agp_app;
GO

/* Usuario de la aplicación: preferir identidad administrada (Azure) o autenticación integrada (sin contraseña).
   Ejemplo con login SQL (la contraseña vive en el gestor de secretos, nunca en este script):
   CREATE USER agp_app_user FOR LOGIN agp_app_login;
   ALTER ROLE agp_app ADD MEMBER agp_app_user;
*/

/* -----------------------------------------------------------------------------
   Opcional (SQL Server 2022+ / Azure SQL): tabla LEDGER APPEND_ONLY.
   SQL Server mantiene un digest criptográfico verificable con sp_verify_database_ledger.
   Debe crearse así desde el inicio (no se puede convertir una tabla existente):

   CREATE TABLE dbo.ledger_entries ( ...mismas columnas que en 001... )
   WITH (LEDGER = ON (APPEND_ONLY = ON));

   Y publicar los digests en almacenamiento inmutable (Azure Immutable Blob / S3 Object Lock):
   ALTER DATABASE SCOPED CONFIGURATION SET LEDGER_DIGEST_STORAGE_ENDPOINT = 'https://<cuenta>.blob.core.windows.net';
   ----------------------------------------------------------------------------- */

/* Cifrado en reposo: habilitar TDE (Azure SQL lo trae activo) y backups cifrados.
   Los números de cuenta ya llegan cifrados (AES-256-GCM) desde la aplicación. */
