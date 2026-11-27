/* =============================================================================
   ApiGatewayPay - 001 - Esquema inicial (SQL Server 2019+ / Azure SQL)
   Generado desde el modelo EF Core:
     dotnet run --project src/ApiGatewayPay.Api --no-launch-profile -- --print-sqlserver-schema
   Ejecutar con sqlcmd o SSMS (modo SQLCMD no requerido; usa lotes GO).
   Rollback: "Rollback - 001 - Esquema inicial.sql"
   ============================================================================= */
SET XACT_ABORT ON;
GO

CREATE TABLE [api_keys] (
    [Id] nvarchar(32) NOT NULL,
    [TenantId] nvarchar(64) NOT NULL,
    [Name] nvarchar(512) NOT NULL,
    [SecretHash] nvarchar(64) NOT NULL,
    [Scopes] nvarchar(512) NOT NULL,
    [Environment] nvarchar(512) NOT NULL,
    [CreatedBy] nvarchar(512) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    [RevokedAt] datetime2 NULL,
    [RevokedBy] nvarchar(512) NULL,
    [LastUsedAt] datetime2 NULL,
    CONSTRAINT [PK_api_keys] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [beneficiaries] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] nvarchar(64) NOT NULL,
    [DisplayName] nvarchar(140) NOT NULL,
    [BankCode] nvarchar(16) NOT NULL,
    [AccountType] nvarchar(32) NOT NULL,
    [AccountNumberCipher] nvarchar(256) NOT NULL,
    [AccountLast4] nvarchar(4) NOT NULL,
    [AccountFingerprint] nvarchar(64) NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [CreatedBy] nvarchar(512) NOT NULL,
    [ApprovedBy] nvarchar(512) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [ActivatedAt] datetime2 NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_beneficiaries] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [jobs] (
    [Id] uniqueidentifier NOT NULL,
    [Type] nvarchar(64) NOT NULL,
    [Payload] nvarchar(4000) NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [Attempts] int NOT NULL,
    [AvailableAt] datetime2 NOT NULL,
    [LockedUntil] datetime2 NULL,
    [LockedBy] nvarchar(512) NULL,
    [LastError] nvarchar(512) NULL,
    [CorrelationId] nvarchar(512) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_jobs] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [kill_switches] (
    [Id] nvarchar(32) NOT NULL,
    [Active] bit NOT NULL,
    [Reason] nvarchar(512) NULL,
    [ChangedBy] nvarchar(512) NULL,
    [ChangedAt] datetime2 NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_kill_switches] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [ledger_entries] (
    [Sequence] bigint NOT NULL IDENTITY,
    [Id] uniqueidentifier NOT NULL,
    [TenantId] nvarchar(64) NOT NULL,
    [AggregateType] nvarchar(32) NOT NULL,
    [AggregateId] nvarchar(64) NOT NULL,
    [AggregateSequence] int NOT NULL,
    [EventType] nvarchar(512) NOT NULL,
    [FromStatus] nvarchar(512) NULL,
    [ToStatus] nvarchar(512) NULL,
    [AmountMinor] bigint NULL,
    [Currency] nvarchar(512) NULL,
    [ActorType] nvarchar(512) NOT NULL,
    [ActorId] nvarchar(512) NOT NULL,
    [StrongAuthentication] bit NOT NULL,
    [SourceIp] nvarchar(512) NULL,
    [UserAgent] nvarchar(512) NULL,
    [CorrelationId] nvarchar(512) NOT NULL,
    [OccurredAt] datetime2 NOT NULL,
    [Detail] nvarchar(512) NULL,
    [PreviousHash] nvarchar(64) NOT NULL,
    [Hash] nvarchar(64) NOT NULL,
    CONSTRAINT [PK_ledger_entries] PRIMARY KEY ([Sequence])
);
GO


CREATE TABLE [payments] (
    [Id] uniqueidentifier NOT NULL,
    [TenantId] nvarchar(64) NOT NULL,
    [CreatedBy] nvarchar(512) NOT NULL,
    [CreatedByType] nvarchar(32) NOT NULL,
    [BeneficiaryId] uniqueidentifier NOT NULL,
    [AmountMinor] bigint NOT NULL,
    [Currency] nchar(3) NOT NULL,
    [Reference] nvarchar(140) NOT NULL,
    [IdempotencyKey] nvarchar(64) NOT NULL,
    [RequestHash] nvarchar(64) NOT NULL,
    [EndToEndId] nvarchar(35) NOT NULL,
    [DebtorAccountRef] nvarchar(512) NULL,
    [Status] nvarchar(32) NOT NULL,
    [BankReference] nvarchar(512) NULL,
    [FailureCode] nvarchar(512) NULL,
    [RequiresApproval] bit NOT NULL,
    [RiskFlags] nvarchar(512) NULL,
    [ApprovedBy] nvarchar(512) NULL,
    [SubmissionAttempts] int NOT NULL,
    [InquiryAttempts] int NOT NULL,
    [Version] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [SentAt] datetime2 NULL,
    [CompletedAt] datetime2 NULL,
    CONSTRAINT [PK_payments] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [reconciliation_runs] (
    [Id] uniqueidentifier NOT NULL,
    [Date] date NOT NULL,
    [StartedAt] datetime2 NOT NULL,
    [FinishedAt] datetime2 NOT NULL,
    [InternalCount] int NOT NULL,
    [BankCount] int NOT NULL,
    CONSTRAINT [PK_reconciliation_runs] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [refresh_tokens] (
    [Id] uniqueidentifier NOT NULL,
    [TokenHash] nvarchar(64) NOT NULL,
    [FamilyId] uniqueidentifier NOT NULL,
    [Subject] nvarchar(512) NOT NULL,
    [TenantId] nvarchar(512) NOT NULL,
    [Scopes] nvarchar(512) NOT NULL,
    [Amr] nvarchar(512) NOT NULL,
    [Acr] nvarchar(512) NOT NULL,
    [AuthTime] datetime2 NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    [FamilyExpiresAt] datetime2 NOT NULL,
    [UsedAt] datetime2 NULL,
    [RevokedAt] datetime2 NULL,
    CONSTRAINT [PK_refresh_tokens] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [webhook_inbox] (
    [Id] uniqueidentifier NOT NULL,
    [Provider] nvarchar(32) NOT NULL,
    [EventId] nvarchar(128) NOT NULL,
    [EventType] nvarchar(512) NOT NULL,
    [EndToEndId] nvarchar(35) NULL,
    [State] nvarchar(512) NULL,
    [BankReference] nvarchar(512) NULL,
    [ReasonCode] nvarchar(512) NULL,
    [ReceivedAt] datetime2 NOT NULL,
    [ProcessedAt] datetime2 NULL,
    [Result] nvarchar(512) NULL,
    CONSTRAINT [PK_webhook_inbox] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [webhook_nonces] (
    [Provider] nvarchar(32) NOT NULL,
    [Nonce] nvarchar(128) NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    CONSTRAINT [PK_webhook_nonces] PRIMARY KEY ([Provider], [Nonce])
);
GO


CREATE TABLE [reconciliation_discrepancies] (
    [Id] uniqueidentifier NOT NULL,
    [RunId] uniqueidentifier NOT NULL,
    [Type] nvarchar(512) NOT NULL,
    [Severity] nvarchar(512) NOT NULL,
    [EndToEndId] nvarchar(35) NULL,
    [PaymentId] uniqueidentifier NULL,
    [Detail] nvarchar(512) NOT NULL,
    CONSTRAINT [PK_reconciliation_discrepancies] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_reconciliation_discrepancies_reconciliation_runs_RunId] FOREIGN KEY ([RunId]) REFERENCES [reconciliation_runs] ([Id]) ON DELETE CASCADE
);
GO


CREATE INDEX [IX_api_keys_TenantId] ON [api_keys] ([TenantId]);
GO


CREATE UNIQUE INDEX [IX_beneficiaries_TenantId_AccountFingerprint] ON [beneficiaries] ([TenantId], [AccountFingerprint]);
GO


CREATE INDEX [IX_jobs_Status_AvailableAt] ON [jobs] ([Status], [AvailableAt]);
GO


CREATE UNIQUE INDEX [IX_ledger_entries_AggregateType_AggregateId_AggregateSequence] ON [ledger_entries] ([AggregateType], [AggregateId], [AggregateSequence]);
GO


CREATE INDEX [IX_ledger_entries_TenantId_OccurredAt] ON [ledger_entries] ([TenantId], [OccurredAt]);
GO


CREATE UNIQUE INDEX [IX_payments_EndToEndId] ON [payments] ([EndToEndId]);
GO


CREATE INDEX [IX_payments_SentAt] ON [payments] ([SentAt]);
GO


CREATE INDEX [IX_payments_TenantId_BeneficiaryId_Status] ON [payments] ([TenantId], [BeneficiaryId], [Status]);
GO


CREATE INDEX [IX_payments_TenantId_CreatedAt] ON [payments] ([TenantId], [CreatedAt]);
GO


CREATE UNIQUE INDEX [IX_payments_TenantId_IdempotencyKey] ON [payments] ([TenantId], [IdempotencyKey]);
GO


CREATE INDEX [IX_reconciliation_discrepancies_RunId] ON [reconciliation_discrepancies] ([RunId]);
GO


CREATE INDEX [IX_refresh_tokens_FamilyId] ON [refresh_tokens] ([FamilyId]);
GO


CREATE UNIQUE INDEX [IX_refresh_tokens_TokenHash] ON [refresh_tokens] ([TokenHash]);
GO


CREATE UNIQUE INDEX [IX_webhook_inbox_Provider_EventId] ON [webhook_inbox] ([Provider], [EventId]);
GO


CREATE INDEX [IX_webhook_nonces_ExpiresAt] ON [webhook_nonces] ([ExpiresAt]);
GO



