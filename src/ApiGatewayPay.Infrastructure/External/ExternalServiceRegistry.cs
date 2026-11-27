using System.Reflection;
using ApiGatewayPay.Core.Ports.External;
using ApiGatewayPay.Core.Ports.Security;
using ApiGatewayPay.Infrastructure.Http;
using ApiGatewayPay.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiGatewayPay.Infrastructure.External;

/// <summary>Marca una clase como tipo de servicio externo. El valor es el "Type" usado en configuración.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ExternalServiceAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}

/// <summary>Configuración común a toda instancia de servicio externo (ExternalServices:{nombre}).</summary>
public sealed class ExternalServiceOptions
{
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public string? BaseUrl { get; set; }
    public OutboundHttpOptions Http { get; set; } = new();
    public OutboundAuthOptions Auth { get; set; } = new();
}

/// <summary>
/// Lo que recibe cada servicio: SU HttpClient (con SU lista blanca), SUS secretos (solo bajo
/// "services/{nombre}/") y SU sección de configuración. No tiene acceso a credenciales de otras integraciones.
/// </summary>
public sealed record ExternalServiceContext(
    string Name, ExternalServiceOptions Options, IConfigurationSection Settings, HttpClient Http,
    ISecretProvider Secrets, IOutboundAuthenticator Auth, ILogger Logger);

public static class ExternalServiceDiscovery
{
    public const string Section = "ExternalServices";

    public static Dictionary<string, Type> DiscoverTypes(params Assembly[] assemblies) =>
        assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IExternalService).IsAssignableFrom(t))
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<ExternalServiceAttribute>()))
            .Where(x => x.Attr is not null)
            .ToDictionary(x => x.Attr!.Type, x => x.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registra un HttpClient endurecido por cada instancia habilitada. Añadir un servicio nuevo =
    /// crear la clase con [ExternalService("tipo")] + una entrada en ExternalServices:{nombre}.
    /// </summary>
    public static IServiceCollection AddExternalServices(this IServiceCollection services, IConfiguration configuration, params Assembly[] assemblies)
    {
        var types = DiscoverTypes(assemblies);
        var definitions = new List<(string Name, ExternalServiceOptions Options, Type Type)>();
        foreach (var section in configuration.GetSection(Section).GetChildren())
        {
            var options = section.Get<ExternalServiceOptions>() ?? new();
            if (!options.Enabled) continue;
            if (!types.TryGetValue(options.Type, out var type))
                throw new InvalidOperationException($"ExternalServices:{section.Key}: tipo '{options.Type}' no encontrado.");
            if (options.BaseUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && options.Http.AllowedHosts.Count == 0)
                options.Http.AllowedHosts.Add(uri.IdnHost);
            services.AddHardenedHttpClient(ClientName(section.Key), options.Http);
            definitions.Add((section.Key, options, type));
        }

        services.AddSingleton<IExternalServiceRegistry>(sp => new ExternalServiceRegistry(definitions.Select(d =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>();
            var secrets = new PrefixedSecretProvider(sp.GetRequiredService<ISecretProvider>(), $"services/{d.Name}");
            var context = new ExternalServiceContext(d.Name, d.Options, configuration.GetSection($"{Section}:{d.Name}:Settings"),
                http.CreateClient(ClientName(d.Name)), secrets,
                OutboundAuthenticator.Create(d.Options.Auth, secrets, http, ClientName(d.Name), sp.GetRequiredService<TimeProvider>()),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ExternalService.{d.Name}"));
            return (IExternalService)ActivatorUtilities.CreateInstance(sp, d.Type, context);
        }).ToList()));
        return services;
    }

    public static string ClientName(string serviceName) => "ext:" + serviceName;
}

public sealed class ExternalServiceRegistry(IReadOnlyList<IExternalService> services) : IExternalServiceRegistry
{
    private readonly Dictionary<string, IExternalService> _byName = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IExternalService> All => _byName.Values;

    public bool TryGet(string name, out IExternalService service) => _byName.TryGetValue(name, out service!);

    public IExternalService Get(string name) =>
        _byName.TryGetValue(name, out var s) ? s : throw new KeyNotFoundException($"Servicio externo no configurado o deshabilitado: {name}");

    public IEnumerable<IExternalService> OfKind(ExternalServiceKind kind) => _byName.Values.Where(s => s.Kind == kind);
}

/// <summary>Alertas: siempre al log estructurado + métricas; además a todo servicio de tipo Alerting habilitado.</summary>
public sealed class AlertNotifier(IExternalServiceRegistry registry, ILogger<AlertNotifier> logger) : IAlertNotifier
{
    public async Task NotifyAsync(Alert alert, CancellationToken ct)
    {
        Telemetry.AppMetrics.Alert(alert.Code, alert.Severity.ToString());
        logger.Log(alert.Severity >= AlertSeverity.High ? LogLevel.Error : LogLevel.Warning,
            "ALERTA {Severity} {AlertCode}: {AlertMessage}", alert.Severity, alert.Code, alert.Message);
        foreach (var service in registry.OfKind(ExternalServiceKind.Alerting))
        {
            try
            {
                var parameters = new Dictionary<string, string>(alert.Data ?? new Dictionary<string, string>())
                {
                    ["severity"] = alert.Severity.ToString(), ["code"] = alert.Code, ["message"] = alert.Message,
                };
                await service.ExecuteAsync(new ExternalServiceRequest("alert", parameters), ct);
            }
            catch (Exception ex)
            {
                // Una alerta que no se entrega no debe tumbar el flujo de negocio.
                logger.LogWarning("No se pudo entregar la alerta vía {Service}: {ErrorType}", service.Name, ex.GetType().Name);
            }
        }
    }
}
