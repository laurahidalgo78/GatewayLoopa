using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Core.UseCases.Webhooks;
using ApiGatewayPay.Infrastructure.Crypto;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Bank.Webhooks;

/// <summary>
/// Verificación de webhooks: firma sobre "{timestamp}.{nonce}.{body}" (HMAC o RSA/ECDSA del banco),
/// timestamp dentro de la tolerancia y nonce presente (el anti-replay lo completa la inbox con el nonce único).
/// </summary>
public sealed class WebhookVerifier(ISecretProvider secrets, IOptionsMonitor<WebhookSecurityOptions> options, TimeProvider time)
{
    public async Task<(DateTime Timestamp, string Nonce)> VerifyAsync(BankWebhookEnvelope envelope, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var signature = Header(envelope, o.SignatureHeader) ?? throw Reject("missing_signature");
        var tsRaw = Header(envelope, o.TimestampHeader) ?? throw Reject("missing_timestamp");
        var nonce = Header(envelope, o.NonceHeader) ?? throw Reject("missing_nonce");
        if (nonce.Length is < 16 or > 128 || !nonce.All(char.IsAsciiLetterOrDigit)) throw Reject("invalid_nonce");
        if (!long.TryParse(tsRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) throw Reject("invalid_timestamp");

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > o.ToleranceSeconds) throw Reject("stale_timestamp");

        var valid = o.Algorithm switch
        {
            "HmacSha256" => HmacSignature.Verify(Encoding.UTF8.GetBytes(await secrets.GetAsync(o.SecretName, ct)), ts, nonce, envelope.Body,
                signature.StartsWith("v1=", StringComparison.Ordinal) ? signature[3..] : signature),
            "RsaSha256" or "EcdsaSha256" => VerifyAsymmetric(o.Algorithm, await secrets.GetAsync(o.SecretName, ct), ts, nonce, envelope.Body, signature),
            _ => throw Reject("unsupported_algorithm"),
        };
        if (!valid) throw Reject("invalid_signature");
        return (DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime, nonce);
    }

    private static bool VerifyAsymmetric(string algorithm, string publicKeyPem, long ts, string nonce, byte[] body, string signatureB64)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureB64); } catch (FormatException) { return false; }
        var data = Encoding.UTF8.GetBytes($"{ts}.{nonce}.").Concat(body).ToArray();
        if (algorithm == "RsaSha256")
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
    }

    private static string? Header(BankWebhookEnvelope e, string name) =>
        e.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static WebhookVerificationException Reject(string reason)
    {
        Telemetry.AppMetrics.WebhookRejected(reason);
        return new WebhookVerificationException(reason);
    }
}
