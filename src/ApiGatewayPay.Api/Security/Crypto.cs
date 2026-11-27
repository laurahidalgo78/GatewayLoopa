using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ApiGatewayPay.Api.Security;

/// <summary>PBKDF2-HMAC-SHA256 (600.000 iteraciones, OWASP 2023). Formato: pbkdf2-sha256$iter$salt$hash.</summary>
public static class PasswordHasher
{
    public const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations) || iterations < 100_000)
            return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Hash ficticio para igualar tiempos cuando el usuario no existe (evita enumeración).</summary>
    public static readonly string Dummy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
}

/// <summary>TOTP RFC 6238 (HMAC-SHA1, 30 s, 6 dígitos), compatible con apps autenticadoras.</summary>
public static class Totp
{
    public static string Compute(byte[] key, long timeStep, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        // HMAC-SHA1 es el algoritmo de RFC 6238 que soportan todas las apps autenticadoras; en HMAC no aplica la debilidad de colisiones de SHA-1.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / 30;

    /// <summary>Devuelve el paso de tiempo que coincidió (ventana ±1) para impedir reutilizar el mismo código.</summary>
    public static long? Verify(byte[] key, string code, DateTimeOffset now)
    {
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) return null;
        var step = CurrentStep(now);
        for (var delta = -1; delta <= 1; delta++)
        {
            var candidate = Compute(key, step + delta);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(code)))
                return step + delta;
        }
        return null;
    }

    public static byte[] DecodeBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.TrimEnd('=').Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0) throw new FormatException("Base32 inválido.");
            buffer = ((buffer << 5) | value) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
