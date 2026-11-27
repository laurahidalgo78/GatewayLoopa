using System.Globalization;
using System.Xml.Linq;
using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Infrastructure.Bank.Real.Codecs;

/// <summary>
/// PLANTILLA SOAP 1.1. Los servicios SOAP bancarios son específicos de cada banco (WSDL propio): adapte los
/// nombres de operación/elementos a su WSDL. Si el banco exige WS-Security (firma XML), aplíquela en
/// <see cref="RealBankAdapter"/> mediante un IMessageSigner dedicado. Las consultas van por POST, así que
/// se marcan explícitamente como reintentables.
/// </summary>
public sealed class SoapBankCodec(SoapOptions soap) : IBankCodec
{
    private static readonly XNamespace Env = "http://schemas.xmlsoap.org/soap/envelope/";
    private XNamespace Ns => soap.Namespace;

    private BankHttpRequest Envelope(string operation, bool retryable, params object[] content)
    {
        var doc = new XDocument(new XElement(Env + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", Env), new XAttribute(XNamespace.Xmlns + "b", Ns),
            new XElement(Env + "Header"),
            new XElement(Env + "Body", new XElement(Ns + operation, content))));
        return new BankHttpRequest(HttpMethod.Post, "", doc.ToString(SaveOptions.DisableFormatting), "text/xml; charset=utf-8", retryable, soap.SoapActionPrefix + operation);
    }

    private XElement E(string name, object? value) => new(Ns + name, value);

    public BankHttpRequest BuildInitiatePayment(BankPaymentInstruction i) => Envelope("InitiatePayment", false,
        E("EndToEndId", i.EndToEndId), E("Amount", CurrencyExponent.ToDecimalString(i.AmountMinor, i.Currency)), E("Currency", i.Currency),
        E("DebtorAccount", i.DebtorAccountId), E("CreditorName", i.Creditor.Name), E("CreditorBankCode", i.Creditor.BankCode),
        E("CreditorAccount", i.Creditor.AccountNumber), E("CreditorAccountType", i.Creditor.AccountType), E("Reference", i.RemittanceInformation));

    public BankPaymentSubmission ParseInitiatePayment(int statusCode, string body)
    {
        var doc = Body(body);
        return RestJsonBankCodec.MapState(doc.Required("Status")) switch
        {
            BankPaymentState.Rejected => new(BankSubmissionOutcome.Rejected, doc.Text("BankReference"), doc.Text("ReasonCode")),
            BankPaymentState.Pending or BankPaymentState.Settled => new(BankSubmissionOutcome.Accepted, doc.Text("BankReference"), null),
            _ => new(BankSubmissionOutcome.Ambiguous, null, "unexpected_status"),
        };
    }

    public BankHttpRequest BuildPaymentStatus(string e2e) => Envelope("GetPaymentStatus", true, E("EndToEndId", e2e));

    public BankPaymentStatusInfo ParsePaymentStatus(string e2e, string body)
    {
        var doc = Body(body);
        return new(e2e, RestJsonBankCodec.MapState(doc.Required("Status")), doc.Text("BankReference"), doc.Text("ReasonCode"));
    }

    public BankHttpRequest BuildBalance(BankAccountRef account) => Envelope("GetBalance", true, E("AccountId", account.BankAccountId));

    public BankBalance ParseBalance(BankAccountRef account, string body)
    {
        var doc = Body(body);
        return new(account.Currency, CurrencyExponent.ToMinor(doc.Required("Available"), account.Currency),
            CurrencyExponent.ToMinor(doc.Required("Ledger"), account.Currency), DateTime.UtcNow);
    }

    public BankHttpRequest BuildMovements(BankAccountRef account, DateOnly from, DateOnly to) => Envelope("GetMovements", true,
        E("AccountId", account.BankAccountId), E("From", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), E("To", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    public IReadOnlyList<BankMovement> ParseMovements(string body) =>
        Body(body).All("Movement").Take(10_000).Select(m =>
        {
            var ccy = m.Required("Currency");
            return new BankMovement(m.Required("Id"), DateTime.Parse(m.Required("BookedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                CurrencyExponent.ToMinor(m.Required("Amount"), ccy), ccy,
                m.Text("Direction") == "CREDIT" ? BankMovementDirection.Credit : BankMovementDirection.Debit, m.Text("EndToEndId"), m.Text("Description") ?? "");
        }).ToList();

    public BankHttpRequest BuildStatement(DateOnly date) => Envelope("GetStatement", true, E("Date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    public BankStatement ParseStatement(DateOnly date, string body) =>
        new(date, Body(body).All("Entry").Take(10_000).Select(e =>
        {
            var ccy = e.Required("Currency");
            return new BankStatementEntry(e.Text("EndToEndId"), e.Required("BankReference"), e.Required("DebtorAccount"),
                CurrencyExponent.ToMinor(e.Required("Amount"), ccy), ccy, RestJsonBankCodec.MapState(e.Required("Status")));
        }).ToList());

    public BankWebhookPayload ParseWebhook(byte[] body) => RestJsonBankCodec.ParseWebhookBody(body);

    private static XElement Body(string xml)
    {
        var doc = SafeXml.Parse(xml);
        if (doc.First("Fault") is { } fault) throw new BankIntegrityException("SOAP Fault: " + (fault.Text("faultcode") ?? "unknown"));
        return doc.First("Body") ?? throw new BankIntegrityException("Respuesta SOAP sin Body.");
    }
}
