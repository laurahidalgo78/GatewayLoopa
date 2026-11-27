using System.Globalization;
using System.Net;
using System.Text;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Infrastructure.Crypto;
using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ApiGatewayPay.Tests.Integration.Flows;

namespace ApiGatewayPay.Tests.Integration;

public class WebhookTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private HttpRequestMessage Webhook(string body, string? nonce = null, long? ts = null, string? signature = null, string provider = "mock")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var timestamp = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var n = nonce ?? HmacSignature.NewNonce();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/webhooks/bank/{provider}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Bank-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Bank-Nonce", n);
        request.Headers.Add("X-Bank-Signature", "v1=" + (signature ?? HmacSignature.Compute(Encoding.UTF8.GetBytes(f.Secret("bank/webhook-secret")), timestamp, n, bytes)));
        return request;
    }

    private static string Event(string eventId, string e2e = "none", string status = "SETTLED") =>
        $"{{\"eventId\":\"{eventId}\",\"type\":\"payment.status_changed\",\"endToEndId\":\"{e2e}\",\"status\":\"{status}\",\"bankReference\":\"B1\"}}";

    [Fact]
    public async Task Valid_webhook_is_accepted_and_processed_asynchronously()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 40_000);
        var e2e = payment.GetProperty("id").GetGuid().ToString("N");
        var eventId = "evt_" + Guid.NewGuid().ToString("N");

        var r = await f.Anonymous().SendAsync(Webhook(Event(eventId, e2e)));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (f.WithDb(db => db.WebhookInbox.Single(w => w.EventId == eventId).ProcessedAt) is null && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.NotNull(f.WithDb(db => db.WebhookInbox.Single(w => w.EventId == eventId).Result));
        await WaitForStatusAsync(maker, payment.GetProperty("id").GetString()!, "confirmed");
    }

    [Fact]
    public async Task Redelivered_event_is_deduplicated()
    {
        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Accepted, (await f.Anonymous().SendAsync(Webhook(Event(eventId)))).StatusCode);
        var again = await f.Anonymous().SendAsync(Webhook(Event(eventId)));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, f.WithDb(db => db.WebhookInbox.Count(w => w.EventId == eventId)));
    }

    [Fact]
    public async Task Replayed_message_with_same_nonce_is_rejected()
    {
        var nonce = HmacSignature.NewNonce();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Event("evt_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Accepted, (await f.Anonymous().SendAsync(Webhook(body, nonce, ts))).StatusCode);
        var replay = await f.Anonymous().SendAsync(Webhook(body, nonce, ts));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("webhook_rejected", await replay.CodeAsync());
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_without_details()
    {
        var r = await f.Anonymous().SendAsync(Webhook(Event("evt_x1"), signature: new string('0', 64)));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.DoesNotContain("signature", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_timestamp_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous().SendAsync(Webhook(Event("evt_x2"), ts: DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds()))).StatusCode);

    [Fact]
    public async Task Unknown_provider_is_not_found() =>
        Assert.Equal(HttpStatusCode.NotFound, (await f.Anonymous().SendAsync(Webhook(Event("evt_x3"), provider: "otro-banco"))).StatusCode);
}

public class OperationsTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Kill_switch_stops_new_money_movements_immediately()
    {
        var maker = await f.As("maker@tenant-a");
        var ops = await f.As("ops@platform");
        var beneficiary = await ActiveBeneficiaryAsync(f);
        var pending = await CreatePaymentAsync(maker, beneficiary, 1_300_000_000);

        Assert.Equal(HttpStatusCode.OK, (await ops.PostJsonAsync("/v1/admin/kill-switch", new { active = true, reason = "Prueba de incidente" })).StatusCode);
        try
        {
            var blocked = await maker.PostJsonAsync("/v1/payments", new { beneficiaryId = beneficiary, amountMinor = 1000L, currency = "COP", reference = "x" }, HttpExtensions.NewKey());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.Equal("bank_connection_disabled", await blocked.CodeAsync());
            var approve = await (await f.As("checker@tenant-a")).PostAsync($"/v1/payments/{pending.GetProperty("id").GetString()}/approve", null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, approve.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await maker.GetAsync("/v1/accounts/ops-cop/balance")).StatusCode);
            Assert.Equal("Degraded", (await (await f.Anonymous().GetAsync("/ready")).JsonAsync()).GetProperty("status").GetString());
        }
        finally
        {
            await ops.PostJsonAsync("/v1/admin/kill-switch", new { active = false, reason = "Fin de la prueba" });
        }
        Assert.Contains("killswitch.activated", f.WithDb(db => db.Ledger.Where(e => e.AggregateType == "killswitch").Select(e => e.EventType).ToList()));
    }

    [Fact]
    public async Task Kill_switch_requires_platform_scope_and_mfa()
    {
        var weakOps = await f.As("ops@platform", mfa: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await weakOps.PostJsonAsync("/v1/admin/kill-switch", new { active = true, reason = "intento sin mfa" })).StatusCode);
        var tenantAdmin = await f.As("admin@tenant-a");
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PostJsonAsync("/v1/admin/kill-switch", new { active = true, reason = "no es plataforma" })).StatusCode);
    }

    [Fact]
    public async Task Reconciliation_matches_and_flags_movements_we_did_not_originate()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 60_000);
        await WaitForStatusAsync(maker, payment.GetProperty("id").GetString()!, "confirmed");
        var ops = await f.As("ops@platform");
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var clean = await (await ops.PostJsonAsync("/v1/admin/reconciliation/runs", new { date = today })).JsonAsync();
        Assert.DoesNotContain(clean.GetProperty("discrepancies").EnumerateArray(), d => d.GetProperty("severity").GetInt32() >= 2);

        f.Bank.InjectedStatementEntries.Add(new BankStatementEntry("e2e-desconocido", "BANKREF999", "0012345678901", 999_000, "COP", BankPaymentState.Settled));
        var dirty = await (await ops.PostJsonAsync("/v1/admin/reconciliation/runs", new { date = today })).JsonAsync();
        Assert.Contains(dirty.GetProperty("discrepancies").EnumerateArray(), d => d.GetProperty("type").GetString() == "missing_internally");
        f.Bank.InjectedStatementEntries.Clear();
    }

    [Fact]
    public async Task Reconciliation_is_restricted_to_platform_operators() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await (await f.As("admin@tenant-a")).PostJsonAsync("/v1/admin/reconciliation/runs", new { date = "2026-01-01" })).StatusCode);
}

/// <summary>Fábrica propia: estas pruebas manipulan la BD directamente.</summary>
public class LedgerIntegrityTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Ledger_is_append_only_and_tampering_is_detected()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 80_000);
        var id = payment.GetProperty("id").GetGuid().ToString("N");
        await WaitForStatusAsync(maker, payment.GetProperty("id").GetString()!, "confirmed");

        var admin = await f.As("admin@tenant-a", mfa: false);
        Assert.True((await (await admin.GetAsync("/v1/admin/ledger/verify")).JsonAsync()).GetProperty("valid").GetBoolean());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 1) La BD rechaza UPDATE y DELETE sobre el libro.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE ledger_entries SET AmountMinor = 1"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM ledger_entries"));

        // 2) El DbContext rechaza modificar entradas.
        var entry = await db.Ledger.FirstAsync(e => e.AggregateId == id);
        entry.AmountMinor = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // 3) Aun saltándose ambas barreras (atacante con DBA), la cadena HMAC detecta la alteración.
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER ledger_no_update");
        await db.Database.ExecuteSqlRawAsync("UPDATE ledger_entries SET AmountMinor = 1 WHERE AggregateId = {0} AND AggregateSequence = 1", id);
        var result = await (await admin.GetAsync("/v1/admin/ledger/verify?aggregateType=payment&aggregateId=" + id)).JsonAsync();
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Contains("alterado", result.GetProperty("firstProblem").GetString());
    }
}
