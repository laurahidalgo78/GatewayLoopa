using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Beneficiaries;
using ApiGatewayPay.Core.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApiGatewayPay.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<LedgerEntryEntity> Ledger => Set<LedgerEntryEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<KillSwitchEntity> KillSwitches => Set<KillSwitchEntity>();
    public DbSet<WebhookInboxEntity> WebhookInbox => Set<WebhookInboxEntity>();
    public DbSet<WebhookNonceEntity> WebhookNonces => Set<WebhookNonceEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<ReconciliationRunEntity> ReconciliationRuns => Set<ReconciliationRunEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Todas las fechas en UTC; al leer se marca Kind=Utc (SQLite las devuelve sin zona).
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
        b.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<BeneficiaryStatus>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<AccountType>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<ActorType>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<JobStatus>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<string>().HaveMaxLength(512);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.Amount);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64);
            e.Property(x => x.EndToEndId).HasMaxLength(35);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Reference).HasMaxLength(140);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            // Defensa en profundidad de la idempotencia: aunque falle todo lo demás, la BD impide el duplicado.
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.EndToEndId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.BeneficiaryId, x.Status });
            e.HasIndex(x => x.SentAt);
        });

        m.Entity<Beneficiary>(e =>
        {
            e.ToTable("beneficiaries");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.AccountNumberCipher).HasMaxLength(256);
            e.Property(x => x.AccountLast4).HasMaxLength(4);
            e.Property(x => x.AccountFingerprint).HasMaxLength(64);
            e.Property(x => x.BankCode).HasMaxLength(16);
            e.Property(x => x.DisplayName).HasMaxLength(140);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.AccountFingerprint }).IsUnique();
        });

        m.Entity<LedgerEntryEntity>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.AggregateType).HasMaxLength(32);
            e.Property(x => x.AggregateId).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.PreviousHash).HasMaxLength(64);
            // Una sola entrada por (agregado, versión): impide bifurcar la cadena.
            e.HasIndex(x => new { x.AggregateType, x.AggregateId, x.AggregateSequence }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
        });

        m.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Payload).HasMaxLength(4000);
            e.Property(x => x.LastError).HasMaxLength(512);
            e.HasIndex(x => new { x.Status, x.AvailableAt });
        });

        m.Entity<KillSwitchEntity>(e =>
        {
            e.ToTable("kill_switches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        m.Entity<WebhookInboxEntity>(e =>
        {
            e.ToTable("webhook_inbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.EventId).HasMaxLength(128);
            e.Property(x => x.EndToEndId).HasMaxLength(35);
            e.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        });

        m.Entity<WebhookNonceEntity>(e =>
        {
            e.ToTable("webhook_nonces");
            e.HasKey(x => new { x.Provider, x.Nonce });
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Nonce).HasMaxLength(128);
            e.HasIndex(x => x.ExpiresAt);
        });

        m.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.HasIndex(x => x.TenantId);
        });

        m.Entity<RefreshTokenEntity>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
        });

        m.Entity<ReconciliationRunEntity>(e =>
        {
            e.ToTable("reconciliation_runs");
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Discrepancies).WithOne().HasForeignKey(x => x.RunId);
        });
        m.Entity<ReconciliationDiscrepancyEntity>(e =>
        {
            e.ToTable("reconciliation_discrepancies");
            e.HasKey(x => x.Id);
            e.Property(x => x.EndToEndId).HasMaxLength(35);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAppendOnlyLedger();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyLedger();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceAppendOnlyLedger()
    {
        if (ChangeTracker.Entries<LedgerEntryEntity>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("El libro de transacciones es append-only.");
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
