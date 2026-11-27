using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ApiGatewayPay.Core.Ports.Security;

namespace ApiGatewayPay.Infrastructure.Http;

public sealed class OutboundAuthOptions
{
    /// <summary>None | ApiKey | OAuth2ClientCredentials (mTLS se configura aparte, en Http.ClientCertificate).</summary>
    public string Type { get; set; } = "None";
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    public string? ApiKeySecretName { get; set; }
    public string? TokenUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecretName { get; set; }
    public string? Scope { get; set; }
}

/// <summary>Aplica credenciales de una integración a una petición saliente.</summary>
public interface IOutboundAuthenticator
{
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct);
}

public static class OutboundAuthenticator
{
    public static IOutboundAuthenticator Create(OutboundAuthOptions options, ISecretProvider secrets, IHttpClientFactory httpFactory, string tokenClientName, TimeProvider time) =>
        options.Type switch
        {
            "None" => new NoAuth(),
            "ApiKey" => new ApiKeyAuth(options, secrets),
            "OAuth2ClientCredentials" => new ClientCredentialsAuth(options, secrets, httpFactory, tokenClientName, time),
            _ => throw new InvalidOperationException($"Tipo de autenticación saliente no soportado: {options.Type}"),
        };

    private sealed class NoAuth : IOutboundAuthenticator
    {
        public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ApiKeyAuth(OutboundAuthOptions o, ISecretProvider secrets) : IOutboundAuthenticator
    {
        public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.Headers.TryAddWithoutValidation(o.ApiKeyHeader, await secrets.GetAsync(o.ApiKeySecretName!, ct));
    }

    /// <summary>OAuth2 client credentials con caché del token y protección contra estampidas.</summary>
    private sealed class ClientCredentialsAuth(OutboundAuthOptions o, ISecretProvider secrets, IHttpClientFactory httpFactory, string clientName, TimeProvider time) : IOutboundAuthenticator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _token;
        private DateTimeOffset _expires;

        public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        }

        private async Task<string> GetTokenAsync(CancellationToken ct)
        {
            if (_token is not null && time.GetUtcNow() < _expires) return _token;
            await _lock.WaitAsync(ct);
            try
            {
                if (_token is not null && time.GetUtcNow() < _expires) return _token;
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = o.ClientId!,
                    ["client_secret"] = await secrets.GetAsync(o.ClientSecretName!, ct),
                };
                if (!string.IsNullOrEmpty(o.Scope)) form["scope"] = o.Scope;
                using var content = new FormUrlEncodedContent(form);
                using var response = await httpFactory.CreateClient(clientName).PostAsync(o.TokenUrl, content, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"El endpoint de token respondió {(int)response.StatusCode}.");
                var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                    ?? throw new HttpRequestException("Respuesta de token vacía.");
                if (string.IsNullOrEmpty(token.AccessToken)) throw new HttpRequestException("Respuesta de token sin access_token.");
                _token = token.AccessToken;
                _expires = time.GetUtcNow().AddSeconds(Math.Max(30, token.ExpiresIn - 60));
                return _token;
            }
            finally
            {
                _lock.Release();
            }
        }

        private sealed record TokenResponse(
            [property: JsonPropertyName("access_token")] string AccessToken,
            [property: JsonPropertyName("expires_in")] int ExpiresIn);
    }
}
