using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ApiGatewayPay.Api.Security;
using ApiGatewayPay.Infrastructure.Bank.Mock;
using ApiGatewayPay.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiGatewayPay.Tests.Integration;

/// <summary>
/// API completa en memoria: configuración de desarrollo (emisor local + mocks) sobre una base SQLite propia,
/// con el worker de cola activo y tiempos acelerados.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "DevOnly-Sandbox-2026!";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"agp-test-{Guid.NewGuid():N}.db");
    private readonly ConcurrentDictionary<string, string> _tokens = new();
    private readonly ConcurrentDictionary<string, long> _lastTotpStep = new();

    protected virtual Dictionary<string, string?> Overrides => new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Persistence:ConnectionString"] = $"Data Source={_db}",
                ["Bank:Mock:Webhooks:Enabled"] = "false",
                ["Bank:Mock:SettlementDelaySeconds"] = "0",
                ["Bank:Mock:LatencyMilliseconds"] = "0",
                ["Queue:PollIntervalMilliseconds"] = "100",
                ["Payments:FirstInquiryDelaySeconds"] = "1",
                ["Payments:AmbiguousInquiryDelaySeconds"] = "1",
                ["Payments:NotFoundGraceSeconds"] = "0",
                ["RateLimiting:AuthPerMinute"] = "1000",
                ["RateLimiting:MoneyPerMinute"] = "1000",
                ["RateLimiting:GlobalPerMinute"] = "100000",
                ["Fraud:VelocityMaxPayments"] = "1000",
                ["Fraud:ReviewFirstPaymentToBeneficiary"] = "false",
            };
            foreach (var (k, v) in Overrides) settings[k] = v;
            c.AddInMemoryCollection(settings);
        });
    }

    public MockBankState Bank => Services.GetRequiredService<MockBankState>();

    public T WithDb<T>(Func<AppDbContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public string Secret(string name) => Services.GetRequiredService<IConfiguration>()[$"DevSecrets:sandbox/{name}"]!;

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Cliente autenticado. Los tokens se cachean por usuario: cada código TOTP es de un solo uso.</summary>
    public async Task<HttpClient> As(string user, bool mfa = true)
    {
        var key = $"{user}|{mfa}";
        if (!_tokens.TryGetValue(key, out var token))
        {
            var response = await LoginAsync(user, mfa);
            response.EnsureSuccessStatusCode();
            token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
            _tokens[key] = token;
        }
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public Task<HttpResponseMessage> LoginAsync(string user, bool mfa, string? password = null, string? otp = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = user, ["password"] = password ?? Password };
        if (mfa) form["otp"] = otp ?? NextTotp(user);
        return Anonymous().PostAsync("/v1/auth/token", new FormUrlEncodedContent(form));
    }

    /// <summary>Código para la ventana actual o siguiente no usada aún (el servidor tolera ±1 paso).</summary>
    public string NextTotp(string user)
    {
        var u = Services.GetRequiredService<IConfiguration>().GetSection("Auth:Local:Users").Get<List<LocalUser>>()!.Single(x => x.Username == user);
        var key = Totp.DecodeBase32(Secret(u.TotpSecretName));
        var current = Totp.CurrentStep(DateTimeOffset.UtcNow);
        var step = _lastTotpStep.AddOrUpdate(user, current, (_, last) => Math.Max(current, last + 1));
        if (step > current + 1) throw new InvalidOperationException("Demasiados logins MFA del mismo usuario en 30 s; reutilice el token.");
        return Totp.Compute(key, step);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}

public static class HttpExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string> CodeAsync(this HttpResponseMessage r) => (await r.JsonAsync()).GetProperty("code").GetString()!;

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient c, string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: ApiFactory.Json) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return c.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostRawAsync(this HttpClient c, string url, string json, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return c.SendAsync(request);
    }

    public static string NewKey() => Guid.NewGuid().ToString("N");
}

/// <summary>Flujos comunes de negocio para las pruebas.</summary>
public static class Flows
{
    public static async Task<Guid> ActiveBeneficiaryAsync(ApiFactory f, string maker = "maker@tenant-a", string checker = "checker@tenant-a")
    {
        var makerClient = await f.As(maker);
        var account = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var created = await makerClient.PostJsonAsync("/v1/beneficiaries", new { displayName = "Proveedor Test", bankCode = "1007", accountType = "savings", accountNumber = account });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.JsonAsync()).GetProperty("id").GetGuid();
        var approved = await (await f.As(checker)).PostAsync($"/v1/beneficiaries/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return id;
    }

    public static async Task<JsonElement> CreatePaymentAsync(HttpClient client, Guid beneficiary, long amountMinor, string? key = null, string reference = "Factura test", string currency = "COP")
    {
        var r = await client.PostJsonAsync("/v1/payments", new { beneficiaryId = beneficiary, amountMinor, currency, reference }, key ?? HttpExtensions.NewKey());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await r.JsonAsync();
    }

    public static async Task<JsonElement> WaitForStatusAsync(HttpClient client, string id, params string[] expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        JsonElement payment;
        do
        {
            payment = await (await client.GetAsync($"/v1/payments/{id}")).JsonAsync();
            if (expected.Contains(payment.GetProperty("status").GetString())) return payment;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        throw new Xunit.Sdk.XunitException($"El pago {id} quedó en {payment.GetProperty("status")} (esperado {string.Join("/", expected)})");
    }
}
