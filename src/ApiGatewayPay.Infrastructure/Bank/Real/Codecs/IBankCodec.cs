using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Infrastructure.Bank.Real.Codecs;

/// <summary>Petición neutral al transporte que el adaptador firma, autentica y envía.</summary>
public sealed record BankHttpRequest(HttpMethod Method, string PathAndQuery, string? Body, string ContentType, bool Retryable, string? SoapAction = null);

public sealed record BankWebhookPayload(string EventId, string Type, string? EndToEndId, BankPaymentState? State, string? BankReference, string? ReasonCode);

/// <summary>
/// Traduce las operaciones del puerto bancario al formato de mensajes del banco. Implementaciones:
/// RestJson (JSON propio), Iso20022 (pain.001/pain.002/camt.05x) y Soap (plantilla).
/// Toda respuesta se trata como NO confiable: se valida forma, tipos y rangos antes de usarla.
/// </summary>
public interface IBankCodec
{
    BankHttpRequest BuildInitiatePayment(BankPaymentInstruction instruction);
    BankPaymentSubmission ParseInitiatePayment(int statusCode, string body);
    BankHttpRequest BuildPaymentStatus(string endToEndId);
    BankPaymentStatusInfo ParsePaymentStatus(string endToEndId, string body);
    BankHttpRequest BuildBalance(BankAccountRef account);
    BankBalance ParseBalance(BankAccountRef account, string body);
    BankHttpRequest BuildMovements(BankAccountRef account, DateOnly from, DateOnly to);
    IReadOnlyList<BankMovement> ParseMovements(string body);
    BankHttpRequest BuildStatement(DateOnly date);
    BankStatement ParseStatement(DateOnly date, string body);
    BankWebhookPayload ParseWebhook(byte[] body);
}

public static class CurrencyExponent
{
    private static readonly Dictionary<string, int> Exponents = new() { ["JPY"] = 0, ["CLP"] = 0, ["KRW"] = 0, ["PYG"] = 0, ["BHD"] = 3, ["KWD"] = 3 };

    /// <summary>COP, USD, EUR, MXN, PEN... usan 2 decimales (ISO 4217).</summary>
    public static int For(string currency) => Exponents.GetValueOrDefault(currency, 2);

    public static string ToDecimalString(long minor, string currency) =>
        (minor / (decimal)Math.Pow(10, For(currency))).ToString("F" + For(currency), System.Globalization.CultureInfo.InvariantCulture);

    public static long ToMinor(string value, string currency)
    {
        var d = decimal.Parse(value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture);
        var scaled = d * (decimal)Math.Pow(10, For(currency));
        if (scaled != decimal.Truncate(scaled)) throw new BankIntegrityException("Monto con más decimales de los permitidos.");
        return (long)scaled;
    }
}

internal static class Paths
{
    public static string Fill(string template, params (string Key, string Value)[] values)
    {
        foreach (var (k, v) in values) template = template.Replace("{" + k + "}", Uri.EscapeDataString(v), StringComparison.Ordinal);
        return template;
    }
}
