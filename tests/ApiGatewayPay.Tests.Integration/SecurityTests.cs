using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static ApiGatewayPay.Tests.Integration.Flows;

namespace ApiGatewayPay.Tests.Integration;

public class SecurityTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/v1/payments")]
    [InlineData("/v1/beneficiaries")]
    [InlineData("/v1/accounts")]
    [InlineData("/v1/admin/api-keys")]
    public async Task Every_business_endpoint_requires_authentication(string path)
    {
        var r = await f.Anonymous().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Forged_or_tampered_tokens_are_rejected()
    {
        var client = f.Anonymous();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4IiwidGlkIjoidGVuYW50LWEifQ.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/payments")).StatusCode);

        var valid = await f.As("maker@tenant-a");
        var token = valid.DefaultRequestHeaders.Authorization!.Parameter!;
        var parts = token.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(Convert.FromBase64String(Pad(parts[1])))!;
        payload["tid"] = "tenant-b";
        var forged = $"{parts[0]}.{Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[2]}";
        client.DefaultRequestHeaders.Authorization = new("Bearer", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/payments")).StatusCode);

        static string Pad(string s) => s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4);
    }

    [Fact]
    public async Task Missing_scope_is_forbidden()
    {
        var admin = await f.As("admin@tenant-a", mfa: false);
        var r = await admin.PostJsonAsync("/v1/payments", new { beneficiaryId = Guid.NewGuid(), amountMinor = 1L, currency = "COP", reference = "x" }, HttpExtensions.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("insufficient_scope", await r.CodeAsync());
    }

    [Theory]
    [InlineData("{\"beneficiaryId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":100,\"currency\":\"COP\",\"reference\":\"x\",\"status\":\"confirmed\"}", "invalid_request")] // asignación masiva
    [InlineData("{\"beneficiaryId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":\"100\",\"currency\":\"COP\",\"reference\":\"x\"}", "invalid_request")]   // número como texto
    [InlineData("{\"beneficiaryId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":100,\"amountMinor\":1,\"currency\":\"COP\",\"reference\":\"x\"}", "invalid_request")] // claves duplicadas
    [InlineData("{\"beneficiaryId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":-5,\"currency\":\"COP\",\"reference\":\"x\"}", "validation_failed")]
    [InlineData("{\"beneficiaryId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":100,\"currency\":\"cop\",\"reference\":\"x\"}", "validation_failed")]
    [InlineData("{\"beneficiaryId\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":100,\"currency\":\"COP\",\"reference\":\"<script>alert(1)</script>\"}", "validation_failed")]
    [InlineData("{\"amountMinor\":100,\"currency\":\"COP\",\"reference\":\"x\"}", "validation_failed")]
    [InlineData("not json", "invalid_request")]
    public async Task Payloads_are_validated_strictly(string json, string expectedCode)
    {
        var maker = await f.As("maker@tenant-a");
        var r = await maker.PostRawAsync("/v1/payments", json, HttpExtensions.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(expectedCode, await r.CodeAsync());
        Assert.DoesNotContain("script", await r.Content.ReadAsStringAsync()); // no se refleja la entrada
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("contains spaces and !!!!")]
    public async Task Money_operations_require_a_valid_idempotency_key(string? key)
    {
        var maker = await f.As("maker@tenant-a");
        var r = await maker.PostJsonAsync("/v1/payments", new { beneficiaryId = Guid.NewGuid(), amountMinor = 100L, currency = "COP", reference = "x" }, key);
        Assert.Equal("invalid_idempotency_key", await r.CodeAsync());
    }

    [Fact]
    public async Task Oversized_payload_is_rejected()
    {
        var maker = await f.As("maker@tenant-a");
        var r = await maker.PostRawAsync("/v1/payments", "{\"reference\":\"" + new string('a', 70_000) + "\"}", HttpExtensions.NewKey());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_uniform_errors()
    {
        // Anónimo: 401 incluso para rutas inexistentes (la política por defecto no revela qué rutas existen).
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous().GetAsync("/v1/no-existe")).StatusCode);
        var r = await (await f.As("maker@tenant-a")).GetAsync("/v1/no-existe");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var error = await r.JsonAsync();
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        Assert.True(error.TryGetProperty("correlationId", out _));
        Assert.False(error.TryGetProperty("stackTrace", out _));
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        Assert.False(r.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Correlation_id_is_echoed_only_when_safe()
    {
        var client = f.Anonymous();
        var ok = new HttpRequestMessage(HttpMethod.Get, "/health");
        ok.Headers.Add("X-Correlation-Id", "abc-123-def-456");
        Assert.Equal("abc-123-def-456", (await client.SendAsync(ok)).Headers.GetValues("X-Correlation-Id").Single());

        var evil = new HttpRequestMessage(HttpMethod.Get, "/health");
        evil.Headers.TryAddWithoutValidation("X-Correlation-Id", "x\" injected=\"1");
        Assert.NotEqual("x\" injected=\"1", (await client.SendAsync(evil)).Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Registering_a_beneficiary_requires_step_up()
    {
        var weak = await f.As("maker@tenant-a", mfa: false);
        var r = await weak.PostJsonAsync("/v1/beneficiaries", new { displayName = "X Y", bankCode = "1007", accountType = "savings", accountNumber = "1111111111" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("acr_values=\"urn:agp:loa:mfa\"", r.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Api_keys_are_hashed_scoped_revocable_and_environment_bound()
    {
        var admin = await f.As("admin@tenant-a");
        var tooPowerful = await admin.PostJsonAsync("/v1/admin/api-keys", new { name = "erp", scopes = new[] { "payments:approve" }, expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, tooPowerful.StatusCode);

        var created = await (await admin.PostJsonAsync("/v1/admin/api-keys", new { name = "erp", scopes = new[] { "payments:read" }, expiresInDays = 30 })).JsonAsync();
        var key = created.GetProperty("key").GetString()!;
        var keyId = created.GetProperty("keyId").GetString()!;
        Assert.StartsWith("agp_test_", key);
        Assert.DoesNotContain(f.WithDb(db => db.ApiKeys.Select(k => k.SecretHash).ToList()), h => key.Contains(h, StringComparison.Ordinal));

        var machine = f.Anonymous();
        machine.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/v1/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await machine.GetAsync("/v1/accounts")).StatusCode); // scope no concedido

        var liveKey = f.Anonymous();
        liveKey.DefaultRequestHeaders.Add("X-Api-Key", key.Replace("agp_test_", "agp_live_"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await liveKey.GetAsync("/v1/payments")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/admin/api-keys/{keyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await machine.GetAsync("/v1/payments")).StatusCode);
    }

    [Fact]
    public async Task Api_keys_cannot_perform_step_up_operations()
    {
        var admin = await f.As("admin@tenant-a");
        var created = await (await admin.PostJsonAsync("/v1/admin/api-keys", new { name = "bot", scopes = new[] { "payments:write", "payments:read" }, expiresInDays = 1 })).JsonAsync();
        var machine = f.Anonymous();
        machine.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("key").GetString());
        var r = await machine.PostJsonAsync("/v1/beneficiaries", new { displayName = "Bot", bankCode = "1007", accountType = "savings", accountNumber = "2222222222" });
        Assert.True(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_family()
    {
        var login = await (await f.LoginAsync("checker@tenant-a", mfa: false)).JsonAsync();
        var r1 = login.GetProperty("refresh_token").GetString()!;

        var refreshed = await Refresh(r1);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var r2 = (await refreshed.JsonAsync()).GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(r1, r2);

        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(r1)).StatusCode); // reutilización detectada
        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(r2)).StatusCode); // la familia completa quedó revocada

        Task<HttpResponseMessage> Refresh(string token) => f.Anonymous().PostAsync("/v1/auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = token }));
    }

    [Fact]
    public async Task Login_errors_do_not_reveal_which_factor_failed()
    {
        var unknown = await f.LoginAsync("nadie@tenant-a", mfa: false);
        var badPassword = await f.LoginAsync("maker@tenant-b", mfa: false, password: "incorrecta");
        var badOtp = await f.LoginAsync("maker@tenant-b", mfa: true, otp: "000000");
        foreach (var r in new[] { unknown, badPassword, badOtp })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_grant", await r.CodeAsync());
        }
    }

    [Fact]
    public async Task Health_endpoints_are_public_and_minimal()
    {
        Assert.Equal(HttpStatusCode.OK, (await f.Anonymous().GetAsync("/health")).StatusCode);
        var ready = await f.Anonymous().GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", (await ready.JsonAsync()).GetProperty("status").GetString());
    }
}

public class LockoutTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Account_locks_after_repeated_failures_even_with_the_right_password()
    {
        for (var i = 0; i < 5; i++) await f.LoginAsync("admin@tenant-a", mfa: false, password: "mala-" + i);
        var r = await f.LoginAsync("admin@tenant-a", mfa: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

public class RateLimitTests : IClassFixture<RateLimitTests.StrictFactory>
{
    private readonly StrictFactory _f;
    public RateLimitTests(StrictFactory f) => _f = f;

    public sealed class StrictFactory : ApiFactory
    {
        protected override Dictionary<string, string?> Overrides => new() { ["RateLimiting:AuthPerMinute"] = "3" };
    }

    [Fact]
    public async Task Auth_endpoint_is_rate_limited()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await _f.LoginAsync("ops@platform", mfa: false, password: "x")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
