using System.Net;
using System.Text;
using ApiGatewayPay.Core.Ports.Bank;
using ApiGatewayPay.Core.UseCases.Webhooks;
using ApiGatewayPay.Infrastructure.Bank;
using ApiGatewayPay.Infrastructure.Bank.Real;
using ApiGatewayPay.Infrastructure.Bank.Real.Codecs;
using ApiGatewayPay.Infrastructure.Bank.Webhooks;
using ApiGatewayPay.Infrastructure.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Tests.Unit;

public class CodecTests
{
    private static readonly BankPaymentInstruction Instruction = new("e2e0000000000000000000000000001", 150000, "COP", "0012345678",
        new BankCreditor("Proveedor & Cía", "1007", "998877665544", "Savings"), "Factura 1");

    [Fact]
    public void Iso20022_builds_pain001_with_decimal_amount_and_e2e()
    {
        var codec = new Iso20022BankCodec(new BankEndpoints(), new Iso20022Options { DebtorName = "Empresa" }, TimeProvider.System);
        var req = codec.BuildInitiatePayment(Instruction);
        Assert.False(req.Retryable);
        Assert.Contains("urn:iso:std:iso:20022:tech:xsd:pain.001.001.09", req.Body);
        Assert.Contains("<EndToEndId>e2e0000000000000000000000000001</EndToEndId>", req.Body);
        Assert.Contains("<InstdAmt Ccy=\"COP\">1500.00</InstdAmt>", req.Body);
        Assert.Contains("Proveedor &amp; Cía", req.Body); // escape XML correcto
    }

    [Theory]
    [InlineData("ACSC", BankPaymentState.Settled)]
    [InlineData("ACSP", BankPaymentState.Pending)]
    [InlineData("RJCT", BankPaymentState.Rejected)]
    public void Iso20022_parses_pain002_status(string code, BankPaymentState expected)
    {
        var xml = $"""
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:pain.002.001.10"><CstmrPmtStsRpt><GrpHdr><MsgId>M1</MsgId></GrpHdr>
            <OrgnlPmtInfAndSts><TxInfAndSts><OrgnlEndToEndId>e2e1</OrgnlEndToEndId><TxSts>{code}</TxSts>
            <StsRsnInf><Rsn><Cd>AC04</Cd></Rsn></StsRsnInf></TxInfAndSts></OrgnlPmtInfAndSts></CstmrPmtStsRpt></Document>
            """;
        var status = new Iso20022BankCodec(new BankEndpoints(), new Iso20022Options(), TimeProvider.System).ParsePaymentStatus("e2e1", xml);
        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void Iso20022_rejects_status_for_another_payment()
    {
        var xml = "<Document><TxInfAndSts><OrgnlEndToEndId>otro</OrgnlEndToEndId><TxSts>ACSC</TxSts></TxInfAndSts></Document>";
        Assert.Throws<BankIntegrityException>(() => new Iso20022BankCodec(new BankEndpoints(), new Iso20022Options(), TimeProvider.System).ParsePaymentStatus("e2e1", xml));
    }

    [Fact]
    public void Iso20022_parses_camt053_debits_for_reconciliation()
    {
        var xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08"><BkToCstmrStmt><Stmt><Acct><Id><Othr><Id>0012345678</Id></Othr></Id></Acct>
            <Ntry><Amt Ccy="COP">1500.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts><BookgDt><Dt>2026-06-01</Dt></BookgDt><AcctSvcrRef>REF1</AcctSvcrRef>
            <NtryDtls><TxDtls><Refs><EndToEndId>e2e1</EndToEndId></Refs></TxDtls></NtryDtls></Ntry>
            <Ntry><Amt Ccy="COP">99.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><BookgDt><Dt>2026-06-01</Dt></BookgDt><AcctSvcrRef>REF2</AcctSvcrRef></Ntry>
            </Stmt></BkToCstmrStmt></Document>
            """;
        var statement = new Iso20022BankCodec(new BankEndpoints(), new Iso20022Options(), TimeProvider.System).ParseStatement(new DateOnly(2026, 6, 1), xml);
        var entry = Assert.Single(statement.Entries);
        Assert.Equal(("e2e1", 150000L, BankPaymentState.Settled), (entry.EndToEndId, entry.AmountMinor, entry.State));
    }

    [Fact]
    public void Xml_parser_rejects_dtd_and_external_entities()
    {
        var xxe = """<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]><Document><TxSts>&x;</TxSts></Document>""";
        Assert.Throws<BankIntegrityException>(() => SafeXml.Parse(xxe));
    }

    [Theory]
    [InlineData("{\"endToEndId\":\"e2e1\",\"status\":\"WHATEVER\"}")]
    [InlineData("{\"endToEndId\":\"otro\",\"status\":\"SETTLED\"}")]
    [InlineData("{\"status\":\"SETTLED\"}")]
    [InlineData("<html>error</html>")]
    public void RestJson_treats_unexpected_responses_as_untrusted(string body) =>
        Assert.Throws<BankIntegrityException>(() => new RestJsonBankCodec(new BankEndpoints()).ParsePaymentStatus("e2e1", body));

    [Theory]
    [InlineData(150000L, "COP", "1500.00")]
    [InlineData(1500L, "JPY", "1500")]
    public void Currency_exponent_roundtrips(long minor, string currency, string text)
    {
        Assert.Equal(text, CurrencyExponent.ToDecimalString(minor, currency));
        Assert.Equal(minor, CurrencyExponent.ToMinor(text, currency));
    }

    [Fact]
    public void Currency_rejects_excess_precision() => Assert.Throws<BankIntegrityException>(() => CurrencyExponent.ToMinor("10.001", "COP"));
}

/// <summary>Clasificación de respuestas del banco real: la diferencia entre "fallido" y "ambiguo" evita dobles débitos.</summary>
public class RealBankAdapterTests
{
    private static readonly BankPaymentInstruction Instruction = new("e2e0000000000000000000000000001", 150000, "COP", "001",
        new BankCreditor("P", "1007", "998877665544", "Savings"), "ref");

    private static RealBankAdapter Adapter(Func<HttpRequestMessage, HttpResponseMessage> respond, Action<HttpRequestMessage>? inspect = null, MessageSigningOptions? signing = null)
    {
        var options = Options.Create(new BankOptions
        {
            Name = "banco-test",
            Real = new RealBankOptions { BaseUrl = "https://api.banco.com", Format = "RestJson", Signing = signing ?? new MessageSigningOptions() },
        });
        var secrets = new DictionarySecrets(new() { ["bank/signing"] = "hmac-key", ["bank/webhook-secret"] = "w" });
        var verifier = new WebhookVerifier(secrets, new StaticMonitor<WebhookSecurityOptions>(new()), TimeProvider.System);
        return new RealBankAdapter(new StubFactory(new StubHandler(respond, inspect)), options, secrets, verifier, TimeProvider.System, NullLogger<RealBankAdapter>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Accepted_response_is_accepted()
    {
        var r = await Adapter(_ => Json(HttpStatusCode.Created, "{\"status\":\"ACCEPTED\",\"bankReference\":\"B1\"}")).InitiatePaymentAsync(Instruction, default);
        Assert.Equal((BankSubmissionOutcome.Accepted, "B1"), (r.Outcome, r.BankReference));
    }

    [Fact]
    public async Task Validation_error_is_a_definitive_rejection()
    {
        var r = await Adapter(_ => Json(HttpStatusCode.UnprocessableEntity, "{\"status\":\"REJECTED\",\"reasonCode\":\"AC01\"}")).InitiatePaymentAsync(Instruction, default);
        Assert.Equal((BankSubmissionOutcome.Rejected, "AC01"), (r.Outcome, r.ReasonCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Server_or_auth_errors_are_ambiguous_never_failed(HttpStatusCode code)
    {
        var r = await Adapter(_ => Json(code, "{}")).InitiatePaymentAsync(Instruction, default);
        Assert.Equal(BankSubmissionOutcome.Ambiguous, r.Outcome);
    }

    [Fact]
    public async Task Timeout_is_ambiguous()
    {
        var r = await Adapter(_ => throw new TaskCanceledException("timeout")).InitiatePaymentAsync(Instruction, default);
        Assert.Equal(BankSubmissionOutcome.Ambiguous, r.Outcome);
    }

    [Fact]
    public async Task Connection_failure_is_ambiguous()
    {
        var r = await Adapter(_ => throw new HttpRequestException("reset")).InitiatePaymentAsync(Instruction, default);
        Assert.Equal(BankSubmissionOutcome.Ambiguous, r.Outcome);
    }

    [Fact]
    public async Task Unparseable_success_is_ambiguous()
    {
        var r = await Adapter(_ => Json(HttpStatusCode.OK, "not json")).InitiatePaymentAsync(Instruction, default);
        Assert.Equal(BankSubmissionOutcome.Ambiguous, r.Outcome);
    }

    [Fact]
    public async Task Invalid_response_signature_is_ambiguous()
    {
        var signing = new MessageSigningOptions { Algorithm = "HmacSha256", KeySecretName = "bank/signing", VerifyResponses = true, BankKeySecretName = "bank/signing" };
        var r = await Adapter(_ =>
        {
            var resp = Json(HttpStatusCode.Created, "{\"status\":\"ACCEPTED\"}");
            resp.Headers.Add("X-Bank-Signature", $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()},n=abc,v1=deadbeef");
            return resp;
        }, signing: signing).InitiatePaymentAsync(Instruction, default);
        Assert.Equal(BankSubmissionOutcome.Ambiguous, r.Outcome);
    }

    [Fact]
    public async Task Requests_carry_idempotency_key_and_signature()
    {
        HttpRequestMessage? seen = null;
        var signing = new MessageSigningOptions { Algorithm = "HmacSha256", KeySecretName = "bank/signing" };
        await Adapter(_ => Json(HttpStatusCode.Created, "{\"status\":\"ACCEPTED\"}"), r => seen = r, signing).InitiatePaymentAsync(Instruction, default);
        Assert.Equal(Instruction.EndToEndId, seen!.Headers.GetValues("Idempotency-Key").Single());
        Assert.Matches("^t=\\d+,n=[0-9a-f]{32},v1=[0-9a-f]{64}$", seen.Headers.GetValues("X-Signature").Single());
    }

    [Fact]
    public async Task Status_404_means_not_found()
    {
        var s = await Adapter(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetPaymentStatusAsync("e2e1", default);
        Assert.Equal(BankPaymentState.NotFound, s.State);
    }

    [Fact]
    public async Task Status_query_failure_is_reported_as_unavailable()
    {
        await Assert.ThrowsAsync<BankUnavailableException>(() => Adapter(_ => Json(HttpStatusCode.ServiceUnavailable, "{}")).GetPaymentStatusAsync("e2e1", default));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, Action<HttpRequestMessage>? inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
