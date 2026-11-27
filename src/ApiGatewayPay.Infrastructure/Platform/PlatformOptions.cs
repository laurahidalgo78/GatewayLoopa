namespace ApiGatewayPay.Infrastructure.Platform;

public enum PlatformEnvironment
{
    Sandbox,
    Production,
}

public sealed class PlatformOptions
{
    public const string Section = "Platform";

    /// <summary>Entorno lógico de negocio (independiente de ASPNETCORE_ENVIRONMENT).</summary>
    public PlatformEnvironment Environment { get; set; } = PlatformEnvironment.Sandbox;

    public string SecretPrefix => Environment == PlatformEnvironment.Production ? "production/" : "sandbox/";

    public string ApiKeyPrefix => Environment == PlatformEnvironment.Production ? "agp_live_" : "agp_test_";
}
