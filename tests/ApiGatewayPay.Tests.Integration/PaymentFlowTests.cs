using System.Net;
using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static ApiGatewayPay.Tests.Integration.Flows;

namespace ApiGatewayPay.Tests.Integration;

public class PaymentFlowTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Payment_goes_end_to_end_to_confirmed_with_a_valid_audit_chain()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 2_500_000);
        Assert.Equal("approved", payment.GetProperty("status").GetString());

        var confirmed = await WaitForStatusAsync(maker, payment.GetProperty("id").GetString()!, "confirmed");
        Assert.StartsWith("MB", confirmed.GetProperty("bankReference").GetString());

        var id = payment.GetProperty("id").GetGuid().ToString("N");
        var events = f.WithDb(db => db.Ledger.Where(e => e.AggregateId == id).OrderBy(e => e.AggregateSequence).Select(e => e.EventType).ToList());
        Assert.Equal(["payment.created", "payment.auto_approved", "payment.sent", "payment.bank_accepted", "payment.confirmed"], events.Where(e => e != "payment.status_inquiry"));

        using var scope = f.Services.CreateScope();
        var verification = await scope.ServiceProvider.GetRequiredService<LedgerVerifier>().VerifyAsync("tenant-a", "payment", id, default);
        Assert.True(verification.Valid, verification.FirstProblem);
    }

    [Fact]
    public async Task Retried_request_with_same_idempotency_key_returns_the_same_payment()
    {
        var maker = await f.As("maker@tenant-a");
        var beneficiary = await ActiveBeneficiaryAsync(f);
        var key = HttpExtensions.NewKey();
        var body = new { beneficiaryId = beneficiary, amountMinor = 1_000_000L, currency = "COP", reference = "Idem" };

        var first = await maker.PostJsonAsync("/v1/payments", body, key);
        var second = await maker.PostJsonAsync("/v1/payments", body, key);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((await first.JsonAsync()).GetProperty("id").GetString(), (await second.JsonAsync()).GetProperty("id").GetString());
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());

        var changed = await maker.PostJsonAsync("/v1/payments", body with { amountMinor = 1_000_001L }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal("idempotency_key_reused", await changed.CodeAsync());
    }

    [Fact]
    public async Task Concurrent_requests_with_same_key_create_exactly_one_payment_and_one_bank_order()
    {
        var maker = await f.As("maker@tenant-a");
        var beneficiary = await ActiveBeneficiaryAsync(f);
        var key = HttpExtensions.NewKey();
        var body = new { beneficiaryId = beneficiary, amountMinor = 777_000L, currency = "COP", reference = "Carrera" };

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => maker.PostJsonAsync("/v1/payments", body, key)));
        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.All(statuses, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.Conflict, $"status {s}"));
        var ids = new HashSet<string>();
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Created)) ids.Add((await r.JsonAsync()).GetProperty("id").GetString()!);
        Assert.Single(ids);

        await WaitForStatusAsync(maker, ids.Single(), "confirmed");
        Assert.Equal(1, f.WithDb(db => db.Payments.Count(p => p.IdempotencyKey == key)));
        Assert.Equal(1, f.Bank.InitiationsFor(Guid.Parse(ids.Single()).ToString("N")));
    }

    [Fact]
    public async Task High_amount_requires_a_second_human_with_fresh_mfa()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 1_500_000_000);
        var id = payment.GetProperty("id").GetString()!;
        Assert.Equal("pending_approval", payment.GetProperty("status").GetString());
        Assert.Contains("high_amount", payment.GetProperty("riskFlags").EnumerateArray().Select(x => x.GetString()));

        // El maker no tiene el scope de aprobación.
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.PostAsync($"/v1/payments/{id}/approve", null)).StatusCode);

        // El checker sin MFA recibe el desafío de step-up (RFC 9470).
        var weak = await (await f.As("checker@tenant-a", mfa: false)).PostAsync($"/v1/payments/{id}/approve", null);
        Assert.Equal(HttpStatusCode.Unauthorized, weak.StatusCode);
        Assert.Contains("insufficient_user_authentication", weak.Headers.WwwAuthenticate.ToString());
        Assert.Equal("step_up_required", await weak.CodeAsync());

        var approved = await (await f.As("checker@tenant-a")).PostAsync($"/v1/payments/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await WaitForStatusAsync(maker, id, "confirmed");
    }

    [Fact]
    public async Task Checker_can_reject_and_payment_never_reaches_the_bank()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 1_200_000_000);
        var id = payment.GetProperty("id").GetString()!;
        var rejected = await (await f.As("checker@tenant-a")).PostJsonAsync($"/v1/payments/{id}/reject", new { reasonCode = "suspicious" });
        Assert.Equal("rejected", (await rejected.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(0, f.Bank.InitiationsFor(Guid.Parse(id).ToString("N")));
    }

    [Fact]
    public async Task Ambiguous_timeout_is_resolved_by_status_inquiry_without_resending()
    {
        var maker = await f.As("maker@tenant-a");
        // ..99: el banco procesa la orden pero la respuesta se pierde (timeout).
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 3_000_099);
        var id = payment.GetProperty("id").GetString()!;
        await WaitForStatusAsync(maker, id, "confirmed");

        var e2e = Guid.Parse(id).ToString("N");
        Assert.Equal(1, f.Bank.InitiationsFor(e2e));
        var events = f.WithDb(db => db.Ledger.Where(e => e.AggregateId == e2e).Select(e => e.EventType).ToList());
        Assert.Contains("payment.outcome_unknown", events);
        Assert.DoesNotContain("payment.requeued_not_found", events);
    }

    [Fact]
    public async Task Lost_order_is_resent_only_after_bank_confirms_not_found_and_with_same_e2e()
    {
        var maker = await f.As("maker@tenant-a");
        // ..98: la orden nunca llega al banco y hay timeout.
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 3_000_098);
        var id = payment.GetProperty("id").GetString()!;
        await WaitForStatusAsync(maker, id, "confirmed");

        var e2e = Guid.Parse(id).ToString("N");
        Assert.Equal(2, f.Bank.InitiationsFor(e2e));
        Assert.Single(f.Bank.Payments.Values, p => p.EndToEndId == e2e);
        var events = f.WithDb(db => db.Ledger.Where(e => e.AggregateId == e2e).OrderBy(e => e.AggregateSequence).Select(e => e.EventType).ToList());
        Assert.True(events.IndexOf("payment.outcome_unknown") < events.IndexOf("payment.requeued_not_found"));
    }

    [Fact]
    public async Task Bank_rejection_marks_payment_failed()
    {
        var maker = await f.As("maker@tenant-a");
        var payment = await CreatePaymentAsync(maker, await ActiveBeneficiaryAsync(f), 3_000_013);
        var failed = await WaitForStatusAsync(maker, payment.GetProperty("id").GetString()!, "failed");
        Assert.Equal("bank_AC04", failed.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task Backend_and_fraud_rules_reject_without_contacting_the_bank()
    {
        var maker = await f.As("maker@tenant-a");
        var beneficiary = await ActiveBeneficiaryAsync(f);

        var declined = await CreatePaymentAsync(maker, beneficiary, 100_000, reference: "DECLINE por negocio");
        Assert.Equal(("rejected", "backend_business_rule"), (declined.GetProperty("status").GetString(), declined.GetProperty("failureCode").GetString()));

        var currency = await CreatePaymentAsync(maker, beneficiary, 100_000, currency: "EUR");
        Assert.Equal("risk_currency_not_allowed", currency.GetProperty("failureCode").GetString());

        var tooBig = await CreatePaymentAsync(maker, beneficiary, 9_000_000_000);
        Assert.Equal("risk_per_transaction_limit", tooBig.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task Only_whitelisted_and_approved_beneficiaries_can_be_paid()
    {
        var maker = await f.As("maker@tenant-a");
        var pending = await maker.PostJsonAsync("/v1/beneficiaries", new { displayName = "Pendiente", bankCode = "1007", accountType = "checking", accountNumber = "5554443332" });
        var id = (await pending.JsonAsync()).GetProperty("id").GetGuid();
        var r = await maker.PostJsonAsync("/v1/payments", new { beneficiaryId = id, amountMinor = 1000L, currency = "COP", reference = "x" }, HttpExtensions.NewKey());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("beneficiary_not_active", await r.CodeAsync());

        var self = await maker.PostAsync($"/v1/beneficiaries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
    }

    [Fact]
    public async Task Tenants_cannot_see_or_use_each_others_resources()
    {
        var makerA = await f.As("maker@tenant-a");
        var beneficiaryA = await ActiveBeneficiaryAsync(f);
        var payment = await CreatePaymentAsync(makerA, beneficiaryA, 50_000);
        var makerB = await f.As("maker@tenant-b");

        var read = await makerB.GetAsync($"/v1/payments/{payment.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode); // 404, no 403: no revela existencia

        var useOtherBeneficiary = await makerB.PostJsonAsync("/v1/payments", new { beneficiaryId = beneficiaryA, amountMinor = 1000L, currency = "COP", reference = "x" }, HttpExtensions.NewKey());
        Assert.Equal("beneficiary_not_found", await useOtherBeneficiary.CodeAsync());

        var list = await (await makerB.GetAsync("/v1/payments")).JsonAsync();
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetString() == payment.GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await makerB.GetAsync("/v1/accounts/ops-cop/balance")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_account_cannot_be_registered_twice()
    {
        var maker = await f.As("maker@tenant-a");
        var body = new { displayName = "Duplicado", bankCode = "1007", accountType = "savings", accountNumber = "1231231231" };
        await maker.PostJsonAsync("/v1/beneficiaries", body);
        var again = await maker.PostJsonAsync("/v1/beneficiaries", body);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Accounts_expose_only_aliases_and_masked_numbers()
    {
        var maker = await f.As("maker@tenant-a");
        var body = await (await maker.GetAsync("/v1/accounts")).Content.ReadAsStringAsync();
        Assert.Contains("ops-cop", body);
        Assert.DoesNotContain("0012345678901", body);
        Assert.Contains("8901", body);
        var balance = await (await maker.GetAsync("/v1/accounts/ops-cop/balance")).JsonAsync();
        Assert.Equal("COP", balance.GetProperty("currency").GetString());
        var tooLong = await maker.GetAsync("/v1/accounts/ops-cop/movements?from=2026-01-01&to=2026-06-01");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
}
