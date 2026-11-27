using System.Globalization;
using System.Xml.Linq;
using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Infrastructure.Bank.Real.Codecs;

/// <summary>
/// Codec ISO 20022: pain.001.001.09 (iniciación), pain.002 (estado), camt.052 (saldo/movimientos) y
/// camt.053 (extracto para conciliación). El parseo es por nombre local, tolerante a versiones del esquema.
/// Los bancos suelen exigir una variante nacional (usage guidelines): revise campos obligatorios con el banco.
/// </summary>
public sealed class Iso20022BankCodec(BankEndpoints endpoints, Iso20022Options iso, TimeProvider time) : IBankCodec
{
    private static readonly XNamespace Pain001 = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.09";

    public BankHttpRequest BuildInitiatePayment(BankPaymentInstruction i)
    {
        var amount = CurrencyExponent.ToDecimalString(i.AmountMinor, i.Currency);
        var now = time.GetUtcNow();
        XElement E(string name, params object?[] content) => new(Pain001 + name, content);
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            E("Document",
                E("CstmrCdtTrfInitn",
                    E("GrpHdr",
                        E("MsgId", i.EndToEndId),
                        E("CreDtTm", now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                        E("NbOfTxs", "1"),
                        E("CtrlSum", amount),
                        E("InitgPty", E("Nm", iso.InitiatingPartyName), E("Id", E("OrgId", E("Othr", E("Id", iso.InitiatingPartyId)))))),
                    E("PmtInf",
                        E("PmtInfId", i.EndToEndId),
                        E("PmtMtd", "TRF"),
                        E("NbOfTxs", "1"),
                        E("CtrlSum", amount),
                        E("ReqdExctnDt", E("Dt", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
                        E("Dbtr", E("Nm", iso.DebtorName)),
                        E("DbtrAcct", E("Id", E("Othr", E("Id", i.DebtorAccountId))), E("Ccy", i.Currency)),
                        E("DbtrAgt", E("FinInstnId", E("BICFI", iso.DebtorAgentBic))),
                        E("CdtTrfTxInf",
                            E("PmtId", E("EndToEndId", i.EndToEndId)),
                            E("Amt", new XElement(Pain001 + "InstdAmt", new XAttribute("Ccy", i.Currency), amount)),
                            E("CdtrAgt", E("FinInstnId", E("ClrSysMmbId", E("MmbId", i.Creditor.BankCode)))),
                            E("Cdtr", E("Nm", Truncate(i.Creditor.Name, 140))),
                            E("CdtrAcct", E("Id", E("Othr", E("Id", i.Creditor.AccountNumber), E("SchmeNm", E("Prtry", i.Creditor.AccountType))))),
                            E("RmtInf", E("Ustrd", Truncate(i.RemittanceInformation, 140))))))));
        return new BankHttpRequest(HttpMethod.Post, endpoints.Payments, doc.Declaration + doc.ToString(SaveOptions.DisableFormatting), "application/xml", Retryable: false);
    }

    public BankPaymentSubmission ParseInitiatePayment(int statusCode, string body)
    {
        var doc = SafeXml.Parse(body);
        var (state, reason) = TxStatus(doc);
        return state switch
        {
            BankPaymentState.Rejected => new BankPaymentSubmission(BankSubmissionOutcome.Rejected, doc.Text("AcctSvcrRef"), reason),
            BankPaymentState.Pending or BankPaymentState.Settled => new BankPaymentSubmission(BankSubmissionOutcome.Accepted, doc.Text("AcctSvcrRef") ?? doc.Text("MsgId"), null),
            _ => new BankPaymentSubmission(BankSubmissionOutcome.Ambiguous, null, "unexpected_status"),
        };
    }

    public BankHttpRequest BuildPaymentStatus(string e2e) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.PaymentStatus, ("endToEndId", e2e)), null, "application/xml", true);

    public BankPaymentStatusInfo ParsePaymentStatus(string e2e, string body)
    {
        var doc = SafeXml.Parse(body);
        if (doc.Text("OrgnlEndToEndId") is { } original && original != e2e)
            throw new BankIntegrityException("El pain.002 corresponde a otro EndToEndId.");
        var (state, reason) = TxStatus(doc);
        return new BankPaymentStatusInfo(e2e, state, doc.Text("AcctSvcrRef"), reason);
    }

    public BankHttpRequest BuildBalance(BankAccountRef account) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.Balance, ("accountId", account.BankAccountId)), null, "application/xml", true);

    /// <summary>camt.052: ITAV/CLAV = disponible, ITBD/CLBD = contable.</summary>
    public BankBalance ParseBalance(BankAccountRef account, string body)
    {
        var doc = SafeXml.Parse(body);
        long Find(params string[] codes)
        {
            var bal = doc.All("Bal").FirstOrDefault(b => codes.Contains(b.Text("Cd")))
                ?? throw new BankIntegrityException("Saldo ausente en camt.052.");
            var amt = bal.First("Amt")!;
            if (amt.Attribute("Ccy")?.Value != account.Currency) throw new BankIntegrityException("Moneda de saldo inesperada.");
            var minor = CurrencyExponent.ToMinor(amt.Value, account.Currency);
            return bal.Text("CdtDbtInd") == "DBIT" ? -minor : minor;
        }
        return new BankBalance(account.Currency, Find("ITAV", "CLAV"), Find("ITBD", "CLBD"), time.GetUtcNow().UtcDateTime);
    }

    public BankHttpRequest BuildMovements(BankAccountRef account, DateOnly from, DateOnly to) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.Movements, ("accountId", account.BankAccountId), ("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), null, "application/xml", true);

    public IReadOnlyList<BankMovement> ParseMovements(string body) =>
        Entries(SafeXml.Parse(body)).Select(n => new BankMovement(n.Reference, n.Booked, n.Amount, n.Currency, n.Direction, n.EndToEndId, n.Description)).ToList();

    public BankHttpRequest BuildStatement(DateOnly date) =>
        new(HttpMethod.Get, Paths.Fill(endpoints.Statement, ("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), null, "application/xml", true);

    /// <summary>camt.053: solo débitos (pagos salientes) participan en la conciliación de pagos.</summary>
    public BankStatement ParseStatement(DateOnly date, string body)
    {
        var doc = SafeXml.Parse(body);
        var account = doc.First("Acct")?.Text("Id") ?? "";
        return new BankStatement(date, Entries(doc).Where(n => n.Direction == BankMovementDirection.Debit)
            .Select(n => new BankStatementEntry(n.EndToEndId, n.Reference, account, n.Amount, n.Currency, n.Returned ? BankPaymentState.Returned : BankPaymentState.Settled))
            .ToList());
    }

    public BankWebhookPayload ParseWebhook(byte[] body)
    {
        var doc = SafeXml.Parse(System.Text.Encoding.UTF8.GetString(body));
        var (state, reason) = TxStatus(doc);
        return new BankWebhookPayload(doc.Required("MsgId"), "pain.002", doc.Text("OrgnlEndToEndId"), state, doc.Text("AcctSvcrRef"), reason);
    }

    private static (BankPaymentState State, string? Reason) TxStatus(XDocument doc)
    {
        var status = doc.Text("TxSts") ?? doc.Text("GrpSts") ?? throw new BankIntegrityException("pain.002 sin estado.");
        var reason = doc.First("StsRsnInf")?.Text("Cd");
        return status switch
        {
            "ACSC" or "ACCC" => (BankPaymentState.Settled, null),
            "ACTC" or "ACCP" or "ACSP" or "ACWC" or "PDNG" or "RCVD" => (BankPaymentState.Pending, null),
            "RJCT" => (BankPaymentState.Rejected, reason),
            _ => throw new BankIntegrityException("Código de estado ISO 20022 desconocido."),
        };
    }

    private sealed record Entry(string Reference, DateTime Booked, long Amount, string Currency, BankMovementDirection Direction, string? EndToEndId, string Description, bool Returned);

    private static IEnumerable<Entry> Entries(XDocument doc) => doc.All("Ntry").Take(10_000).Select(n =>
    {
        var amt = n.Elements().First(e => e.Name.LocalName == "Amt");
        var ccy = amt.Attribute("Ccy")?.Value ?? throw new BankIntegrityException("Movimiento sin moneda.");
        var booked = n.First("BookgDt")?.Text("Dt") ?? n.First("BookgDt")?.Text("DtTm") ?? throw new BankIntegrityException("Movimiento sin fecha.");
        return new Entry(
            n.Text("AcctSvcrRef") ?? n.Text("NtryRef") ?? "",
            DateTime.Parse(booked, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            CurrencyExponent.ToMinor(amt.Value, ccy), ccy,
            n.Text("CdtDbtInd") == "CRDT" ? BankMovementDirection.Credit : BankMovementDirection.Debit,
            n.Text("EndToEndId") is { } e2e && e2e != "NOTPROVIDED" ? e2e : null,
            Truncate(n.Text("AddtlNtryInf") ?? n.Text("Ustrd") ?? "", 140),
            n.First("RtrInf") is not null);
    });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
