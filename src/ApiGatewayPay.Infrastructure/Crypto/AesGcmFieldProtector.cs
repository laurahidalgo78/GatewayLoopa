using System.Security.Cryptography;
using System.Text;
using ApiGatewayPay.Core.Ports.Security;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Infrastructure.Crypto;

public sealed class CryptoOptions
{
    public const string Section = "Crypto";

    /// <summary>Versión de clave activa para cifrar. Las anteriores se conservan para descifrar (rotación).</summary>
    public int ActiveKeyVersion { get; set; } = 1;
    public string FieldKeySecretPrefix { get; set; } = "crypto/field-key-v";
    public string FingerprintKeySecretName { get; set; } = "crypto/fingerprint-key";
}

/// <summary>
/// Cifrado de campo AES-256-GCM (autenticado). Formato: "v{n}.{base64url(nonce|ciphertext|tag)}".
/// El "purpose" se usa como AAD: el texto cifrado queda atado a su campo y tenant.
/// Las claves (32 bytes base64) viven en el gestor de secretos/KMS, nunca en la base de datos.
/// </summary>
public sealed class AesGcmFieldProtector(ISecretProvider secrets, IOptionsMonitor<CryptoOptions> options) : IFieldProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async ValueTask<string> ProtectAsync(string plaintext, string purpose, CancellationToken ct)
    {
        var version = options.CurrentValue.ActiveKeyVersion;
        var key = await GetKeyAsync(options.CurrentValue.FieldKeySecretPrefix + version, ct);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length), Encoding.UTF8.GetBytes(purpose));
        CryptographicOperations.ZeroMemory(plain);
        return $"v{version}.{Base64Url.Encode(output)}";
    }

    public async ValueTask<string> UnprotectAsync(string ciphertext, string purpose, CancellationToken ct)
    {
        var dot = ciphertext.IndexOf('.');
        if (dot < 2 || ciphertext[0] != 'v' || !int.TryParse(ciphertext.AsSpan(1, dot - 1), out var version))
            throw new CryptographicException("Formato de texto cifrado inválido.");
        var data = Base64Url.Decode(ciphertext[(dot + 1)..]);
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Texto cifrado truncado.");
        var key = await GetKeyAsync(options.CurrentValue.FieldKeySecretPrefix + version, ct);
        var plainLength = data.Length - NonceSize - TagSize;
        var plain = new byte[plainLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, plainLength), data.AsSpan(NonceSize + plainLength), plain, Encoding.UTF8.GetBytes(purpose));
        return Encoding.UTF8.GetString(plain);
    }

    public async ValueTask<string> FingerprintAsync(string value, string purpose, CancellationToken ct)
    {
        var key = await GetKeyAsync(options.CurrentValue.FingerprintKeySecretName, ct);
        return Base64Url.Encode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(purpose + ":" + value)));
    }

    private async ValueTask<byte[]> GetKeyAsync(string name, CancellationToken ct)
    {
        var key = Convert.FromBase64String(await secrets.GetAsync(name, ct));
        if (key.Length != 32) throw new CryptographicException($"La clave {name} debe ser de 256 bits.");
        return key;
    }
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => System.Buffers.Text.Base64Url.EncodeToString(data);

    public static byte[] Decode(string value) => System.Buffers.Text.Base64Url.DecodeFromChars(value);
}
