/* =============================================================================
   ApiGatewayPay - Rollback - 001 - Esquema inicial
   ¡ATENCIÓN! Elimina TODAS las tablas, incluido el libro de transacciones.
   En producción el libro NO debe eliminarse: exporte y archive antes (retención legal).
   ============================================================================= */
SET XACT_ABORT ON;
GO
IF OBJECT_ID('dbo.trg_ledger_append_only', 'TR') IS NOT NULL DROP TRIGGER dbo.trg_ledger_append_only;
GO
DROP TABLE IF EXISTS dbo.reconciliation_discrepancies;
DROP TABLE IF EXISTS dbo.reconciliation_runs;
DROP TABLE IF EXISTS dbo.refresh_tokens;
DROP TABLE IF EXISTS dbo.api_keys;
DROP TABLE IF EXISTS dbo.webhook_nonces;
DROP TABLE IF EXISTS dbo.webhook_inbox;
DROP TABLE IF EXISTS dbo.kill_switches;
DROP TABLE IF EXISTS dbo.jobs;
DROP TABLE IF EXISTS dbo.ledger_entries;
DROP TABLE IF EXISTS dbo.beneficiaries;
DROP TABLE IF EXISTS dbo.payments;
GO
