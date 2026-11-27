using System.Globalization;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Domain.Beneficiaries;
using ApiGatewayPay.Core.Domain.Payments;
using ApiGatewayPay.Core.Ports.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ApiGatewayPay.Infrastructure.Persistence;

public sealed class EfPaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetAsync(string tenantId, Guid id, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);

    public Task<Payment?> FindByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == idempotencyKey, ct);

    public async Task<PagedResult<Payment>> ListAsync(string tenantId, PaymentStatus? status, int limit, string? cursor, CancellationToken ct)
    {
        var query = db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId);
        if (status is not null) query = query.Where(p => p.Status == status);
        if (cursor is not null && long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        {
            var before = new DateTime(ticks, DateTimeKind.Utc);
            query = query.Where(p => p.CreatedAt < before);
        }
        var items = await query.OrderByDescending(p => p.CreatedAt).Take(limit + 1).ToListAsync(ct);
        var next = items.Count > limit ? items[limit - 1].CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture) : null;
        return new PagedResult<Payment>(items.Take(limit).ToList(), next);
    }

    public async Task<TenantPaymentStats> GetStatsAsync(string tenantId, string currency, Guid beneficiaryId, DateTime dailySince, DateTime velocitySince, CancellationToken ct)
    {
        var tenant = db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId);
        var count = await tenant.CountAsync(p => p.CreatedAt >= velocitySince, ct);
        var counted = tenant.Where(p => p.Currency == currency && p.Status != PaymentStatus.Rejected && p.Status != PaymentStatus.Failed);
        var sum = await counted.Where(p => p.CreatedAt >= dailySince).SumAsync(p => (long?)p.AmountMinor, ct) ?? 0;
        var recent = await counted.OrderByDescending(p => p.CreatedAt).Take(30).Select(p => p.AmountMinor).ToListAsync(ct);
        var paidBefore = await tenant.AnyAsync(p => p.BeneficiaryId == beneficiaryId && p.Status == PaymentStatus.Confirmed, ct);
        return new TenantPaymentStats(count, sum, recent, paidBefore);
    }

    public Task<Payment?> GetForProcessingAsync(Guid id, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Payment?> FindByEndToEndIdAsync(string endToEndId, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.EndToEndId == endToEndId, ct);

    public async Task<IReadOnlyList<Payment>> ListSentBetweenAsync(DateTime from, DateTime to, CancellationToken ct) =>
        await db.Payments.Where(p => p.SentAt >= from && p.SentAt < to).ToListAsync(ct);

    public async Task<IReadOnlyList<Payment>> FindByEndToEndIdsAsync(IReadOnlyCollection<string> endToEndIds, CancellationToken ct) =>
        endToEndIds.Count == 0 ? [] : await db.Payments.Where(p => endToEndIds.Contains(p.EndToEndId)).ToListAsync(ct);

    public void Add(Payment payment) => db.Payments.Add(payment);
}

public sealed class EfBeneficiaryRepository(AppDbContext db) : IBeneficiaryRepository
{
    public Task<Beneficiary?> GetAsync(string tenantId, Guid id, CancellationToken ct) =>
        db.Beneficiaries.FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct);

    public Task<bool> ExistsWithFingerprintAsync(string tenantId, string fingerprint, CancellationToken ct) =>
        db.Beneficiaries.AnyAsync(b => b.TenantId == tenantId && b.AccountFingerprint == fingerprint, ct);

    public async Task<IReadOnlyList<Beneficiary>> ListAsync(string tenantId, CancellationToken ct) =>
        await db.Beneficiaries.AsNoTracking().Where(b => b.TenantId == tenantId).OrderBy(b => b.DisplayName).Take(500).ToListAsync(ct);

    public void Add(Beneficiary beneficiary) => db.Beneficiaries.Add(beneficiary);
}

public sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new ConcurrencyConflictException("Conflicto de concurrencia.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new DuplicateKeyException("Clave duplicada.", ex);
        }
    }

    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        SqliteException s => s.SqliteErrorCode == 19 && s.SqliteExtendedErrorCode is 2067 or 1555,
        SqlException s => s.Number is 2601 or 2627,
        _ => false,
    };
}

public sealed class EfWebhookInbox(AppDbContext db, TimeProvider time) : IWebhookInbox
{
    public async Task<bool> TryRegisterNonceAsync(string provider, string nonce, DateTime expiresAt, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (await db.WebhookNonces.AnyAsync(n => n.Provider == provider && n.Nonce == nonce && n.ExpiresAt > now, ct))
            return false;
        db.WebhookNonces.Add(new WebhookNonceEntity { Provider = provider, Nonce = nonce, ExpiresAt = expiresAt });
        return true;
    }

    public Task<bool> ExistsAsync(string provider, string eventId, CancellationToken ct) =>
        db.WebhookInbox.AnyAsync(w => w.Provider == provider && w.EventId == eventId, ct);

    public void Add(WebhookInboxItem item) => db.WebhookInbox.Add(new WebhookInboxEntity
    {
        Id = item.Id, Provider = item.Provider, EventId = item.EventId, EventType = item.EventType, EndToEndId = item.EndToEndId,
        State = item.State, BankReference = item.BankReference, ReasonCode = item.ReasonCode, ReceivedAt = item.ReceivedAt,
    });

    public async Task<WebhookInboxItem?> GetAsync(Guid id, CancellationToken ct)
    {
        var e = await db.WebhookInbox.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return e is null ? null : new WebhookInboxItem(e.Id, e.Provider, e.EventId, e.EventType, e.EndToEndId, e.State, e.BankReference, e.ReasonCode, e.ReceivedAt);
    }

    public async Task MarkProcessedAsync(Guid id, string result, CancellationToken ct)
    {
        var e = await db.WebhookInbox.FirstAsync(w => w.Id == id, ct);
        e.ProcessedAt = time.GetUtcNow().UtcDateTime;
        e.Result = result;
    }
}

public sealed class EfReconciliationStore(AppDbContext db) : IReconciliationStore
{
    public void Add(ReconciliationReport report) => db.ReconciliationRuns.Add(new ReconciliationRunEntity
    {
        Id = report.Id, Date = report.Date, StartedAt = report.StartedAt, FinishedAt = report.FinishedAt,
        InternalCount = report.InternalCount, BankCount = report.BankCount,
        Discrepancies = report.Discrepancies.Select(d => new ReconciliationDiscrepancyEntity
        {
            Id = Guid.CreateVersion7(), RunId = report.Id, Type = d.Type, Severity = d.Severity.ToString(),
            EndToEndId = d.EndToEndId, PaymentId = d.PaymentId, Detail = d.Detail,
        }).ToList(),
    });

    public async Task<ReconciliationReport?> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.ReconciliationRuns.AsNoTracking().Include(x => x.Discrepancies).FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : new ReconciliationReport(r.Id, r.Date, r.StartedAt, r.FinishedAt, r.InternalCount, r.BankCount,
            r.Discrepancies.Select(d => new ReconciliationDiscrepancy(d.Type, Enum.Parse<DiscrepancySeverity>(d.Severity), d.EndToEndId, d.PaymentId, d.Detail)).ToList());
    }
}
