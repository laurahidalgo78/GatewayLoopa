namespace ApiGatewayPay.Api.Security;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Oidc (producción: IdP externo) | Local (solo desarrollo/sandbox: emisor integrado).</summary>
    public string Mode { get; set; } = "Oidc";
    public OidcOptions Oidc { get; set; } = new();
    public ClaimNames Claims { get; set; } = new();
    public StepUpOptions StepUp { get; set; } = new();
    public LocalAuthOptions Local { get; set; } = new();
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string Audience { get; set; } = "apigatewaypay";
    public bool RequireHttpsMetadata { get; set; } = true;
}

public sealed class ClaimNames
{
    public string Tenant { get; set; } = "tid";
    public string Scope { get; set; } = "scope";
}

/// <summary>Autenticación reforzada (RFC 9470): MFA reciente para operaciones sensibles.</summary>
public sealed class StepUpOptions
{
    public List<string> AcceptedAcr { get; set; } = ["urn:agp:loa:mfa"];
    public List<string> AcceptedAmr { get; set; } = ["mfa", "otp", "hwk", "swk"];
    public int MaxAgeSeconds { get; set; } = 300;
    public string RequiredAcr { get; set; } = "urn:agp:loa:mfa";
}

public sealed class LocalAuthOptions
{
    public string Issuer { get; set; } = "https://localhost/agp-dev";
    public string Audience { get; set; } = "apigatewaypay";
    public int AccessTokenMinutes { get; set; } = 5;
    public int RefreshTokenMinutes { get; set; } = 30;
    public int SessionHours { get; set; } = 8;
    public string? SigningKeySecretName { get; set; }
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public List<LocalUser> Users { get; set; } = [];
}

public sealed class LocalUser
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string TotpSecretName { get; set; } = "";
    public string TenantId { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

public static class Scopes
{
    public const string PaymentsRead = "payments:read";
    public const string PaymentsWrite = "payments:write";
    public const string PaymentsApprove = "payments:approve";
    public const string BeneficiariesRead = "beneficiaries:read";
    public const string BeneficiariesWrite = "beneficiaries:write";
    public const string BeneficiariesApprove = "beneficiaries:approve";
    public const string AccountsRead = "accounts:read";
    public const string AdminApiKeys = "admin:apikeys";
    public const string AdminAudit = "admin:audit";
    public const string PlatformKillSwitch = "platform:killswitch";
    public const string PlatformReconciliation = "platform:reconciliation";

    public static readonly string[] All =
    [
        PaymentsRead, PaymentsWrite, PaymentsApprove, BeneficiariesRead, BeneficiariesWrite, BeneficiariesApprove,
        AccountsRead, AdminApiKeys, AdminAudit, PlatformKillSwitch, PlatformReconciliation,
    ];
}
