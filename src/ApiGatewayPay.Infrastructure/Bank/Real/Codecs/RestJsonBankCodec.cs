using System.Globalization;
using System.Text.Json;
using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Infrastructure.Bank.Real.Codecs;

/// <summary>
/// Codec REST/JSON con un contrato canónico (ver contracts/bank-restjson.schema.json). Si el banco usa
/// otros nombres de campo, ajuste los métodos Build*/Parse* aquí: el resto del sistema no cambia.
/// </summary>
public sealed class RestJsonBankCodec(BankEndpoints endpoints) : IBankCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BankHttpRequest BuildInitiatePayment(BankPaymentInstruction i) => new(HttpMethod.Post, endpoints.Payments,
        JsonSerializer.Serialize(new
        {
            endToEndId = i.EndToEndId,
            amount = new { value = i.AmountMinor, currency = i.Currency },
            debtorAccount = i.DebtorAccountId,
            creditor = new { name = i.Creditor.Name, bankCode = i.Creditor.BankCode, accountNumber = i.Creditor.AccountNumber, accountType = i.Creditor.AccountType },
            remittanceInformation = i.RemittanceInformation,
        }, Json), "application/json", Retryable: false);

    public BankPaymentSubmission ParseInitiatePayment(int statusCode, string body)
    {
        using var doc = Parse(body);
        var root = doc.RootElement;
        var status = Str(root, "status");
        return status switch
        {
            "ACCEPTED" or "PENDING" or "SETTLED" => new BankPaymentSubmission(BankSubmissionOutcome.Accepted, OptStr(root, "bankReference"), null),
            "REJECTED" => new BankPaymentSubmission(BankSubmissionOutcome.Rejected, OptStr(root, "bankReference"), Code(OptStr(root, "reasonCode"))),
            _ => new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "unexpected_status"),
        };
    }

    public BankHttpRequest BuildPaymentStatus(string e2e) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.PaymentStatus, ("endToEndId", e2e)), null, "application/json", Retryable: true);

    public BankPaymentStatusInfo ParsePaymentStatus(string e2e, string body)
    {
        using var doc = Parse(body);
        var r = doc.RootElement;
        if (!string.Equals(Str(r, "endToEndId"), e2e, StringComparison.Ordinal))
            throw new BankIntegrityException("La respuesta corresponde a otro EndToEndId.");
        var amount = r.TryGetProperty("amount", out var a) ? a : default;
        return new BankPaymentStatusInfo(e2e, MapState(Str(r, "status")), OptStr(r, "bankReference"), Code(OptStr(r, "reasonCode")),
            amount.ValueKind == JsonValueKind.Object ? Long(amount, "value") : null,
            amount.ValueKind == JsonValueKind.Object ? Currency(amount) : null);
    }

    public BankHttpRequest BuildBalance(BankAccountRef account) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.Balance, ("accountId", account.BankAccountId)), null, "application/json", true);

    public BankBalance ParseBalance(BankAccountRef account, string body)
    {
        using var doc = Parse(body);
        var r = doc.RootElement;
        var currency = Currency(r);
        if (currency != account.Currency) throw new BankIntegrityException("Moneda de saldo inesperada.");
        return new BankBalance(currency, Long(r, "available"), Long(r, "ledger"),
            DateTime.Parse(Str(r, "asOf"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
    }

    public BankHttpRequest BuildMovements(BankAccountRef account, DateOnly from, DateOnly to) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.Movements, ("accountId", account.BankAccountId), ("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), null, "application/json", true);

    public IReadOnlyList<BankMovement> ParseMovements(string body)
    {
        using var doc = Parse(body);
        return Items(doc.RootElement, "items").Select(m => new BankMovement(
            Str(m, "id"),
            DateTime.Parse(Str(m, "bookedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            Positive(Long(m, "amount")), Currency(m),
            Str(m, "direction") == "CREDIT" ? BankMovementDirection.Credit : BankMovementDirection.Debit,
            OptStr(m, "endToEndId"), Truncate(OptStr(m, "description") ?? "", 140))).ToList();
    }

    public BankHttpRequest BuildStatement(DateOnly date) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.Statement, ("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), null, "application/json", true);

    public BankStatement ParseStatement(DateOnly date, string body)
    {
        using var doc = Parse(body);
        return new BankStatement(date, Items(doc.RootElement, "entries").Select(e => new BankStatementEntry(
            OptStr(e, "endToEndId"), Str(e, "bankReference"), Str(e, "debtorAccount"), Positive(Long(e, "amount")), Currency(e), MapState(Str(e, "status")))).ToList());
    }

    public BankWebhookPayload ParseWebhook(byte[] body) => ParseWebhookBody(body);

    public static BankWebhookPayload ParseWebhookBody(byte[] body)
    {
        using var doc = Parse(System.Text.Encoding.UTF8.GetString(body));
        var r = doc.RootElement;
        var eventId = Str(r, "eventId");
        if (eventId.Length > 128) throw new WebhookVerificationException("invalid_event_id");
        var status = OptStr(r, "status");
        return new BankWebhookPayload(eventId, Str(r, "type"), OptStr(r, "endToEndId"), status is null ? null : MapState(status), OptStr(r, "bankReference"), Code(OptStr(r, "reasonCode")));
    }

    internal static BankPaymentState MapState(string status) => status switch
    {
        "PENDING" or "ACCEPTED" => BankPaymentState.Pending,
        "SETTLED" or "COMPLETED" => BankPaymentState.Settled,
        "REJECTED" => BankPaymentState.Rejected,
        "RETURNED" => BankPaymentState.Returned,
        "NOT_FOUND" => BankPaymentState.NotFound,
        _ => throw new BankIntegrityException("Estado desconocido en la respuesta del banco."),
    };

    private static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false });
        }
        catch (JsonException)
        {
            throw new BankIntegrityException("Respuesta JSON inválida.");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 256 } s
            ? s : throw new BankIntegrityException($"Campo requerido ausente o inválido: {name}");

    private static string? OptStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Truncate(v.GetString()!, 256) : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)
            ? l : throw new BankIntegrityException($"Campo numérico inválido: {name}");

    private static string Currency(JsonElement e)
    {
        var c = Str(e, "currency");
        return c.Length == 3 && c.All(char.IsAsciiLetterUpper) ? c : throw new BankIntegrityException("Moneda inválida.");
    }

    private static long Positive(long v) => v >= 0 ? v : throw new BankIntegrityException("Monto negativo inesperado.");

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= 10_000
            ? v.EnumerateArray().ToList() : throw new BankIntegrityException($"Lista inválida: {name}");

    private static string? Code(string? code) => code is null ? null : new string(code.Where(char.IsAsciiLetterOrDigit).Take(16).ToArray());

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
