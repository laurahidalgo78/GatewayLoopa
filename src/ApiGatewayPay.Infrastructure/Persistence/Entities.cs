namespace ApiGatewayPay.Infrastructure.Persistence;

/// <summary>Fila del libro inmutable. Solo INSERT: el DbContext y la base de datos rechazan UPDATE/DELETE.</summary>
public sealed class LedgerEntryEntity
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public string AggregateType { get; set; } = default!;
    public string AggregateId { get; set; } = default!;
    public int AggregateSequence { get; set; }
    public string EventType { get; set; } = default!;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public long? AmountMinor { get; set; }
    public string? Currency { get; set; }
    public string ActorType { get; set; } = default!;
    public string ActorId { get; set; } = default!;
    public bool StrongAuthentication { get; set; }
    public string? SourceIp { get; set; }
    public string? UserAgent { get; set; }
    public string CorrelationId { get; set; } = default!;
    public DateTime OccurredAt { get; set; }
    public string? Detail { get; set; }
    public string PreviousHash { get; set; } = default!;
    public string Hash { get; set; } = default!;
}

public enum JobStatus { Pending, Processing, Done, Dead }

public sealed class JobEntity
{
    public Guid Id { get; set; }
    public string Type { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public string CorrelationId { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class KillSwitchEntity
{
    public string Id { get; set; } = default!;
    public bool Active { get; set; }
    public string? Reason { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime? ChangedAt { get; set; }
    public int Version { get; set; }
}

public sealed class WebhookInboxEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = default!;
    public string EventId { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public string? EndToEndId { get; set; }
    public string? State { get; set; }
    public string? BankReference { get; set; }
    public string? ReasonCode { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Result { get; set; }
}

public sealed class WebhookNonceEntity
{
    public string Provider { get; set; } = default!;
    public string Nonce { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
}

public sealed class ApiKeyEntity
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string SecretHash { get; set; } = default!;
    public string Scopes { get; set; } = default!;
    public string Environment { get; set; } = default!;
    public string CreatedBy { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = default!;
    public Guid FamilyId { get; set; }
    public string Subject { get; set; } = default!;
    public string TenantId { get; set; } = default!;
    public string Scopes { get; set; } = default!;
    public string Amr { get; set; } = default!;
    public string Acr { get; set; } = default!;
    public DateTime AuthTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime FamilyExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class ReconciliationRunEntity
{
    public Guid Id { get; set; }
    public DateOnly Date { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int InternalCount { get; set; }
    public int BankCount { get; set; }
    public List<ReconciliationDiscrepancyEntity> Discrepancies { get; set; } = [];
}

public sealed class ReconciliationDiscrepancyEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Type { get; set; } = default!;
    public string Severity { get; set; } = default!;
    public string? EndToEndId { get; set; }
    public Guid? PaymentId { get; set; }
    public string Detail { get; set; } = default!;
}
