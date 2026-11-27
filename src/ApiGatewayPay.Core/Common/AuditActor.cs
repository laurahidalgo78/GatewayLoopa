namespace ApiGatewayPay.Core.Common;

public enum ActorType
{
    User,
    ApiKey,
    /// <summary>Cliente máquina autenticado por el IdP (OAuth2 client credentials).</summary>
    Machine,
    System,
    Bank,
}

/// <summary>
/// Quién ejecuta una acción, desde dónde y con qué fuerza de autenticación. Viaja explícitamente
/// hasta el libro de auditoría: los casos de uso nunca leen el contexto HTTP.
/// </summary>
public sealed record AuditActor(
    string ActorId,
    ActorType Type,
    string TenantId,
    string? SourceIp,
    string? UserAgent,
    string CorrelationId,
    bool StrongAuthentication)
{
    public static AuditActor System(string tenantId, string correlationId) =>
        new("system", ActorType.System, tenantId, null, null, correlationId, false);

    public static AuditActor Bank(string bankName, string tenantId, string correlationId) =>
        new($"bank:{bankName}", ActorType.Bank, tenantId, null, null, correlationId, false);

    public bool IsHuman => Type == ActorType.User;
}
