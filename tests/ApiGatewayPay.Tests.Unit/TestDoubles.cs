using ApiGatewayPay.Core.Ports.Security;
using Microsoft.Extensions.Options;

namespace ApiGatewayPay.Tests.Unit;

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class DictionarySecrets(Dictionary<string, string> values) : ISecretProvider
{
    public ValueTask<string> GetAsync(string name, CancellationToken ct) =>
        values.TryGetValue(name, out var v) ? ValueTask.FromResult(v) : throw new SecretNotFoundException(name);
}

public sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
