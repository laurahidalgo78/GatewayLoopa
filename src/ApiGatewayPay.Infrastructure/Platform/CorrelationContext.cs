namespace ApiGatewayPay.Infrastructure.Platform;

/// <summary>Correlation ID del flujo actual (petición HTTP o trabajo de cola), propagado a llamadas salientes.</summary>
public static class CorrelationContext
{
    public const string HeaderName = "X-Correlation-Id";
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Id
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? value) =>
        value is { Length: >= 8 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
