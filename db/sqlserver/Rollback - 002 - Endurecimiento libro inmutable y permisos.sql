/* ApiGatewayPay - Rollback - 002 - Endurecimiento libro inmutable y permisos */
SET XACT_ABORT ON;
GO
IF OBJECT_ID('dbo.trg_ledger_append_only', 'TR') IS NOT NULL DROP TRIGGER dbo.trg_ledger_append_only;
GO
IF DATABASE_PRINCIPAL_ID('agp_app') IS NOT NULL
BEGIN
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.payments FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.beneficiaries FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.jobs FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.kill_switches FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.webhook_inbox FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.webhook_nonces FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.api_keys FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE ON dbo.refresh_tokens FROM agp_app;
    REVOKE SELECT, INSERT ON dbo.reconciliation_runs FROM agp_app;
    REVOKE SELECT, INSERT ON dbo.reconciliation_discrepancies FROM agp_app;
    REVOKE SELECT, INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON dbo.ledger_entries FROM agp_app;
    REVOKE ALTER ON SCHEMA::dbo FROM agp_app;
    DROP ROLE agp_app;
END
GO
