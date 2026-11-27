using ApiGatewayPay.Infrastructure.Bank;
using ApiGatewayPay.Infrastructure.Backend;
using ApiGatewayPay.Infrastructure.External;
using ApiGatewayPay.Infrastructure.Http;
using Microsoft.Extensions.Configuration;

namespace ApiGatewayPay.Infrastructure.Platform;

/// <summary>
/// Validación de arranque (fail-fast): la aplicación NO inicia si la configuración mezcla entornos o
/// deja activos componentes de desarrollo en producción. Es la barrera técnica principal contra
/// "credenciales de sandbox en producción" o "mock bancario en producción".
/// </summary>
public static class EnvironmentGuard
{
    private static readonly string[] DefaultSandboxMarkers = ["sandbox", "test", "uat", "qa", "dev", "staging", "localhost", "mock"];

    public static IReadOnlyList<string> Validate(IConfiguration c)
    {
        var errors = new List<string>();
        var platform = c.GetSection(PlatformOptions.Section).Get<PlatformOptions>() ?? new();
        var bank = c.GetSection(BankOptions.Section).Get<BankOptions>() ?? new();
        var backend = c.GetSection(BackendOptions.Section).Get<BackendOptions>() ?? new();
        var sandboxMarkers = c.GetSection("Platform:SandboxHostMarkers").Get<string[]>() ?? DefaultSandboxMarkers;
        var productionBankHosts = c.GetSection("Platform:ProductionBankHosts").Get<string[]>() ?? [];

        if (platform.Environment == PlatformEnvironment.Production)
        {
            Require(errors, bank.Adapter == "Real", "Bank:Adapter debe ser Real en producción.");
            Require(errors, backend.Adapter == "Http", "Backend:Adapter debe ser Http en producción.");
            Require(errors, c["Auth:Mode"] == "Oidc", "Auth:Mode debe ser Oidc en producción (el emisor local es solo para desarrollo).");
            Require(errors, c["Secrets:Provider"] is "Vault" or "File" or "Environment", "Secrets:Provider no puede ser Config en producción.");
            Require(errors, !c.GetSection("DevSecrets").GetChildren().Any(), "La sección DevSecrets debe estar vacía en producción.");
            Require(errors, c["Persistence:Provider"] != "Sqlite", "Persistence:Provider no puede ser Sqlite en producción.");
            Require(errors, !c.GetValue<bool>("Persistence:AutoCreateSchema"), "Persistence:AutoCreateSchema debe ser false en producción (use los scripts de db/).");
            Require(errors, !bank.Mock.Webhooks.Enabled, "Bank:Mock:Webhooks:Enabled debe ser false en producción.");
            Require(errors, c.GetValue("Security:RequireHttps", false), "Security:RequireHttps debe ser true en producción.");

            CheckUrl(errors, "Bank:Real:BaseUrl", bank.Real.BaseUrl, sandboxMarkers);
            CheckUrl(errors, "Backend:Http:BaseUrl", backend.Http.BaseUrl, sandboxMarkers);
            CheckHttp(errors, "Bank:Real:Http", bank.Real.Http);
            CheckHttp(errors, "Backend:Http:Http", backend.Http.Http);
            Require(errors, bank.Real.Http.CheckCertificateRevocation, "Bank:Real:Http:CheckCertificateRevocation debe ser true en producción.");

            foreach (var s in c.GetSection(ExternalServiceDiscovery.Section).GetChildren())
            {
                var o = s.Get<ExternalServiceOptions>() ?? new();
                if (!o.Enabled) continue;
                Require(errors, !o.Type.Contains("console", StringComparison.OrdinalIgnoreCase) && !o.Type.Contains("mock", StringComparison.OrdinalIgnoreCase),
                    $"ExternalServices:{s.Key}: el tipo {o.Type} es de desarrollo.");
                CheckHttp(errors, $"ExternalServices:{s.Key}:Http", o.Http);
            }
        }
        else if (bank.Adapter == "Real" && Uri.TryCreate(bank.Real.BaseUrl, UriKind.Absolute, out var sandboxUri))
        {
            Require(errors, !productionBankHosts.Contains(sandboxUri.Host, StringComparer.OrdinalIgnoreCase),
                "Bank:Real:BaseUrl apunta a un host bancario de PRODUCCIÓN con Platform:Environment=Sandbox.");
        }
        return errors;
    }

    public static void EnsureValid(IConfiguration configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
            throw new InvalidOperationException("Configuración insegura, la aplicación no arranca:\n - " + string.Join("\n - ", errors));
    }

    private static void CheckUrl(List<string> errors, string key, string url, string[] markers)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{key} debe ser una URL https absoluta.");
            return;
        }
        var labels = uri.Host.Split('.', '-');
        if (labels.Any(l => markers.Contains(l, StringComparer.OrdinalIgnoreCase)))
            errors.Add($"{key} parece un host de sandbox/pruebas ({uri.Host}).");
    }

    private static void CheckHttp(List<string> errors, string key, OutboundHttpOptions o)
    {
        Require(errors, o.RequireHttps, $"{key}:RequireHttps debe ser true en producción.");
        Require(errors, !o.AllowPrivateNetworks, $"{key}:AllowPrivateNetworks debe ser false en producción.");
    }

    private static void Require(List<string> errors, bool condition, string message)
    {
        if (!condition) errors.Add(message);
    }
}
