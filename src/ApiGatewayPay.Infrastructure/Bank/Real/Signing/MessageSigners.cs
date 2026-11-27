using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Crypto;

namespace ApiGatewayPay.Infrastructure.Bank.Real.Signing;

/// <summary>Firma peticiones salientes y verifica la firma de las respuestas del banco.</summary>
public interface IMessageSigner
{
    Task SignAsync(HttpRequestMessage request, byte[] body, CancellationToken ct);

    Task VerifyResponseAsync(HttpResponseMessage response, byte[] body, CancellationToken ct);
}

public static class MessageSigner
{
    public static IMessageSigner Create(MessageSigningOptions o, ISecretProvider secrets, TimeProvider time) => o.Algorithm switch
    {
        "None" => new NoSigner(),
        "HmacSha256" => new HmacRequestSigner(o, secrets, time),
        "JwsRs256" or "JwsPs256" or "JwsEs256" => new JwsDetachedSigner(o, secrets, new PemAsymmetricSigner(o, secrets)),
        _ => throw new InvalidOperationException($"Algoritmo de firma no soportado: {o.Algorithm}"),
    };

    private sealed class NoSigner : IMessageSigner
    {
        public Task SignAsync(HttpRequestMessage request, byte[] body, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyResponseAsync(HttpResponseMessage response, byte[] body, CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>
/// HMAC-SHA256 sobre "{ts}.{nonce}.{METHOD} {path}\n{body}". Incluir método y ruta impide reutilizar
/// una firma válida contra otro endpoint.
/// Cabecera: X-Signature: t={ts},n={nonce},v1={hex}
/// </summary>
public sealed class HmacRequestSigner(MessageSigningOptions o, ISecretProvider secrets, TimeProvider time) : IMessageSigner
{
    public async Task SignAsync(HttpRequestMessage request, byte[] body, CancellationToken ct)
    {
        var ts = time.GetUtcNow().ToUnixTimeSeconds();
        var nonce = HmacSignature.NewNonce();
        var key = Encoding.UTF8.GetBytes(await secrets.GetAsync(o.KeySecretName!, ct));
        var signed = Encoding.UTF8.GetBytes($"{request.Method.Method} {request.RequestUri!.PathAndQuery}\n").Concat(body).ToArray();
        request.Headers.TryAddWithoutValidation(o.HeaderName, $"t={ts},n={nonce},v1={HmacSignature.Compute(key, ts, nonce, signed)}");
    }

    public async Task VerifyResponseAsync(HttpResponseMessage response, byte[] body, CancellationToken ct)
    {
        if (!o.VerifyResponses) return;
        var header = response.Headers.TryGetValues(o.ResponseHeaderName, out var v) ? v.FirstOrDefault() : null;
        var parts = header?.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (parts is null || !parts.TryGetValue("t", out var t) || !parts.TryGetValue("n", out var n) || !parts.TryGetValue("v1", out var sig) ||
            !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
            throw new BankIntegrityException("Respuesta del banco sin firma.");
        if (Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - ts) > 300)
            throw new BankIntegrityException("Firma de respuesta fuera de ventana.");
        var key = Encoding.UTF8.GetBytes(await secrets.GetAsync(o.BankKeySecretName!, ct));
        if (!HmacSignature.Verify(key, ts, n, body, sig))
            throw new BankIntegrityException("Firma de respuesta inválida.");
    }
}

/// <summary>Operación de firma asimétrica. La clave puede vivir en memoria (PEM) o en un HSM/KMS remoto.</summary>
public interface IAsymmetricSigner
{
    string Algorithm { get; }
    Task<byte[]> SignAsync(byte[] data, CancellationToken ct);
}

public sealed class PemAsymmetricSigner(MessageSigningOptions o, ISecretProvider secrets) : IAsymmetricSigner
{
    public string Algorithm => o.Algorithm["Jws".Length..];

    public async Task<byte[]> SignAsync(byte[] data, CancellationToken ct)
    {
        var pem = await secrets.GetAsync(o.KeySecretName!, ct);
        if (Algorithm == "ES256")
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            return ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.SignData(data, HashAlgorithmName.SHA256, Algorithm == "PS256" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
    }
}

/// <summary>
/// Firma con HashiCorp Vault Transit: la clave privada nunca sale del gestor (equivalente a un HSM/KMS).
/// Plantilla: registre la clave en Vault ("transit/keys/{KeyId}") y sustituya PemAsymmetricSigner por esta clase.
/// </summary>
public sealed class VaultTransitSigner(IHttpClientFactory httpFactory, string vaultAddress, string tokenFile, string keyName, string algorithm) : IAsymmetricSigner
{
    public string Algorithm => algorithm;

    public async Task<byte[]> SignAsync(byte[] data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{vaultAddress.TrimEnd('/')}/v1/transit/sign/{keyName}/sha2-256")
        {
            Content = JsonContent.Create(new
            {
                input = Convert.ToBase64String(data),
                signature_algorithm = algorithm == "PS256" ? "pss" : "pkcs1v15",
                marshaling_algorithm = algorithm == "ES256" ? "jws" : "asn1",
            }),
        };
        request.Headers.Add("X-Vault-Token", (await File.ReadAllTextAsync(tokenFile, ct)).Trim());
        using var response = await httpFactory.CreateClient(Secrets.VaultSecretProvider.HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
        var signature = doc!.RootElement.GetProperty("data").GetProperty("signature").GetString()!; // "vault:v1:<base64>"
        var raw = signature[(signature.LastIndexOf(':') + 1)..];
        return algorithm == "ES256" ? Base64Url.Decode(raw) : Convert.FromBase64String(raw);
    }
}

/// <summary>
/// JWS con payload desacoplado (RFC 7515 apéndice F), estilo Open Banking: cabecera "x-jws-signature".
/// </summary>
public sealed class JwsDetachedSigner(MessageSigningOptions o, ISecretProvider secrets, IAsymmetricSigner signer) : IMessageSigner
{
    public async Task SignAsync(HttpRequestMessage request, byte[] body, CancellationToken ct)
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = signer.Algorithm,
            ["kid"] = o.KeyId ?? "",
            ["typ"] = "JOSE",
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + Base64Url.Encode(body));
        var signature = await signer.SignAsync(signingInput, ct);
        request.Headers.TryAddWithoutValidation(o.HeaderName, $"{header}..{Base64Url.Encode(signature)}");
    }

    public async Task VerifyResponseAsync(HttpResponseMessage response, byte[] body, CancellationToken ct)
    {
        if (!o.VerifyResponses) return;
        var jws = response.Headers.TryGetValues(o.ResponseHeaderName, out var v) ? v.FirstOrDefault() : null;
        var parts = jws?.Split('.');
        if (parts is not { Length: 3 } || parts[1].Length != 0) throw new BankIntegrityException("Respuesta del banco sin JWS desacoplado.");

        using var header = JsonDocument.Parse(Base64Url.Decode(parts[0]));
        var alg = header.RootElement.GetProperty("alg").GetString();
        // Solo el algoritmo acordado: impide ataques de confusión de algoritmo ("none", HS256 con clave pública...).
        if (alg != signer.Algorithm) throw new BankIntegrityException("Algoritmo JWS no permitido.");

        var data = Encoding.ASCII.GetBytes(parts[0] + "." + Base64Url.Encode(body));
        var sig = Base64Url.Decode(parts[2]);
        var pem = await secrets.GetAsync(o.BankKeySecretName!, ct);
        bool valid;
        if (alg == "ES256")
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            valid = ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        else
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            valid = rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, alg == "PS256" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        }
        if (!valid) throw new BankIntegrityException("Firma JWS de respuesta inválida.");
    }
}
