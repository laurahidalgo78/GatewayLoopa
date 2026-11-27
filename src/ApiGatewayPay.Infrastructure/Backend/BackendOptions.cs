using ApiGatewayPay.Infrastructure.Http;

namespace ApiGatewayPay.Infrastructure.Backend;

public sealed class BackendOptions
{
    public const string Section = "Backend";

    /// <summary>Mock | Http</summary>
    public string Adapter { get; set; } = "Mock";
    public MockBackendOptions Mock { get; set; } = new();
    public HttpBackendOptions Http { get; set; } = new();
}

public sealed class MockBackendOptions
{
    public Dictionary<string, List<MockAccount>> Tenants { get; set; } = new();
    /// <summary>Referencias que contengan este texto son rechazadas por el "backend" (para pruebas).</summary>
    public string DeclineReferencesContaining { get; set; } = "DECLINE";
}

public sealed class MockAccount
{
    public string AccountRef { get; set; } = "";
    public string BankAccountId { get; set; } = "";
    public string Currency { get; set; } = "COP";
    public string DisplayName { get; set; } = "";
}

public sealed class HttpBackendOptions
{
    public string BaseUrl { get; set; } = "";
    public OutboundAuthOptions Auth { get; set; } = new();
    public OutboundHttpOptions Http { get; set; } = new();
    /// <summary>Secreto para firmar (HMAC) las notificaciones salientes; el backend verifica que vienen de nosotros.</summary>
    public string? SigningSecretName { get; set; } = "backend/notify-signing-key";
    public BackendPaths Paths { get; set; } = new();
}

public sealed class BackendPaths
{
    public string TenantAccounts { get; set; } = "/v1/tenants/{tenantId}/accounts";
    public string AuthorizePayment { get; set; } = "/v1/payments/authorize";
    public string PaymentEvents { get; set; } = "/v1/payments/events";
    public string Health { get; set; } = "/health";
}
