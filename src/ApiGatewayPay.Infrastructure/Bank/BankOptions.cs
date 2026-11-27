using ApiGatewayPay.Infrastructure.Http;

namespace ApiGatewayPay.Infrastructure.Bank;

public sealed class BankOptions
{
    public const string Section = "Bank";

    /// <summary>Mock | Real</summary>
    public string Adapter { get; set; } = "Mock";
    /// <summary>Identificador del proveedor; también es el segmento de la ruta de webhooks.</summary>
    public string Name { get; set; } = "mock";
    public bool KillSwitch { get; set; }
    public MockBankOptions Mock { get; set; } = new();
    public RealBankOptions Real { get; set; } = new();
}

public sealed class MockBankOptions
{
    public int LatencyMilliseconds { get; set; } = 50;
    public int SettlementDelaySeconds { get; set; } = 2;
    public Dictionary<string, long> OpeningBalances { get; set; } = new();
    public MockWebhookOptions Webhooks { get; set; } = new();
}

public sealed class MockWebhookOptions
{
    public bool Enabled { get; set; }
    public string TargetUrl { get; set; } = "http://localhost:8080/v1/webhooks/bank/mock";
    public OutboundHttpOptions Http { get; set; } = new() { AllowedHosts = ["localhost"], RequireHttps = false, AllowPrivateNetworks = true, MaxRetries = 0 };
}

public sealed class RealBankOptions
{
    public string BaseUrl { get; set; } = "";
    /// <summary>RestJson | Iso20022 | Soap</summary>
    public string Format { get; set; } = "RestJson";
    public OutboundAuthOptions Auth { get; set; } = new();
    public MessageSigningOptions Signing { get; set; } = new();
    public OutboundHttpOptions Http { get; set; } = new();
    public BankEndpoints Endpoints { get; set; } = new();
    public Iso20022Options Iso20022 { get; set; } = new();
    public SoapOptions Soap { get; set; } = new();
}

public sealed class MessageSigningOptions
{
    /// <summary>None | HmacSha256 | JwsRs256 | JwsPs256 | JwsEs256</summary>
    public string Algorithm { get; set; } = "None";
    public string? KeySecretName { get; set; }
    public string? KeyId { get; set; }
    public string HeaderName { get; set; } = "X-Signature";
    public bool VerifyResponses { get; set; }
    public string ResponseHeaderName { get; set; } = "X-Bank-Signature";
    /// <summary>Secreto HMAC del banco o su clave pública PEM para verificar respuestas.</summary>
    public string? BankKeySecretName { get; set; }
}

public sealed class BankEndpoints
{
    public string Balance { get; set; } = "/v1/accounts/{accountId}/balance";
    public string Movements { get; set; } = "/v1/accounts/{accountId}/transactions?from={from}&to={to}";
    public string Payments { get; set; } = "/v1/payments";
    public string PaymentStatus { get; set; } = "/v1/payments/{endToEndId}";
    public string Statement { get; set; } = "/v1/statements?date={date}";
}

public sealed class Iso20022Options
{
    public string InitiatingPartyName { get; set; } = "";
    public string InitiatingPartyId { get; set; } = "";
    public string DebtorName { get; set; } = "";
    public string DebtorAgentBic { get; set; } = "";
}

public sealed class SoapOptions
{
    public string Namespace { get; set; } = "urn:bank:payments:v1";
    public string SoapActionPrefix { get; set; } = "urn:bank:payments:v1#";
}
