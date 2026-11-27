using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using ApiGatewayPay.Infrastructure.Bank.Tls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace ApiGatewayPay.Infrastructure.Http;

public static class HardenedHttpClientExtensions
{
    public const string RetryableOption = "agp.retryable";
    public static readonly HttpRequestOptionsKey<bool> Retryable = new(RetryableOption);

    /// <summary>
    /// Cliente saliente endurecido: lista blanca de hosts, anti-SSRF por socket, TLS 1.2/1.3, sin redirecciones,
    /// sin cookies, mTLS y pinning opcionales, timeouts, reintentos con backoff+jitter SOLO para operaciones
    /// idempotentes, y circuit breaker.
    /// </summary>
    public static IHttpClientBuilder AddHardenedHttpClient(this IServiceCollection services, string name, OutboundHttpOptions options)
    {
        if (options.ClientCertificate is { IsConfigured: true } certificate)
            services.AddSingleton(new CertificateRegistration(name, certificate));
        var builder = services.AddHttpClient(name, c =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan; // los timeouts los gestiona el pipeline de resiliencia
                c.MaxResponseContentBufferSize = options.MaxResponseBytes;
                c.DefaultRequestVersion = HttpVersion.Version11;
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var store = sp.GetRequiredService<CertificateStore>();
                var customRoot = options.CustomRootCaSecretName is null ? null : store.GetRootCa(options.CustomRootCaSecretName);
                var pins = options.PinnedSpkiSha256.ToHashSet(StringComparer.Ordinal);

                var ssl = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = options.CheckCertificateRevocation
                        ? System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
                        : System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                        CertificatePinning.Validate(name, cert as System.Security.Cryptography.X509Certificates.X509Certificate2 ?? (cert is null ? null : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(cert.GetRawCertData())),
                            chain, errors, pins, customRoot, store, options.CheckCertificateRevocation),
                };
                if (store.GetClientCertificate(name) is { } clientCert)
                    ssl.ClientCertificates = [clientCert];

                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,            // una redirección es un vector SSRF clásico
                    UseCookies = false,
                    UseProxy = false,                     // el ConnectCallback valida la IP final; con proxy validaría la del proxy
                    AutomaticDecompression = DecompressionMethods.None, // evita bombas de descompresión
                    ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, options.AttemptTimeoutSeconds)),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    MaxResponseHeadersLength = 64,
                    SslOptions = ssl,
                    ConnectCallback = SsrfGuard.CreateConnectCallback(options.AllowPrivateNetworks),
                };
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(2))
            .RemoveAllLoggers()
            .AddHttpMessageHandler(() => new HostAllowlistHandler(name, options))
            .AddHttpMessageHandler(() => new CorrelationPropagationHandler())
            .AddHttpMessageHandler(sp => new OutboundLoggingHandler(name, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Outbound")));

        builder.AddResilienceHandler($"{name}-resilience", pipeline =>
        {
            pipeline.AddTimeout(TimeSpan.FromSeconds(options.TimeoutSeconds));
            if (options.MaxRetries > 0)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(300),
                    ShouldHandle = args =>
                    {
                        var request = args.Context.GetRequestMessage();
                        return ValueTask.FromResult(request is not null && IsRetryable(request) &&
                                                    HttpClientResiliencePredicates.IsTransient(args.Outcome));
                    },
                });
            }
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = options.CircuitBreakerFailureRatio,
                MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, options.AttemptTimeoutSeconds * 2)),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerBreakSeconds),
                OnOpened = _ => { Telemetry.AppMetrics.CircuitOpened(name); return ValueTask.CompletedTask; },
            });
            pipeline.AddTimeout(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
        });
        return builder;
    }

    /// <summary>Copia de la política añadiendo el host de una URL (p. ej. BaseUrl o TokenUrl) a la lista blanca.</summary>
    public static OutboundHttpOptions WithHostsOf(this OutboundHttpOptions o, params string?[] urls)
    {
        var copy = new OutboundHttpOptions
        {
            AllowedHosts = [.. o.AllowedHosts], RequireHttps = o.RequireHttps, AllowPrivateNetworks = o.AllowPrivateNetworks,
            TimeoutSeconds = o.TimeoutSeconds, AttemptTimeoutSeconds = o.AttemptTimeoutSeconds, MaxRetries = o.MaxRetries,
            CircuitBreakerFailureRatio = o.CircuitBreakerFailureRatio, CircuitBreakerMinimumThroughput = o.CircuitBreakerMinimumThroughput,
            CircuitBreakerBreakSeconds = o.CircuitBreakerBreakSeconds, PinnedSpkiSha256 = [.. o.PinnedSpkiSha256],
            CustomRootCaSecretName = o.CustomRootCaSecretName, ClientCertificate = o.ClientCertificate,
            CheckCertificateRevocation = o.CheckCertificateRevocation, MaxResponseBytes = o.MaxResponseBytes,
        };
        foreach (var url in urls)
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !copy.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
                copy.AllowedHosts.Add(uri.IdnHost);
        return copy;
    }

    /// <summary>
    /// Solo se reintenta lo idempotente: GET/HEAD/OPTIONS o peticiones marcadas explícitamente
    /// (p. ej. consultas SOAP vía POST). Un POST de pago NUNCA se reintenta aquí.
    /// </summary>
    public static bool IsRetryable(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || request.Method == HttpMethod.Options ||
        (request.Options.TryGetValue(Retryable, out var flag) && flag);
}
