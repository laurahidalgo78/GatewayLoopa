namespace ApiGatewayPay.Core.Ports.External;

public enum ExternalServiceKind { Email, Sms, Kyc, Crm, PaymentGateway, Alerting, Other }

/// <summary>
/// Interfaz común para servicios externos (correo, SMS, KYC, CRM, pasarelas...). Cada instancia
/// configurada tiene su propio HttpClient, lista blanca de hosts y espacio de secretos.
/// </summary>
public interface IExternalService
{
    /// <summary>Nombre de la instancia en configuración (p. ej. "alerts", "sms-principal").</summary>
    string Name { get; }

    ExternalServiceKind Kind { get; }

    Task<ExternalServiceResult> ExecuteAsync(ExternalServiceRequest request, CancellationToken ct);

    Task<bool> CheckHealthAsync(CancellationToken ct);
}

/// <summary>Parámetros como texto plano: evita deserializar objetos arbitrarios entre capas.</summary>
public sealed record ExternalServiceRequest(string Operation, IReadOnlyDictionary<string, string> Parameters);

public sealed record ExternalServiceResult(
    bool Success, string? ProviderReference = null, string? ErrorCode = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public static ExternalServiceResult Ok(string? reference = null) => new(true, reference);
    public static ExternalServiceResult Fail(string errorCode) => new(false, ErrorCode: errorCode);
}

public interface IExternalServiceRegistry
{
    IReadOnlyCollection<IExternalService> All { get; }

    bool TryGet(string name, out IExternalService service);

    IExternalService Get(string name);

    IEnumerable<IExternalService> OfKind(ExternalServiceKind kind);
}

public enum AlertSeverity { Info, Warning, High, Critical }

public sealed record Alert(AlertSeverity Severity, string Code, string Message, IReadOnlyDictionary<string, string>? Data = null);

public interface IAlertNotifier
{
    Task NotifyAsync(Alert alert, CancellationToken ct);
}
