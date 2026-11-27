using ApiGatewayPay.Core.Common;

namespace ApiGatewayPay.Core.Ports.Security;

/// <summary>
/// Acceso a secretos (Vault, AWS/Azure, archivos montados...). Los nombres son relativos al entorno:
/// la infraestructura antepone "sandbox/" o "production/" y rechaza nombres de otro entorno.
/// </summary>
public interface ISecretProvider
{
    ValueTask<string> GetAsync(string name, CancellationToken ct);
}

public class SecretNotFoundException(string name) : Exception($"Secreto no disponible: {name}");

/// <summary>Cifrado a nivel de campo con datos asociados (purpose) que atan el texto cifrado a su contexto.</summary>
public interface IFieldProtector
{
    ValueTask<string> ProtectAsync(string plaintext, string purpose, CancellationToken ct);

    ValueTask<string> UnprotectAsync(string ciphertext, string purpose, CancellationToken ct);

    /// <summary>Huella determinística (HMAC) para búsquedas/deduplicación sin descifrar.</summary>
    ValueTask<string> FingerprintAsync(string value, string purpose, CancellationToken ct);
}

public sealed record KillSwitchState(bool Active, string? Reason, string? ChangedBy, DateTime? ChangedAt, bool FromConfiguration);

/// <summary>Palanca de emergencia: desactiva de inmediato todo tráfico saliente hacia el banco.</summary>
public interface IKillSwitch
{
    Task<KillSwitchState> GetAsync(CancellationToken ct);

    Task SetAsync(bool active, string reason, AuditActor actor, CancellationToken ct);
}
