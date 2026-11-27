using System.Net;
using System.Security.Cryptography;
using System.Text;
using ApiGatewayPay.Api.Security;
using ApiGatewayPay.Core.Common;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.UseCases.Webhooks;
using ApiGatewayPay.Infrastructure.Bank.Webhooks;
using ApiGatewayPay.Infrastructure.Crypto;
using ApiGatewayPay.Infrastructure.Http;
using ApiGatewayPay.Infrastructure.Secrets;
using ApiGatewayPay.Infrastructure.Platform;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Tests.Unit;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]   // metadatos AWS/Azure/GCP
    [InlineData("100.100.100.200")]   // metadatos Alibaba
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")]     // metadatos AWS IPv6
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]  // IPv4 mapeada en IPv6
    [InlineData("::ffff:169.254.169.254")]
    public void Blocks_internal_and_metadata_addresses(string ip) => Assert.True(SsrfGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Allows_public_addresses(string ip) => Assert.False(SsrfGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("api.bank.com", true)]
    [InlineData("API.BANK.COM", true)]
    [InlineData("x.partner.com", true)]
    [InlineData("partner.com", false)]          // el comodín no cubre el dominio raíz
    [InlineData("evilpartner.com", false)]      // sufijo sin punto
    [InlineData("api.bank.com.evil.io", false)]
    [InlineData("169.254.169.254", false)]
    public void Host_allowlist_is_exact_or_subdomain_wildcard(string host, bool allowed) =>
        Assert.Equal(allowed, HostAllowlistHandler.IsAllowed(host, ["api.bank.com", "*.partner.com"]));

    [Fact]
    public async Task Allowlist_handler_blocks_before_any_network_io()
    {
        var handler = new HostAllowlistHandler("bank", new OutboundHttpOptions { AllowedHosts = ["api.bank.com"] }) { InnerHandler = new ThrowingHandler() };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.GetAsync("https://evil.com/x"));
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.GetAsync("http://api.bank.com/x"));       // https obligatorio
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.GetAsync("https://user:pass@api.bank.com/")); // credenciales en URL
    }

    [Fact]
    public void Payment_initiation_is_never_retried_by_the_http_pipeline()
    {
        Assert.False(HardenedHttpClientExtensions.IsRetryable(new HttpRequestMessage(HttpMethod.Post, "https://api.bank.com/payments")));
        Assert.True(HardenedHttpClientExtensions.IsRetryable(new HttpRequestMessage(HttpMethod.Get, "https://api.bank.com/payments/1")));
        var soapQuery = new HttpRequestMessage(HttpMethod.Post, "https://api.bank.com/soap");
        soapQuery.Options.Set(HardenedHttpClientExtensions.Retryable, true);
        Assert.True(HardenedHttpClientExtensions.IsRetryable(soapQuery));
    }

    [Theory]
    [InlineData("/v1/accounts/0012345678901/balance", "/v1/accounts/***8901/balance")]
    [InlineData("/v1/payments/abc", "/v1/payments/abc")]
    public void Outbound_log_paths_are_redacted(string path, string expected) => Assert.Equal(expected, OutboundLoggingHandler.RedactPath(path));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debería llegar a la red");
    }
}

public class WebhookVerifierTests
{
    private const string Secret = "webhook-secret-for-tests";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static WebhookVerifier Verifier(FakeTime time) => new(
        new DictionarySecrets(new() { ["bank/webhook-secret"] = Secret }),
        new StaticMonitor<WebhookSecurityOptions>(new WebhookSecurityOptions()), time);

    private static BankWebhookEnvelope Signed(byte[] body, long ts, string nonce, string? signature = null) => new(
        new Dictionary<string, string>
        {
            ["X-Bank-Timestamp"] = ts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Bank-Nonce"] = nonce,
            ["X-Bank-Signature"] = "v1=" + (signature ?? HmacSignature.Compute(Encoding.UTF8.GetBytes(Secret), ts, nonce, body)),
        }, body);

    [Fact]
    public async Task Accepts_valid_signature()
    {
        var body = "{\"eventId\":\"e1\"}"u8.ToArray();
        var (_, nonce) = await Verifier(new FakeTime(Now)).VerifyAsync(Signed(body, Now.ToUnixTimeSeconds(), "0123456789abcdef0123"), default);
        Assert.Equal("0123456789abcdef0123", nonce);
    }

    [Fact]
    public async Task Rejects_tampered_body()
    {
        var envelope = Signed("{\"amount\":1}"u8.ToArray(), Now.ToUnixTimeSeconds(), "0123456789abcdef0123");
        var tampered = envelope with { Body = "{\"amount\":9}"u8.ToArray() };
        var ex = await Assert.ThrowsAsync<WebhookVerificationException>(() => Verifier(new FakeTime(Now)).VerifyAsync(tampered, default));
        Assert.Equal("invalid_signature", ex.Reason);
    }

    [Fact]
    public async Task Rejects_stale_timestamp()
    {
        var ex = await Assert.ThrowsAsync<WebhookVerificationException>(() =>
            Verifier(new FakeTime(Now)).VerifyAsync(Signed("{}"u8.ToArray(), Now.AddMinutes(-10).ToUnixTimeSeconds(), "0123456789abcdef0123"), default));
        Assert.Equal("stale_timestamp", ex.Reason);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bad nonce with spaces!!")]
    public async Task Rejects_malformed_nonce(string nonce) =>
        await Assert.ThrowsAsync<WebhookVerificationException>(() =>
            Verifier(new FakeTime(Now)).VerifyAsync(Signed("{}"u8.ToArray(), Now.ToUnixTimeSeconds(), nonce), default));

    [Fact]
    public async Task Rejects_missing_signature()
    {
        var envelope = new BankWebhookEnvelope(new Dictionary<string, string>(), "{}"u8.ToArray());
        var ex = await Assert.ThrowsAsync<WebhookVerificationException>(() => Verifier(new FakeTime(Now)).VerifyAsync(envelope, default));
        Assert.Equal("missing_signature", ex.Reason);
    }
}

public class CryptoTests
{
    private static readonly string Key1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string Key2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmFieldProtector Protector(int activeVersion) => new(
        new DictionarySecrets(new() { ["crypto/field-key-v1"] = Key1, ["crypto/field-key-v2"] = Key2, ["crypto/fingerprint-key"] = Key1 }),
        new StaticMonitor<CryptoOptions>(new CryptoOptions { ActiveKeyVersion = activeVersion }));

    [Fact]
    public async Task Field_encryption_roundtrips_and_hides_plaintext()
    {
        var cipher = await Protector(1).ProtectAsync("0012345678", "beneficiary.account:t1", default);
        Assert.StartsWith("v1.", cipher);
        Assert.DoesNotContain("0012345678", cipher);
        Assert.Equal("0012345678", await Protector(1).UnprotectAsync(cipher, "beneficiary.account:t1", default));
    }

    [Fact]
    public async Task Ciphertext_is_bound_to_its_purpose_tenant()
    {
        var cipher = await Protector(1).ProtectAsync("0012345678", "beneficiary.account:t1", default);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await Protector(1).UnprotectAsync(cipher, "beneficiary.account:t2", default));
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var cipher = await Protector(1).ProtectAsync("0012345678", "p", default);
        var bytes = Base64Url.Decode(cipher[3..]);
        bytes[^1] ^= 0x01;
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await Protector(1).UnprotectAsync("v1." + Base64Url.Encode(bytes), "p", default));
    }

    [Fact]
    public async Task Key_rotation_keeps_old_ciphertexts_readable()
    {
        var old = await Protector(1).ProtectAsync("secret", "p", default);
        var rotated = Protector(2);
        Assert.StartsWith("v2.", await rotated.ProtectAsync("secret", "p", default));
        Assert.Equal("secret", await rotated.UnprotectAsync(old, "p", default));
    }

    [Fact]
    public async Task Fingerprint_is_deterministic_per_purpose()
    {
        var p = Protector(1);
        Assert.Equal(await p.FingerprintAsync("x", "a", default), await p.FingerprintAsync("x", "a", default));
        Assert.NotEqual(await p.FingerprintAsync("x", "a", default), await p.FingerprintAsync("x", "b", default));
    }

    [Fact]
    public void Totp_matches_rfc6238_vectors()
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(key, Totp.DecodeBase32("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"));
        Assert.Equal("287082", Totp.Compute(key, 59 / 30));
        Assert.Equal("081804", Totp.Compute(key, 1111111109 / 30));
        Assert.Equal("050471", Totp.Compute(key, 1111111111 / 30));
    }

    [Fact]
    public void Totp_accepts_only_adjacent_windows()
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.CurrentStep(now);
        Assert.Equal(step, Totp.Verify(key, Totp.Compute(key, step), now));
        Assert.Equal(step + 1, Totp.Verify(key, Totp.Compute(key, step + 1), now));
        Assert.Null(Totp.Verify(key, Totp.Compute(key, step + 3), now));
        Assert.Null(Totp.Verify(key, "12345", now));
    }

    [Fact]
    public void Password_hasher_verifies_and_rejects()
    {
        var hash = PasswordHasher.Hash("correct horse");
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("Correct horse", hash));
        Assert.False(PasswordHasher.Verify("x", "md5$1$abc$def"));
    }

    [Fact]
    public void Hmac_verification_is_exact()
    {
        var key = "k"u8.ToArray();
        var sig = HmacSignature.Compute(key, 100, "nonce", "body"u8);
        Assert.True(HmacSignature.Verify(key, 100, "nonce", "body"u8, sig));
        Assert.False(HmacSignature.Verify(key, 101, "nonce", "body"u8, sig));
        Assert.False(HmacSignature.Verify(key, 100, "nonce2", "body"u8, sig));
    }
}

public class SecretIsolationTests
{
    private sealed class Recording : Core.Ports.Security.ISecretProvider
    {
        public string? Last;
        public ValueTask<string> GetAsync(string name, CancellationToken ct) { Last = name; return ValueTask.FromResult("v"); }
    }

    [Fact]
    public async Task Secrets_are_scoped_to_the_platform_environment()
    {
        var inner = new Recording();
        var sandbox = new EnvironmentScopedSecretProvider(inner, Options.Create(new PlatformOptions { Environment = PlatformEnvironment.Sandbox }));
        await sandbox.GetAsync("bank/key", default);
        Assert.Equal("sandbox/bank/key", inner.Last);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sandbox.GetAsync("production/bank/key", default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sandbox.GetAsync("../production/bank/key", default));
    }

    [Fact]
    public async Task External_services_only_reach_their_own_namespace()
    {
        var inner = new Recording();
        var scoped = new PrefixedSecretProvider(inner, "services/sms");
        await scoped.GetAsync("api-key", default);
        Assert.Equal("services/sms/api-key", inner.Last);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await scoped.GetAsync("../../bank/key", default));
    }

    [Theory]
    [InlineData("0012345678901", "********8901")]
    [InlineData("123", "***")]
    public void Account_numbers_are_masked(string input, string expected) => Assert.Equal(expected, Masking.MaskAccount(input));
}
