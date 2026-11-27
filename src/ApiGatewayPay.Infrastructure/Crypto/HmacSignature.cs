using System.Security.Cryptography;
using System.Text;

namespace ApiGatewayPay.Infrastructure.Crypto;

/// <summary>
/// Firma HMAC-SHA256 de mensajes con timestamp y nonce: "{timestamp}.{nonce}.{body}".
/// Usada para webhooks entrantes, notificaciones al backend y firma de peticiones a bancos tipo HMAC.
/// </summary>
public static class HmacSignature
{
    public static string Compute(byte[] key, long timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.{nonce}.");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }

    /// <summary>Comparación en tiempo constante para no filtrar la firma por diferencias de tiempo.</summary>
    public static bool Verify(byte[] key, long timestamp, string nonce, ReadOnlySpan<byte> body, string signatureHex)
    {
        var expected = Encoding.ASCII.GetBytes(Compute(key, timestamp, nonce, body));
        var provided = Encoding.ASCII.GetBytes(signatureHex.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
