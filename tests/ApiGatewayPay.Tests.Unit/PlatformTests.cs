using System.Reflection;
using ApiGatewayPay.Infrastructure.Platform;
using Microsoft.Extensions.Configuration;

namespace ApiGatewayPay.Tests.Unit;

public class EnvironmentGuardTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ValidProduction() => new()
    {
        ["Platform:Environment"] = "Production",
        ["Bank:Adapter"] = "Real",
        ["Bank:Real:BaseUrl"] = "https://api.banco.com",
        ["Backend:Adapter"] = "Http",
        ["Backend:Http:BaseUrl"] = "https://backend.empresa.com",
        ["Auth:Mode"] = "Oidc",
        ["Secrets:Provider"] = "Vault",
        ["Persistence:Provider"] = "SqlServer",
        ["Security:RequireHttps"] = "true",
    };

    [Fact]
    public void Valid_production_configuration_passes() => Assert.Empty(EnvironmentGuard.Validate(Config(ValidProduction())));

    [Theory]
    [InlineData("Bank:Adapter", "Mock")]
    [InlineData("Backend:Adapter", "Mock")]
    [InlineData("Auth:Mode", "Local")]
    [InlineData("Secrets:Provider", "Config")]
    [InlineData("Persistence:Provider", "Sqlite")]
    [InlineData("Persistence:AutoCreateSchema", "true")]
    [InlineData("Security:RequireHttps", "false")]
    [InlineData("Bank:Real:BaseUrl", "https://api.sandbox.banco.com")]
    [InlineData("Bank:Real:BaseUrl", "http://api.banco.com")]
    [InlineData("Bank:Real:Http:AllowPrivateNetworks", "true")]
    [InlineData("DevSecrets:production/x", "leaked")]
    [InlineData("ExternalServices:mail:Enabled", "true")]
    public void Production_rejects_insecure_or_mixed_settings(string key, string value)
    {
        var cfg = ValidProduction();
        cfg[key] = value;
        if (key.StartsWith("ExternalServices", StringComparison.Ordinal)) cfg["ExternalServices:mail:Type"] = "email-console";
        Assert.NotEmpty(EnvironmentGuard.Validate(Config(cfg)));
    }

    [Fact]
    public void Sandbox_cannot_point_to_a_production_bank_host()
    {
        var errors = EnvironmentGuard.Validate(Config(new()
        {
            ["Platform:Environment"] = "Sandbox",
            ["Bank:Adapter"] = "Real",
            ["Bank:Real:BaseUrl"] = "https://api.banco.com",
            ["Platform:ProductionBankHosts:0"] = "api.banco.com",
        }));
        Assert.Single(errors);
    }

    [Fact]
    public void EnsureValid_throws_and_lists_all_problems()
    {
        var cfg = ValidProduction();
        cfg["Bank:Adapter"] = "Mock";
        cfg["Auth:Mode"] = "Local";
        var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentGuard.EnsureValid(Config(cfg)));
        Assert.Contains("Bank:Adapter", ex.Message);
        Assert.Contains("Auth:Mode", ex.Message);
    }
}

public class ArchitectureTests
{
    /// <summary>Regla hexagonal: el núcleo no depende de infraestructura, ASP.NET, EF Core ni clientes HTTP concretos.</summary>
    [Fact]
    public void Core_does_not_depend_on_adapters_or_frameworks()
    {
        var core = typeof(Core.CoreServiceCollectionExtensions).Assembly;
        var forbidden = new[] { "ApiGatewayPay.Infrastructure", "ApiGatewayPay.Api", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Polly", "Microsoft.Data" };
        var references = core.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, r => forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_external_service_type_is_discoverable()
    {
        var types = Infrastructure.External.ExternalServiceDiscovery.DiscoverTypes(typeof(Infrastructure.InfrastructureServiceCollectionExtensions).Assembly);
        Assert.Contains("email-console", types.Keys);
        Assert.Contains("signed-webhook", types.Keys);
        Assert.Contains("sms-gateway", types.Keys);
    }
}
